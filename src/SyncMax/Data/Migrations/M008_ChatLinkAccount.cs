using Microsoft.Data.Sqlite;

namespace SyncMax.Data.Migrations;

/// <summary>
/// Ссылка связки чатов на аккаунт пары, которой она принадлежит. Нужна статистике
/// (<see cref="M009_RelayStats"/>): пересылка знает только связку, а копить показатели
/// надо по аккаунту, и резолвить его на каждом сообщении через <c>users</c> означало бы
/// лишний запрос к БД на горячем пути.
///
/// <c>ON DELETE SET NULL</c>, а не <c>CASCADE</c>: связки чатов удаляются явно —
/// командой <c>/clear</c> и мини-приложением, — и превращать разрыв связки аккаунтов
/// в неявное удаление чужой сущности значило бы менять уже сложившееся поведение.
/// </summary>
public sealed class M008_ChatLinkAccount : IMigration
{
    public int Version => 8;

    public string Name => "Chat link -> account";

    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        Exec(connection, transaction,
            "ALTER TABLE chat_links ADD COLUMN account_id INTEGER REFERENCES accounts(id) ON DELETE SET NULL;");

        Exec(connection, transaction,
            "CREATE INDEX ix_chat_links_account_id ON chat_links (account_id);");

        // Существующим связкам аккаунт проставляем по той стороне, чей пользователь его имеет.
        // Сначала по MAX, затем по Telegram для оставшихся: у связки, созданной парой, обе
        // стороны ведут к одному аккаунту, а если сторона MAX успела отвязаться — сгодится
        // вторая. Не нашлось ни там, ни там — связка остаётся без аккаунта, и статистика по
        // ней просто не пишется (см. MessageRelayService).
        Exec(connection, transaction,
            """
            UPDATE chat_links
            SET account_id = (
                SELECT u.account_id FROM users u
                WHERE u.messenger = 'max' AND u.user_id = chat_links.max_user_id
            )
            WHERE account_id IS NULL;
            """);

        Exec(connection, transaction,
            """
            UPDATE chat_links
            SET account_id = (
                SELECT u.account_id FROM users u
                WHERE u.messenger = 'tg' AND u.user_id = chat_links.tg_user_id
            )
            WHERE account_id IS NULL;
            """);
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
