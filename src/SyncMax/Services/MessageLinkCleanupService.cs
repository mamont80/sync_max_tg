using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Data.Repositories;

namespace SyncMax.Services;

/// <summary>
/// Фоновая уборка карты «оригинал ↔ копия» (<c>message_links</c>): удаляет записи старше
/// <see cref="CleanupOptions.MessageLinkRetentionDays"/> суток.
///
/// Таблица растёт по записи на каждое пересланное сообщение и сама по себе не
/// самоочищается: записи убираются только при удалении сообщения или связки. Смысл
/// хранить их дольше двух недель невелик — карта нужна, пока на сообщение ещё могут
/// ответить или его отредактировать.
///
/// Удаление идёт **партиями с паузами**, а не одним запросом: в ту же таблицу в это время
/// пишет пересылка сообщений, и разовый <c>DELETE</c> по десяткам тысяч строк держал бы
/// блокировку записи (в SQLite она одна на всю базу) заметное время. Маленькие транзакции
/// с паузами между ними освобождают базу после каждой партии.
/// </summary>
public sealed class MessageLinkCleanupService : BackgroundService
{
    private readonly MessageLinkRepository _messageLinks;
    private readonly CleanupOptions _options;
    private readonly ILogger<MessageLinkCleanupService> _logger;

    public MessageLinkCleanupService(
        MessageLinkRepository messageLinks,
        IOptions<CleanupOptions> options,
        ILogger<MessageLinkCleanupService> logger)
    {
        _messageLinks = messageLinks;
        _options = options.Value;
        _logger = logger;
    }

    private int RetentionDays => _options.MessageLinkRetentionDays;

    // Значения из конфигурации приводим к разумным: нулевой размер партии зациклил бы
    // проход впустую, а нулевые паузы превратили бы уборку в непрерывную нагрузку.
    private int BatchSize => Math.Max(1, _options.BatchSize);

    private TimeSpan BatchPause => TimeSpan.FromSeconds(Math.Max(1, _options.BatchPauseSeconds));

    private TimeSpan IdlePause => TimeSpan.FromMinutes(Math.Max(1, _options.IdlePauseMinutes));

    private TimeSpan StartupDelay => TimeSpan.FromSeconds(Math.Max(0, _options.StartupDelaySeconds));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (RetentionDays <= 0)
        {
            _logger.LogInformation(
                "Уборка message_links отключена (Cleanup:MessageLinkRetentionDays = {Days}).", RetentionDays);
            return;
        }

        _logger.LogInformation(
            "Уборка message_links включена: храним {Days} сут., партиями по {BatchSize} с паузой {Pause} c.",
            RetentionDays, BatchSize, BatchPause.TotalSeconds);

        if (!await DelayAsync(StartupDelay, ct))
            return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Уборка не критична: ошибку логируем и пробуем в следующий раз.
                _logger.LogError(ex, "Уборка message_links: ошибка прохода, повтор через {Pause} мин.",
                    IdlePause.TotalMinutes);
            }

            if (!await DelayAsync(IdlePause, ct))
                break;
        }
    }

    /// <summary>
    /// Один проход: удаляет просроченные записи партиями, пока они не кончатся.
    /// Признак «партия кончилась» — удалено меньше, чем просили: значит подходящих
    /// записей больше нет.
    /// </summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        // Порог считаем один раз на проход: сдвигать его в процессе незачем, а так
        // объём работы прохода предсказуем и он гарантированно завершится.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            var removed = await _messageLinks.DeleteOlderThanAsync(cutoff, BatchSize, ct);
            total += removed;

            if (removed < BatchSize)
                break;

            if (!await DelayAsync(BatchPause, ct))
                break;
        }

        // Логируем итог прохода, а не каждую партию: иначе при первой уборке большой
        // базы лог заполнится сотнями одинаковых строк.
        if (total > 0)
        {
            _logger.LogInformation("Уборка message_links: удалено {Count} записей старше {Cutoff:u}.",
                total, cutoff);
        }
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
