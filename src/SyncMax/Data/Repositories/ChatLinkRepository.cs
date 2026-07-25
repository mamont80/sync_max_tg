using Dapper;
using SyncMax.Models;

namespace SyncMax.Data.Repositories;

public sealed class ChatLinkRepository
{
    private readonly SqliteConnectionFactory _factory;

    public ChatLinkRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<bool> ExistsAsync(string maxChatId, string tgChatId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            "SELECT 1 FROM chat_links WHERE max_chat_id = @maxChatId AND tg_chat_id = @tgChatId LIMIT 1;";
        var row = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(sql,
            new { maxChatId, tgChatId }, cancellationToken: ct));
        return row.HasValue;
    }

    public async Task CreateAsync(ChatLink link, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            INSERT INTO chat_links
                (max_chat_id, max_chat_type, max_user_id, tg_chat_id, tg_chat_type, tg_user_id,
                 active, title, max_chat_title, tg_chat_title, repost_type, created_at)
            VALUES
                (@MaxChatId, @MaxChatType, @MaxUserId, @TgChatId, @TgChatType, @TgUserId,
                 1, @Title, @MaxChatTitle, @TgChatTitle, @RepostType, @CreatedAt);
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, link, cancellationToken: ct));
    }

    /// <summary>Удаляет все связки чатов. Используется командами /deleteAllLinks и /clear.</summary>
    public async Task DeleteAllAsync(CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM chat_links;", cancellationToken: ct));
    }

    /// <summary>
    /// Ищет активную связку по чату <paramref name="chatId"/> в мессенджере <paramref name="messenger"/> —
    /// для пересылки входящего сообщения в связанный чат второго мессенджера.
    /// </summary>
    public async Task<ChatLink?> FindActiveByChatAsync(MessengerType messenger, string chatId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        var column = messenger == MessengerType.Max ? "max_chat_id" : "tg_chat_id";
        var sql = $"SELECT * FROM chat_links WHERE {column} = @chatId AND active = 1 LIMIT 1;";
        return await conn.QuerySingleOrDefaultAsync<ChatLink>(new CommandDefinition(sql,
            new { chatId }, cancellationToken: ct));
    }

    /// <summary>
    /// Связки пары связанных аккаунтов. Идентификаторы передаются по сторонам, а не одним
    /// списком: id из MAX и из Telegram живут в разных пространствах, и сравнивать
    /// «любой мой id с любой колонкой» значило бы допускать случайные совпадения.
    /// Любая из сторон может быть null — тогда условие по ней не сработает (сравнение
    /// с NULL в SQL не истинно), что и нужно для не связанного ещё аккаунта.
    /// </summary>
    public async Task<IReadOnlyList<ChatLink>> ListForUserAsync(
        string? maxUserId, string? tgUserId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            SELECT * FROM chat_links
            WHERE max_user_id = @maxUserId OR tg_user_id = @tgUserId
            ORDER BY created_at DESC, id DESC;
            """;
        var rows = await conn.QueryAsync<ChatLink>(new CommandDefinition(sql,
            new { maxUserId, tgUserId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<ChatLink?> GetByIdAsync(long id, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql = "SELECT * FROM chat_links WHERE id = @id LIMIT 1;";
        return await conn.QuerySingleOrDefaultAsync<ChatLink>(new CommandDefinition(sql,
            new { id }, cancellationToken: ct));
    }

    /// <summary>Включает/выключает связку. У выключенной не переносится ничего, включая правки.</summary>
    public async Task SetActiveAsync(long id, bool active, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql = "UPDATE chat_links SET active = @active WHERE id = @id;";
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { id, active = active ? 1 : 0 }, cancellationToken: ct));
    }

    /// <summary>
    /// Меняет направление пересылки. <paramref name="direction"/> — значение перечисления,
    /// а не произвольная строка: в колонку попадает только код из
    /// <see cref="RepostDirectionExtensions.ToCode"/>.
    /// </summary>
    public async Task SetDirectionAsync(long id, RepostDirection direction, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql = "UPDATE chat_links SET repost_type = @repostType WHERE id = @id;";
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { id, repostType = direction.ToCode() }, cancellationToken: ct));
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM chat_links WHERE id = @id;",
            new { id }, cancellationToken: ct));
    }
}
