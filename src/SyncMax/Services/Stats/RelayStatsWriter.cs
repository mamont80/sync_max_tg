using Microsoft.Extensions.Logging;
using SyncMax.Data.Repositories;

namespace SyncMax.Services.Stats;

/// <summary>
/// Переносит накопленное в <see cref="RelayStatsCollector"/> в БД. Вынесено отдельно от
/// фоновой выгрузки, потому что вызывающих двое: <see cref="RelayStatsFlushService"/>
/// по расписанию и экран статистики мини-приложения — перед чтением.
///
/// Экран обязан звать выгрузку сам: между двумя фоновыми проходами показатели живут
/// только в ОЗУ, и без этого пользователь, переславший сообщение минуту назад, видел бы
/// пустой экран и делал вывод, что статистика не работает. Дорого это не обходится —
/// экран открывают несравнимо реже, чем пересылают сообщения, а горячий путь пересылки
/// по-прежнему не пишет в БД вообще.
/// </summary>
public sealed class RelayStatsWriter
{
    private readonly RelayStatsCollector _collector;
    private readonly RelayStatsRepository _stats;
    private readonly ILogger<RelayStatsWriter> _logger;

    public RelayStatsWriter(
        RelayStatsCollector collector, RelayStatsRepository stats, ILogger<RelayStatsWriter> logger)
    {
        _collector = collector;
        _stats = stats;
        _logger = logger;
    }

    /// <summary>
    /// Записывает накопленное одной транзакцией. Не бросает: статистика не должна ронять
    /// ни фоновый цикл, ни экран, ради которого её выгружают.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var rows = _collector.Drain();
        if (rows.Count == 0)
            return;

        try
        {
            await _stats.UpsertBatchAsync(rows, ct);
            _logger.LogInformation("Статистика пересылки: записано строк {Count}.", rows.Count);
        }
        catch (Exception ex)
        {
            // Возвращаем в накопитель: данные ещё целы, а следующая попытка — через интервал.
            _collector.Restore(rows);
            _logger.LogError(ex, "Статистика пересылки: не удалось записать {Count} строк, отложено до следующего прохода.",
                rows.Count);
        }
    }
}
