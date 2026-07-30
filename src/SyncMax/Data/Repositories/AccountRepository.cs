using Dapper;
using SyncMax.Models;

namespace SyncMax.Data.Repositories;

/// <summary>
/// Чтение аккаунтов. Создание и удаление живут в <see cref="UserRepository"/>
/// (<c>LinkAccountsAsync</c> / <c>UnlinkAsync</c>): аккаунт заводится и убирается
/// только вместе со связкой пользователей, одной транзакцией на обе стороны, —
/// отдельная точка входа для этого позволяла бы оставить связку без аккаунта
/// или аккаунт без связки.
/// </summary>
public sealed class AccountRepository
{
    private readonly SqliteConnectionFactory _factory;

    public AccountRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<Account?> GetAsync(long id, CancellationToken ct)
    {
        await using var conn = await _factory.CreateOpenAsync(ct);
        const string sql = "SELECT * FROM accounts WHERE id = @id LIMIT 1;";
        return await conn.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(sql,
            new { id }, cancellationToken: ct));
    }
}
