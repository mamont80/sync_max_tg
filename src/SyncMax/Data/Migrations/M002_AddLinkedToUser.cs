using Microsoft.Data.Sqlite;

namespace SyncMax.Data.Migrations;

/// <summary>
/// Добавляет колонку связки в существующие БД, созданные до того, как
/// <see cref="M001_InitialSchema"/> обзавёлся колонкой linked_to_user
/// (её добавили в тот же класс задним числом, версия схемы не менялась —
/// поэтому на старых БД колонки физически нет).
/// </summary>
public sealed class M002_AddLinkedToUser : IMigration
{
    public int Version => 2;

    public string Name => "Add users.linked_to_user";

    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;

        var hasColumn = false;
        cmd.CommandText = "PRAGMA table_info(users);";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "linked_to_user", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
            return;

        cmd.CommandText = "ALTER TABLE users ADD COLUMN linked_to_user TEXT;";
        cmd.ExecuteNonQuery();
    }
}
