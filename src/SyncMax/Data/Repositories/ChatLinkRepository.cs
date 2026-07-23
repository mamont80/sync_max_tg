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
                 active, title, repost_type, created_at)
            VALUES
                (@MaxChatId, @MaxChatType, @MaxUserId, @TgChatId, @TgChatType, @TgUserId,
                 1, @Title, @RepostType, @CreatedAt);
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
}
