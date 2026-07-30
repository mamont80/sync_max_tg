using Dapper;
using Microsoft.Data.Sqlite;
using SyncMax.Models;

namespace SyncMax.Data.Repositories;

/// <summary>
/// Запись и чтение статистики пересылки (<c>relay_stats</c>). Пишет сюда только фоновая
/// выгрузка накопителя (<see cref="Services.Stats.RelayStatsFlushService"/>) — пачкой и
/// одной транзакцией.
/// </summary>
public sealed class RelayStatsRepository
{
    private readonly SqliteConnectionFactory _factory;

    public RelayStatsRepository(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Прибавляет накопленное к тому, что уже лежит в БД, — одной транзакцией на всю пачку.
    ///
    /// Именно прибавляет: строка суток заводится первой выгрузкой, а каждая следующая
    /// увеличивает её счётчики (<c>ON CONFLICT ... DO UPDATE SET messages = messages +
    /// excluded.messages</c>). Перезапись здесь была бы ошибкой — накопитель отдаёт
    /// прирост с прошлой выгрузки, а не итог за сутки.
    /// </summary>
    public async Task UpsertBatchAsync(IReadOnlyList<RelayStatsRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return;

        await using var conn = await _factory.CreateOpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        const string sql =
            """
            INSERT INTO relay_stats
                (account_id, chat_link_id, day, direction, messages, text_bytes,
                 photo_count, photo_bytes, video_count, video_bytes,
                 audio_count, audio_bytes, file_count, file_bytes, updated_at)
            VALUES
                (@AccountId, @ChatLinkId, @Day, @Direction, @Messages, @TextBytes,
                 @PhotoCount, @PhotoBytes, @VideoCount, @VideoBytes,
                 @AudioCount, @AudioBytes, @FileCount, @FileBytes, @UpdatedAt)
            ON CONFLICT (account_id, chat_link_id, day, direction) DO UPDATE SET
                messages    = messages    + excluded.messages,
                text_bytes  = text_bytes  + excluded.text_bytes,
                photo_count = photo_count + excluded.photo_count,
                photo_bytes = photo_bytes + excluded.photo_bytes,
                video_count = video_count + excluded.video_count,
                video_bytes = video_bytes + excluded.video_bytes,
                audio_count = audio_count + excluded.audio_count,
                audio_bytes = audio_bytes + excluded.audio_bytes,
                file_count  = file_count  + excluded.file_count,
                file_bytes  = file_bytes  + excluded.file_bytes,
                updated_at  = excluded.updated_at;
            """;

        await conn.ExecuteAsync(new CommandDefinition(sql, rows, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>Итог по аккаунту за всё время, с разбивкой по видам вложений и направлениям.</summary>
    public async Task<RelayStatsTotals> GetTotalsAsync(long accountId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            $"""
             SELECT
                 COALESCE(SUM(messages), 0)    AS Messages,
                 COALESCE(SUM({BytesSum}), 0)  AS Bytes,
                 COALESCE(SUM(CASE WHEN direction = '{RepostDirectionExtensions.MaxToTgCode}'
                                   THEN messages ELSE 0 END), 0) AS MaxToTg,
                 COALESCE(SUM(CASE WHEN direction = '{RepostDirectionExtensions.TgToMaxCode}'
                                   THEN messages ELSE 0 END), 0) AS TgToMax,
                 COALESCE(SUM(photo_count), 0) AS PhotoCount,
                 COALESCE(SUM(photo_bytes), 0) AS PhotoBytes,
                 COALESCE(SUM(video_count), 0) AS VideoCount,
                 COALESCE(SUM(video_bytes), 0) AS VideoBytes,
                 COALESCE(SUM(audio_count), 0) AS AudioCount,
                 COALESCE(SUM(audio_bytes), 0) AS AudioBytes,
                 COALESCE(SUM(file_count), 0)  AS FileCount,
                 COALESCE(SUM(file_bytes), 0)  AS FileBytes,
                 COALESCE(SUM(text_bytes), 0)  AS TextBytes
             FROM relay_stats
             WHERE account_id = @accountId;
             """;
        return await conn.QuerySingleAsync<RelayStatsTotals>(new CommandDefinition(sql,
            new { accountId }, cancellationToken: ct));
    }

    /// <summary>
    /// Последние <paramref name="limit"/> суток с активностью, свежие сверху. Дни без
    /// пересылки строк не имеют и в выдачу не попадают — «дырки» дорисовывает интерфейс,
    /// он же знает, какой период показывает.
    /// </summary>
    public Task<IReadOnlyList<RelayStatsPeriod>> GetDailyAsync(long accountId, int limit, CancellationToken ct) =>
        GetPeriodsAsync(accountId, "day", limit, ct);

    /// <summary>Последние <paramref name="limit"/> месяцев с активностью, свежие сверху.</summary>
    public Task<IReadOnlyList<RelayStatsPeriod>> GetMonthlyAsync(long accountId, int limit, CancellationToken ct) =>
        GetPeriodsAsync(accountId, "substr(day, 1, 7)", limit, ct);

    /// <summary>
    /// Итоги по связкам чатов за всё время. Связка могла быть удалена — тогда названия
    /// нет, а её вклад в сумму аккаунта остаётся (см. миграцию M009), поэтому LEFT JOIN.
    /// </summary>
    public async Task<IReadOnlyList<RelayStatsLink>> GetByLinksAsync(long accountId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            $"""
             SELECT
                 s.chat_link_id             AS ChatLinkId,
                 c.title                    AS Title,
                 SUM(s.messages)            AS Messages,
                 SUM({BytesSumPrefixed})    AS Bytes
             FROM relay_stats s
             LEFT JOIN chat_links c ON c.id = s.chat_link_id
             WHERE s.account_id = @accountId
             GROUP BY s.chat_link_id, c.title
             ORDER BY Messages DESC;
             """;
        var rows = await conn.QueryAsync<RelayStatsLink>(new CommandDefinition(sql,
            new { accountId }, cancellationToken: ct));
        return rows.AsList();
    }

    private async Task<IReadOnlyList<RelayStatsPeriod>> GetPeriodsAsync(
        long accountId, string periodExpression, int limit, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);

        // periodExpression — не пользовательский ввод, а константа вызывающего метода
        // (сутки либо их первые 7 символов), поэтому подстановка в текст запроса безопасна.
        var sql =
            $"""
             SELECT
                 {periodExpression}           AS Period,
                 SUM(messages)                AS Messages,
                 SUM({BytesSum})              AS Bytes,
                 SUM(CASE WHEN direction = '{RepostDirectionExtensions.MaxToTgCode}'
                          THEN messages ELSE 0 END) AS MaxToTg,
                 SUM(CASE WHEN direction = '{RepostDirectionExtensions.TgToMaxCode}'
                          THEN messages ELSE 0 END) AS TgToMax
             FROM relay_stats
             WHERE account_id = @accountId
             GROUP BY Period
             ORDER BY Period DESC
             LIMIT @limit;
             """;
        var rows = await conn.QueryAsync<RelayStatsPeriod>(new CommandDefinition(sql,
            new { accountId, limit }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Весь перенесённый объём: текст плюс все виды вложений.</summary>
    private const string BytesSum = "text_bytes + photo_bytes + video_bytes + audio_bytes + file_bytes";

    private const string BytesSumPrefixed =
        "s.text_bytes + s.photo_bytes + s.video_bytes + s.audio_bytes + s.file_bytes";
}
