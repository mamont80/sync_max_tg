using Microsoft.Data.Sqlite;

namespace SyncMax.Data.Migrations;

/// <summary>
/// Статистика пересылки: сколько сообщений и байт перенесено, по дням, связкам чатов
/// и направлениям.
///
/// Хранятся только ДНЕВНЫЕ строки: месяцы и суммы за всё время считаются из них
/// (<c>GROUP BY substr(day, 1, 7)</c>). Отдельные таблицы месяцев и итогов — это три
/// источника правды, обязанных сходиться между собой; рано или поздно они расходятся,
/// а выигрыш нулевой — строк здесь единицы на связку в день.
///
/// <c>chat_link_id</c> намеренно без внешнего ключа: с <c>ON DELETE SET NULL</c> он
/// сломал бы первичный ключ, так как в SQLite NULL не равны друг другу — строки
/// удалённых связок перестали бы схлопываться по <c>ON CONFLICT</c> и полезли бы
/// дублями. Поэтому id хранится как историческое значение: связку могли удалить, но
/// её вклад в сумму по аккаунту остаётся. Уборку обеспечивает каскад по
/// <c>account_id</c> — разрыв связки аккаунтов уносит и статистику.
/// </summary>
public sealed class M009_RelayStats : IMigration
{
    public int Version => 9;

    public string Name => "Relay stats";

    public void Up(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            CREATE TABLE relay_stats (
                account_id   INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                chat_link_id INTEGER NOT NULL,
                day          TEXT    NOT NULL,          -- 'YYYY-MM-DD', UTC
                direction    TEXT    NOT NULL,          -- 'max_to_tg' | 'tg_to_max'
                messages     INTEGER NOT NULL DEFAULT 0,
                text_bytes   INTEGER NOT NULL DEFAULT 0,
                photo_count  INTEGER NOT NULL DEFAULT 0,
                photo_bytes  INTEGER NOT NULL DEFAULT 0,
                video_count  INTEGER NOT NULL DEFAULT 0,
                video_bytes  INTEGER NOT NULL DEFAULT 0,
                audio_count  INTEGER NOT NULL DEFAULT 0,
                audio_bytes  INTEGER NOT NULL DEFAULT 0,
                file_count   INTEGER NOT NULL DEFAULT 0,
                file_bytes   INTEGER NOT NULL DEFAULT 0,
                updated_at   TEXT    NOT NULL,
                PRIMARY KEY (account_id, chat_link_id, day, direction)
            );
            """;
        cmd.ExecuteNonQuery();

        // Отдельный индекс по (account_id, day) не нужен: первичный ключ начинается с
        // account_id, и выборка за период сначала сужается по нему до считанных строк.
    }
}
