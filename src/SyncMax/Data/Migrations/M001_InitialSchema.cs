using Microsoft.Data.Sqlite;

namespace SyncMax.Data.Migrations;

/// <summary>Начальная схема: одна таблица пользователей. Связка хранится прямо в ней.</summary>
public sealed class M001_InitialSchema : IMigration
{
    public int Version => 1;

    public string Name => "Initial schema (users)";

    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Первичный ключ — пара (user_id, messenger): идентификаторы MAX и Telegram
        // лежат в разных пространствах и могут численно совпасть.
        // linked_to_user хранит user_id связанного аккаунта в другом мессенджере
        // и заполняется у обеих сторон связки.
        Exec(connection, transaction,
            """
            CREATE TABLE users (
                user_id              TEXT    NOT NULL,
                messenger            TEXT    NOT NULL,          -- 'tg' | 'max'
                registered_at        TEXT    NOT NULL,
                is_active            INTEGER NOT NULL DEFAULT 1,
                name                 TEXT,
                language             TEXT    NOT NULL DEFAULT 'ru',
                link_code            TEXT,
                link_code_created_at TEXT,
                linked_to_user       TEXT,
                PRIMARY KEY (user_id, messenger)
            );
            """);

        // Индекс по коду связки — по нему ищем "владельца" кода при вводе во втором мессенджере.
        Exec(connection, transaction,
            "CREATE INDEX ix_users_link_code ON users (link_code);");
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
