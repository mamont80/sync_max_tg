using Microsoft.Data.Sqlite;

namespace SyncMax.Data.Migrations;

/// <summary>
/// Названия чатов по сторонам связки. До этого было только общее <c>title</c> вида
/// «чат1 &lt;=&gt; чат2», причём порядок в нём зависит от того, кто из двоих сделал репост
/// первым, — то есть по нему нельзя понять, какая сторона MAX, а какая Telegram.
/// Мини-приложению это нужно: без такого разделения переключатель направления
/// (MAX → TG / TG → MAX) не с чем сопоставить.
///
/// Колонки nullable: у связок, созданных раньше, названия сторон взять неоткуда —
/// интерфейс в этом случае откатывается на общее <c>title</c>.
/// </summary>
public sealed class M005_ChatLinkTitles : IMigration
{
    public int Version => 5;

    public string Name => "Chat link side titles";

    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        Exec(connection, transaction, "ALTER TABLE chat_links ADD COLUMN max_chat_title TEXT;");
        Exec(connection, transaction, "ALTER TABLE chat_links ADD COLUMN tg_chat_title TEXT;");
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
