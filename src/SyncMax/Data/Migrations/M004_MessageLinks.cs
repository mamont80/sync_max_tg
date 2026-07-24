using Microsoft.Data.Sqlite;

namespace SyncMax.Data.Migrations;

/// <summary>
/// Карта соответствия сообщений между чатами: для каждого пересланного сообщения хранится
/// пара «id оригинала ↔ id пересланной копии» (с обеих сторон). Нужна для переноса ответов
/// (reply): если сообщение — ответ на R, находим по карте копию R в целевом чате и оформляем
/// пересланную копию как ответ на неё.
/// </summary>
public sealed class M004_MessageLinks : IMigration
{
    public int Version => 4;

    public string Name => "Message links (reply mapping)";

    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        Exec(connection, transaction,
            """
            CREATE TABLE message_links (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                max_chat_id TEXT NOT NULL,
                max_msg_id  TEXT NOT NULL,          -- mid сообщения в MAX
                tg_chat_id  TEXT NOT NULL,
                tg_msg_id   TEXT NOT NULL,          -- message_id сообщения в Telegram
                created_at  TEXT NOT NULL
            );
            """);

        // Поиск по любой из сторон (в обе стороны пересылки).
        Exec(connection, transaction,
            "CREATE INDEX ix_message_links_max ON message_links (max_chat_id, max_msg_id);");
        Exec(connection, transaction,
            "CREATE INDEX ix_message_links_tg ON message_links (tg_chat_id, tg_msg_id);");
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
