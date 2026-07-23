using Microsoft.Data.Sqlite;

namespace SyncMax.Data.Migrations;

/// <summary>
/// Второй этап: связки чатов/каналов между MAX и Telegram, плюс поля в users
/// для отслеживания "ожидающего" выбора чата (репост сделан одной стороной,
/// ждём вторую).
/// </summary>
public sealed class M003_ChatLinks : IMigration
{
    public int Version => 3;

    public string Name => "Chat links";

    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        Exec(connection, transaction,
            """
            CREATE TABLE chat_links (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                max_chat_id   TEXT    NOT NULL,
                max_chat_type TEXT    NOT NULL,          -- 'chat' | 'channel'
                max_user_id   TEXT    NOT NULL,
                tg_chat_id    TEXT    NOT NULL,
                tg_chat_type  TEXT    NOT NULL,          -- 'chat' | 'channel'
                tg_user_id    TEXT    NOT NULL,
                active        INTEGER NOT NULL DEFAULT 1,
                title         TEXT    NOT NULL,
                repost_type   TEXT    NOT NULL,          -- 'max_to_tg' | 'tg_to_max' | 'both'
                created_at    TEXT    NOT NULL,
                UNIQUE (max_chat_id, tg_chat_id)
            );
            """);

        Exec(connection, transaction,
            "ALTER TABLE users ADD COLUMN linking_chat_id TEXT;");
        Exec(connection, transaction,
            "ALTER TABLE users ADD COLUMN linking_chat_type TEXT;");
        Exec(connection, transaction,
            "ALTER TABLE users ADD COLUMN linking_chat_title TEXT;");
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
