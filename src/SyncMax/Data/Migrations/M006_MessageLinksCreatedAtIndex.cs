using Microsoft.Data.Sqlite;

namespace SyncMax.Data.Migrations;

/// <summary>
/// Индекс по дате вставки в <c>message_links</c>. У таблицы были только индексы по сторонам
/// пересылки (<c>M004</c>), а фоновая уборка (<see cref="Services.MessageLinkCleanupService"/>)
/// отбирает записи именно по <c>created_at</c> — без индекса каждый её проход означал бы
/// полный перебор таблицы, которая растёт по записи на каждое пересланное сообщение.
/// </summary>
public sealed class M006_MessageLinksCreatedAtIndex : IMigration
{
    public int Version => 6;

    public string Name => "Message links created_at index";

    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "CREATE INDEX ix_message_links_created_at ON message_links (created_at);";
        cmd.ExecuteNonQuery();
    }
}
