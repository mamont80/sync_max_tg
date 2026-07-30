namespace SyncMax.Models;

/// <summary>
/// Аккаунт — пара связанных между собой пользователей (по одному в каждом мессенджере),
/// то есть одна физическая персона. Существует ровно столько, сколько существует связка:
/// создаётся при её создании и удаляется при разрыве
/// (<see cref="Data.Repositories.UserRepository.LinkAccountsAsync"/> /
/// <see cref="Data.Repositories.UserRepository.UnlinkAsync"/>).
///
/// Своих данных у аккаунта пока нет — он нужен как общая точка привязки для всего,
/// что относится к паре целиком, а не к отдельному мессенджеру (статистика, подписка).
/// </summary>
public sealed class Account
{
    public long Id { get; set; }

    /// <summary>Момент создания связки (ISO-8601).</summary>
    public string CreatedAt { get; set; } = string.Empty;
}
