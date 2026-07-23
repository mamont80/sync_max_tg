using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SyncMax.Data.Migrations;

/// <summary>
/// Прогоняет невыполненные миграции при старте приложения.
/// Текущая версия схемы хранится в заголовке файла БД (<c>PRAGMA user_version</c>),
/// что не требует отдельной служебной таблицы.
/// </summary>
public sealed class MigrationRunner
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IReadOnlyList<IMigration> _migrations;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(
        SqliteConnectionFactory factory,
        IEnumerable<IMigration> migrations,
        ILogger<MigrationRunner> logger)
    {
        _factory = factory;
        _migrations = migrations.OrderBy(m => m.Version).ToList();
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var connection = await _factory.CreateOpenAsync(ct);

        var current = GetUserVersion(connection);
        _logger.LogInformation("Текущая версия БД: {Version}. Доступно миграций: {Count}.",
            current, _migrations.Count);

        foreach (var migration in _migrations)
        {
            if (migration.Version <= current)
                continue;

            _logger.LogInformation("Применяю миграцию #{Version} ({Name})...",
                migration.Version, migration.Name);

            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            migration.Up(connection, tx);
            // user_version не принимает параметры — значение берём из нашего int, инъекция исключена.
            ExecuteNonQuery(connection, tx, $"PRAGMA user_version = {migration.Version};");
            await tx.CommitAsync(ct);

            current = migration.Version;
        }

        _logger.LogInformation("БД в актуальном состоянии, версия: {Version}.", current);
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
