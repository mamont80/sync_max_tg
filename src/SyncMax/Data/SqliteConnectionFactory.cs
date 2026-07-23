using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;

namespace SyncMax.Data;

/// <summary>Создаёт новые подключения к SQLite по строке из конфигурации.</summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(IOptions<DatabaseOptions> options)
    {
        _connectionString = options.Value.ConnectionString;
    }

    public SqliteConnection Create() => new(_connectionString);

    public async Task<SqliteConnection> CreateOpenAsync(CancellationToken ct)
    {
        var connection = Create();
        await connection.OpenAsync(ct);
        // Включаем внешние ключи и WAL — важно для параллельной работы двух ботов.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        return connection;
    }
}
