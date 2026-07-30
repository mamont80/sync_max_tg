using Microsoft.Data.Sqlite;

namespace SyncMax.Data.Migrations;

/// <summary>
/// Переключатель функции «видео из ссылок» (<see cref="Services.VideoEmbed.VideoEmbedRelayService"/>)
/// per-связке. <c>DEFAULT 1</c> — фича включена по умолчанию у всех существующих и новых связок;
/// выключить её можно только явно, из мини-приложения.
/// </summary>
public sealed class M010_ChatLinkVideoEmbed : IMigration
{
    public int Version => 10;

    public string Name => "Chat link video embed toggle";

    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "ALTER TABLE chat_links ADD COLUMN video_embed_enabled INTEGER NOT NULL DEFAULT 1;";
        cmd.ExecuteNonQuery();
    }
}
