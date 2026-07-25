using Dapper;
using SyncMax.Models;

namespace SyncMax.Data.Repositories;

/// <summary>
/// Карта соответствия «сообщение-оригинал ↔ пересланная копия» (см. миграцию M004).
/// Одна запись на каждую пересылку, заполнены обе стороны (MAX и Telegram), поэтому запрос
/// по колонкам исходной платформы всегда находит id копии в целевой — карта работает в обе
/// стороны одной таблицей независимо от того, куда именно разрешено пересылать.
/// Записи здесь ничего не разрешают сами по себе: право на перенос правки/удаления решает
/// связка в <see cref="ChatLinkRepository"/> (активность + направление).
/// </summary>
public sealed class MessageLinkRepository
{
    private readonly SqliteConnectionFactory _factory;

    public MessageLinkRepository(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>Сохраняет связку id оригинала и пересланной копии.</summary>
    public async Task AddAsync(string maxChatId, string maxMsgId, string tgChatId, string tgMsgId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            INSERT INTO message_links (max_chat_id, max_msg_id, tg_chat_id, tg_msg_id, created_at)
            VALUES (@maxChatId, @maxMsgId, @tgChatId, @tgMsgId, @createdAt);
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            maxChatId,
            maxMsgId,
            tgChatId,
            tgMsgId,
            createdAt = DateTimeOffset.UtcNow.ToString("o")
        }, cancellationToken: ct));
    }

    /// <summary>
    /// По сообщению <paramref name="sourceMsgId"/> в чате <paramref name="sourceChatId"/>
    /// мессенджера <paramref name="sourceMessenger"/> возвращает соответствующую копию
    /// (chatId, msgId) в другом мессенджере, либо null, если связки нет.
    /// </summary>
    public async Task<(string ChatId, string MsgId)?> FindCounterpartAsync(
        MessengerType sourceMessenger, string sourceChatId, string sourceMsgId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        var sql = sourceMessenger == MessengerType.Max
            ? "SELECT tg_chat_id AS ChatId, tg_msg_id AS MsgId FROM message_links WHERE max_chat_id = @chatId AND max_msg_id = @msgId ORDER BY id DESC LIMIT 1;"
            : "SELECT max_chat_id AS ChatId, max_msg_id AS MsgId FROM message_links WHERE tg_chat_id = @chatId AND tg_msg_id = @msgId ORDER BY id DESC LIMIT 1;";

        var row = await conn.QuerySingleOrDefaultAsync<CounterpartRow>(new CommandDefinition(sql,
            new { chatId = sourceChatId, msgId = sourceMsgId }, cancellationToken: ct));

        return row is null ? null : (row.ChatId, row.MsgId);
    }

    /// <summary>Удаляет из карты записи для указанного сообщения (после его удаления) — уборка.</summary>
    public async Task RemoveAsync(MessengerType messenger, string chatId, string msgId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        var sql = messenger == MessengerType.Max
            ? "DELETE FROM message_links WHERE max_chat_id = @chatId AND max_msg_id = @msgId;"
            : "DELETE FROM message_links WHERE tg_chat_id = @chatId AND tg_msg_id = @msgId;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { chatId, msgId }, cancellationToken: ct));
    }

    /// <summary>
    /// Удаляет всю карту пары чатов — вызывается при удалении связки. Без этого записи
    /// от несуществующей связки копились бы в таблице навсегда: сами по себе прав на
    /// перенос они не дают, но и смысла в них больше нет.
    /// </summary>
    public async Task DeleteByChatPairAsync(string maxChatId, string tgChatId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql = "DELETE FROM message_links WHERE max_chat_id = @maxChatId AND tg_chat_id = @tgChatId;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { maxChatId, tgChatId }, cancellationToken: ct));
    }

    /// <summary>
    /// Удаляет не более <paramref name="batchSize"/> записей старше <paramref name="olderThan"/>
    /// и возвращает, сколько удалено. Ограничение размера — главное здесь: одиночный
    /// <c>DELETE</c> по всем просроченным записям держал бы блокировку записи на всю таблицу,
    /// а пересылка сообщений в это время пишет в неё же. Отбор идёт подзапросом с
    /// <c>LIMIT</c>, потому что в SQLite у самого <c>DELETE</c> ограничения по количеству
    /// нет (если только сборка не собрана с SQLITE_ENABLE_UPDATE_DELETE_LIMIT).
    ///
    /// Сравнение дат строковое: <c>created_at</c> у всех записей пишется одним форматом
    /// (<c>DateTimeOffset.ToString("o")</c> в UTC), а он лексикографически упорядочен.
    ///
    /// Сортировка именно по <c>created_at</c>, а не по <c>id</c>: по нему есть индекс
    /// (<c>M006</c>), и подзапрос обслуживается покрывающим поиском по нему. С <c>ORDER BY id</c>
    /// планировщик SQLite вместо этого сканирует таблицу по rowid — пока просроченные записи
    /// есть, это незаметно (они в начале таблицы), но каждый холостой проход перебирал бы
    /// таблицу целиком, ничего не найдя.
    /// </summary>
    public async Task<int> DeleteOlderThanAsync(DateTimeOffset olderThan, int batchSize, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            DELETE FROM message_links
            WHERE id IN (
                SELECT id FROM message_links
                WHERE created_at < @cutoff
                ORDER BY created_at
                LIMIT @batchSize
            );
            """;
        return await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            cutoff = olderThan.ToUniversalTime().ToString("o"),
            batchSize
        }, cancellationToken: ct));
    }

    private sealed class CounterpartRow
    {
        public string ChatId { get; set; } = string.Empty;
        public string MsgId { get; set; } = string.Empty;
    }
}
