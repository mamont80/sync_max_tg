using Dapper;
using SyncMax.Models;

namespace SyncMax.Data.Repositories;

public sealed class UserRepository
{
    private readonly SqliteConnectionFactory _factory;

    public UserRepository(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Создаёт пользователя, если его ещё нет; иначе обновляет имя и активирует.
    /// registered_at выставляется только при первичной вставке.
    /// </summary>
    public async Task UpsertAsync(
        MessengerType messenger, string userId, string? name, string defaultLanguage, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            INSERT INTO users (user_id, messenger, registered_at, is_active, name, language)
            VALUES (@userId, @messenger, @registeredAt, 1, @name, @language)
            ON CONFLICT (user_id, messenger) DO UPDATE SET
                name = COALESCE(excluded.name, users.name),
                is_active = 1;
            """;

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            userId,
            messenger = messenger.ToCode(),
            registeredAt = IsoNow(),
            name,
            language = defaultLanguage
        }, cancellationToken: ct));
    }

    public async Task<User?> GetAsync(MessengerType messenger, string userId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql = "SELECT * FROM users WHERE user_id = @userId AND messenger = @messenger LIMIT 1;";
        return await conn.QuerySingleOrDefaultAsync<User>(new CommandDefinition(sql,
            new { userId, messenger = messenger.ToCode() }, cancellationToken: ct));
    }

    public async Task SetLinkCodeAsync(
        MessengerType messenger, string userId, string code, DateTimeOffset createdAt, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            UPDATE users
            SET link_code = @code, link_code_created_at = @createdAt
            WHERE user_id = @userId AND messenger = @messenger;
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            code,
            createdAt = createdAt.ToString("o"),
            userId,
            messenger = messenger.ToCode()
        }, cancellationToken: ct));
    }

    /// <summary>Ищет активного владельца кода в мессенджере, отличном от указанного.</summary>
    public async Task<User?> FindActiveByCodeAsync(
        string code, MessengerType excludeMessenger, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            SELECT * FROM users
            WHERE link_code = @code AND messenger <> @exclude AND is_active = 1
            LIMIT 1;
            """;
        return await conn.QuerySingleOrDefaultAsync<User>(new CommandDefinition(sql,
            new { code, exclude = excludeMessenger.ToCode() }, cancellationToken: ct));
    }

    /// <summary>Записывает связку: <paramref name="linkedToUserId"/> — user_id аккаунта в другом мессенджере.</summary>
    public async Task SetLinkedAsync(
        MessengerType messenger, string userId, string linkedToUserId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            UPDATE users
            SET linked_to_user = @linkedToUser
            WHERE user_id = @userId AND messenger = @messenger;
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            linkedToUser = linkedToUserId,
            userId,
            messenger = messenger.ToCode()
        }, cancellationToken: ct));
    }

    /// <summary>
    /// Записывает (или, при передаче null-ов, очищает) "ожидающий" выбор чата для связки
    /// чатов — репост сообщения из чата/канала, сделанный этим пользователем.
    /// </summary>
    public async Task SetLinkingChatAsync(
        MessengerType messenger, string userId, string? chatId, string? chatType, string? chatTitle, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            UPDATE users
            SET linking_chat_id = @chatId, linking_chat_type = @chatType, linking_chat_title = @chatTitle
            WHERE user_id = @userId AND messenger = @messenger;
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            chatId,
            chatType,
            chatTitle,
            userId,
            messenger = messenger.ToCode()
        }, cancellationToken: ct));
    }

    /// <summary>
    /// Сбрасывает связку аккаунта: связь со вторым мессенджером и "ожидающий"/выбранный
    /// чат для связки чатов. Используется командой /clear.
    /// </summary>
    public async Task ClearLinkAsync(MessengerType messenger, string userId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            UPDATE users
            SET linked_to_user = NULL, linking_chat_id = NULL, linking_chat_type = NULL, linking_chat_title = NULL
            WHERE user_id = @userId AND messenger = @messenger;
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { userId, messenger = messenger.ToCode() }, cancellationToken: ct));
    }

    public async Task ClearCodeAsync(MessengerType messenger, string userId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql =
            """
            UPDATE users
            SET link_code = NULL, link_code_created_at = NULL
            WHERE user_id = @userId AND messenger = @messenger;
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { userId, messenger = messenger.ToCode() }, cancellationToken: ct));
    }

    private static string IsoNow() => DateTimeOffset.UtcNow.ToString("o");
}
