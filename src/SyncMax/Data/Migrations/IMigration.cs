using Microsoft.Data.Sqlite;

namespace SyncMax.Data.Migrations;

/// <summary>
/// Одна миграция схемы БД. Версии применяются по возрастанию;
/// текущая версия хранится в <c>PRAGMA user_version</c>.
/// Чтобы добавить изменение схемы в будущем — создайте новый класс
/// с бОльшим <see cref="Version"/> и зарегистрируйте его в DI.
/// </summary>
public interface IMigration
{
    /// <summary>Номер версии (уникальный, &gt; 0, монотонно растёт).</summary>
    int Version { get; }

    /// <summary>Человекочитаемое имя для логов.</summary>
    string Name { get; }

    /// <summary>Применяет изменения в рамках переданной транзакции.</summary>
    void Up(SqliteConnection connection, SqliteTransaction transaction);
}
