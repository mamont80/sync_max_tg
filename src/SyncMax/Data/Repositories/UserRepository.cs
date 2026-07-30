using Dapper;
using Microsoft.Data.Sqlite;
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

    /// <summary>
    /// Связывает два аккаунта: заводит общий <see cref="Account"/>, проставляет обеим
    /// сторонам ссылку на него и друг на друга и гасит использованные коды связки.
    /// Возвращает id созданного аккаунта.
    ///
    /// Всё это одна транзакция: полусвязка (одна сторона знает о второй, вторая о ней —
    /// нет) или аккаунт без участников выглядят для остального кода как обычное
    /// состояние и молча ломают логику вместо того, чтобы упасть.
    ///
    /// Прежние аккаунты обеих сторон удаляются: связаться, уже будучи связанным,
    /// можно — прежняя пара при этом распадается, и её аккаунт остался бы сиротой.
    /// </summary>
    public async Task<long> LinkAccountsAsync(
        MessengerType messengerA, string userIdA,
        MessengerType messengerB, string userIdB,
        CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await DropAccountOfAsync(conn, tx, messengerA, userIdA, ct);
        await DropAccountOfAsync(conn, tx, messengerB, userIdB, ct);

        const string insert = "INSERT INTO accounts (created_at) VALUES (@createdAt); SELECT last_insert_rowid();";
        var accountId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(insert,
            new { createdAt = IsoNow() }, tx, cancellationToken: ct));

        await SetLinkedSideAsync(conn, tx, messengerA, userIdA, userIdB, accountId, ct);
        await SetLinkedSideAsync(conn, tx, messengerB, userIdB, userIdA, accountId, ct);

        await tx.CommitAsync(ct);
        return accountId;
    }

    /// <summary>
    /// Разрывает связку аккаунтов: удаляет общий <see cref="Account"/> и сбрасывает у ВСЕХ
    /// его участников связь со вторым мессенджером и "ожидающий"/выбранный чат для связки
    /// чатов. Используется командой /clear и мини-приложением.
    ///
    /// Операция симметрична — вызова с одной стороны достаточно, чтобы распалась вся пара.
    /// Иначе было бы непонятно, в какой момент удалять сам аккаунт, и между двумя
    /// половинами разрыва он висел бы на одном пользователе.
    /// </summary>
    public async Task UnlinkAsync(MessengerType messenger, string userId, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        if (!await DropAccountOfAsync(conn, tx, messenger, userId, ct))
        {
            // Аккаунта нет: односторонняя связка, которую не подхватил бэкфил M007.
            // Чистить, кроме своей стороны, всё равно нечего.
            await ClearSideAsync(conn, tx, messenger, userId, ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Удаляет аккаунт пользователя вместе со связкой. false — аккаунта не было.
    /// </summary>
    private static async Task<bool> DropAccountOfAsync(
        SqliteConnection conn, SqliteTransaction tx, MessengerType messenger, string userId, CancellationToken ct)
    {
        const string select = "SELECT account_id FROM users WHERE user_id = @userId AND messenger = @messenger;";
        var accountId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(select,
            new { userId, messenger = messenger.ToCode() }, tx, cancellationToken: ct));

        if (accountId is not { } id)
            return false;

        // Порядок важен: account_id участников обнуляет сама БД (ON DELETE SET NULL),
        // поэтому после DELETE отбирать их по account_id уже не по чему.
        const string clear =
            """
            UPDATE users
            SET linked_to_user = NULL, linking_chat_id = NULL, linking_chat_type = NULL, linking_chat_title = NULL
            WHERE account_id = @id;
            """;
        await conn.ExecuteAsync(new CommandDefinition(clear, new { id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM accounts WHERE id = @id;",
            new { id }, tx, cancellationToken: ct));

        return true;
    }

    private static Task SetLinkedSideAsync(
        SqliteConnection conn, SqliteTransaction tx, MessengerType messenger, string userId,
        string linkedToUserId, long accountId, CancellationToken ct)
    {
        const string sql =
            """
            UPDATE users
            SET linked_to_user = @linkedToUser,
                account_id = @accountId,
                link_code = NULL,
                link_code_created_at = NULL
            WHERE user_id = @userId AND messenger = @messenger;
            """;
        return conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            linkedToUser = linkedToUserId,
            accountId,
            userId,
            messenger = messenger.ToCode()
        }, tx, cancellationToken: ct));
    }

    private static Task ClearSideAsync(
        SqliteConnection conn, SqliteTransaction tx, MessengerType messenger, string userId, CancellationToken ct)
    {
        const string sql =
            """
            UPDATE users
            SET linked_to_user = NULL, linking_chat_id = NULL, linking_chat_type = NULL, linking_chat_title = NULL
            WHERE user_id = @userId AND messenger = @messenger;
            """;
        return conn.ExecuteAsync(new CommandDefinition(sql,
            new { userId, messenger = messenger.ToCode() }, tx, cancellationToken: ct));
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

    private static string IsoNow() => DateTimeOffset.UtcNow.ToString("o");
}
