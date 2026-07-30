using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Data.Repositories;

namespace SyncMax.Services.Stats;

/// <summary>
/// Переносит накопленную в ОЗУ статистику (<see cref="RelayStatsCollector"/>) в БД —
/// раз в <see cref="StatsOptions.FlushIntervalMinutes"/> минут, одной транзакцией на всю
/// пачку. Смысл именно в редкости: запись каждого сообщения отдельной транзакцией
/// заставляла бы пересылку соревноваться за единственную в SQLite блокировку записи.
///
/// Устроен как <see cref="MessageLinkCleanupService"/>: стартовая задержка, цикл,
/// ошибка прохода только логируется. Разница в том, что снятые и не записанные
/// показатели возвращаются в накопитель, а не выбрасываются.
/// </summary>
public sealed class RelayStatsFlushService : BackgroundService
{
    private readonly RelayStatsWriter _writer;
    private readonly StatsOptions _options;
    private readonly ILogger<RelayStatsFlushService> _logger;

    public RelayStatsFlushService(
        RelayStatsWriter writer,
        IOptions<StatsOptions> options,
        ILogger<RelayStatsFlushService> logger)
    {
        _writer = writer;
        _options = options.Value;
        _logger = logger;
    }

    // Как и в уборке, значения из конфигурации приводим к разумным: нулевой интервал
    // превратил бы выгрузку в непрерывную нагрузку на базу.
    private TimeSpan FlushInterval => TimeSpan.FromMinutes(Math.Max(1, _options.FlushIntervalMinutes));

    private TimeSpan StartupDelay => TimeSpan.FromSeconds(Math.Max(0, _options.StartupDelaySeconds));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Статистика пересылки: выгрузка в БД раз в {Minutes} мин.",
            FlushInterval.TotalMinutes);

        if (!await DelayAsync(StartupDelay, ct))
            return;

        while (!ct.IsCancellationRequested)
        {
            await _writer.FlushAsync(ct);

            if (!await DelayAsync(FlushInterval, ct))
                break;
        }
    }

    /// <summary>
    /// Последняя выгрузка при штатной остановке: накопленное с прошлого прохода иначе
    /// пропало бы, а стоит это одну транзакцию.
    ///
    /// Сначала останавливаем цикл (<c>base.StopAsync</c>) — иначе выгрузка пошла бы
    /// параллельно очередному проходу. Токен остановки при этом НЕ передаём: к моменту
    /// вызова он, как правило, уже отменён, и запись упала бы, не начавшись.
    /// </summary>
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        await _writer.FlushAsync(CancellationToken.None);
    }

    /// <summary>Пауза с учётом остановки приложения. false — пора завершаться.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
            return !ct.IsCancellationRequested;

        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
