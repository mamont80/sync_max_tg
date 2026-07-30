using Microsoft.Data.Sqlite;

namespace SyncMax.Data.Migrations;

/// <summary>
/// Аккаунт — общая сущность пары связанных пользователей (MAX + Telegram).
/// Создаётся в момент связки, удаляется при её разрыве; всё, что относится к паре
/// целиком (статистика, в перспективе подписка), ссылается на <c>accounts.id</c>.
///
/// Ссылка <c>users.account_id</c> объявлена с <c>ON DELETE SET NULL</c>: разрыв связки —
/// это удаление одной строки <c>accounts</c>, после которого БД сама обнуляет ссылки
/// у обеих сторон. Таблицы, которые появятся позже, должны ссылаться на аккаунт с
/// <c>ON DELETE CASCADE</c> — тогда тот же самый <c>DELETE</c> уберёт и их записи,
/// и отдельная ручная уборка не понадобится.
/// </summary>
public sealed class M007_Accounts : IMigration
{
    public int Version => 7;

    public string Name => "Accounts";

    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        Exec(connection, transaction,
            """
            CREATE TABLE accounts (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT    NOT NULL
            );
            """);

        // ALTER TABLE ... ADD COLUMN с REFERENCES при включённых внешних ключах
        // допустим только с дефолтом NULL — что нам и нужно.
        Exec(connection, transaction,
            "ALTER TABLE users ADD COLUMN account_id INTEGER REFERENCES accounts(id) ON DELETE SET NULL;");

        // По account_id ищутся все участники аккаунта при его удалении.
        Exec(connection, transaction,
            "CREATE INDEX ix_users_account_id ON users (account_id);");

        Backfill(connection, transaction);
    }

    /// <summary>
    /// Заводит аккаунт каждой паре, связанной до появления этой таблицы.
    /// Учитываются только ВЗАИМНЫЕ связки (A ссылается на B, B — на A, обе записи
    /// существуют): односторонняя ссылка — это следствие сбоя или ручной правки БД,
    /// и делать из неё аккаунт значило бы закрепить испорченное состояние. Такие
    /// записи остаются с <c>account_id IS NULL</c>, разрыв связки их всё равно очистит.
    /// </summary>
    private static void Backfill(SqliteConnection connection, SqliteTransaction transaction)
    {
        var linked = ReadLinkedUsers(connection, transaction);
        var byKey = linked.ToDictionary(u => (u.Messenger, u.UserId));
        var done = new HashSet<(string, string)>();

        foreach (var user in linked)
        {
            if (done.Contains((user.Messenger, user.UserId)))
                continue;

            var otherMessenger = user.Messenger == "max" ? "tg" : "max";
            if (!byKey.TryGetValue((otherMessenger, user.LinkedToUser), out var counterpart))
                continue;

            if (counterpart.LinkedToUser != user.UserId)
                continue;

            // Момента связки в БД нет, а аккаунт заведомо не мог возникнуть раньше,
            // чем зарегистрировалась вторая сторона — берём позднейшую регистрацию.
            var createdAt = string.CompareOrdinal(user.RegisteredAt, counterpart.RegisteredAt) >= 0
                ? user.RegisteredAt
                : counterpart.RegisteredAt;

            var accountId = InsertAccount(connection, transaction, createdAt);
            AssignAccount(connection, transaction, accountId, user.Messenger, user.UserId);
            AssignAccount(connection, transaction, accountId, counterpart.Messenger, counterpart.UserId);

            done.Add((user.Messenger, user.UserId));
            done.Add((counterpart.Messenger, counterpart.UserId));
        }
    }

    private static List<LinkedUser> ReadLinkedUsers(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT user_id, messenger, registered_at, linked_to_user
            FROM users
            WHERE linked_to_user IS NOT NULL;
            """;

        var users = new List<LinkedUser>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            users.Add(new LinkedUser(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));

        return users;
    }

    private static long InsertAccount(SqliteConnection connection, SqliteTransaction transaction, string createdAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO accounts (created_at) VALUES ($createdAt); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$createdAt", createdAt);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void AssignAccount(
        SqliteConnection connection, SqliteTransaction transaction, long accountId, string messenger, string userId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE users SET account_id = $accountId WHERE user_id = $userId AND messenger = $messenger;";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$userId", userId);
        cmd.Parameters.AddWithValue("$messenger", messenger);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed record LinkedUser(string UserId, string Messenger, string RegisteredAt, string LinkedToUser);
}
