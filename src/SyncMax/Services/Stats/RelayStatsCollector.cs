using System.Collections.Concurrent;
using SyncMax.Models;

namespace SyncMax.Services.Stats;

/// <summary>
/// Накопитель статистики пересылки в ОЗУ. Пересылка сообщения не должна ждать базу:
/// SQLite держит одну блокировку записи на всю базу, и транзакция на каждое сообщение
/// поставила бы пересылку в очередь саму к себе. Поэтому показатели копятся здесь, а
/// на диск уходят пачкой раз в несколько минут (<see cref="RelayStatsFlushService"/>).
///
/// <see cref="Record"/> не делает ни ввода-вывода, ни блокировок: <c>GetOrAdd</c> плюс
/// <c>Interlocked.Add</c> по счётчикам. Соответственно потеря данных возможна — при
/// аварийном завершении процесса теряется всё, что накоплено с прошлой выгрузки. Это
/// осознанный размен: статистика не расчётная величина, а справочная.
/// </summary>
public sealed class RelayStatsCollector
{
    private readonly ConcurrentDictionary<StatsKey, Counters> _buckets = new();

    /// <summary>Учитывает одну успешную пересылку. Вызывается из потока пересылки, ничего не ждёт.</summary>
    public void Record(long accountId, long chatLinkId, RepostDirection direction, RelayStatsDelta delta)
    {
        var key = new StatsKey(accountId, chatLinkId, Today(), direction);
        var counters = _buckets.GetOrAdd(key, _ => new Counters());

        Interlocked.Add(ref counters.Messages, delta.Messages);
        Interlocked.Add(ref counters.TextBytes, delta.TextBytes);
        Interlocked.Add(ref counters.PhotoCount, delta.PhotoCount);
        Interlocked.Add(ref counters.PhotoBytes, delta.PhotoBytes);
        Interlocked.Add(ref counters.VideoCount, delta.VideoCount);
        Interlocked.Add(ref counters.VideoBytes, delta.VideoBytes);
        Interlocked.Add(ref counters.AudioCount, delta.AudioCount);
        Interlocked.Add(ref counters.AudioBytes, delta.AudioBytes);
        Interlocked.Add(ref counters.FileCount, delta.FileCount);
        Interlocked.Add(ref counters.FileBytes, delta.FileBytes);
    }

    /// <summary>
    /// Снимает накопленное, обнуляя счётчики. Каждое значение снимается
    /// <c>Interlocked.Exchange</c>, а не подменой всего словаря: при подмене всё, что
    /// конкурентный поток успел прибавить между чтением ссылки и заменой, ушло бы в
    /// выброшенную копию. Здесь же прибавка либо попадает в снимаемое значение, либо
    /// остаётся в счётчике и уедет следующей выгрузкой.
    /// </summary>
    public IReadOnlyList<RelayStatsRow> Drain()
    {
        var today = Today();
        var rows = new List<RelayStatsRow>();
        var now = DateTimeOffset.UtcNow.ToString("o");

        foreach (var (key, counters) in _buckets)
        {
            var row = new RelayStatsRow
            {
                AccountId = key.AccountId,
                ChatLinkId = key.ChatLinkId,
                Day = key.Day,
                Direction = key.Direction.ToCode(),
                Messages = Interlocked.Exchange(ref counters.Messages, 0),
                TextBytes = Interlocked.Exchange(ref counters.TextBytes, 0),
                PhotoCount = Interlocked.Exchange(ref counters.PhotoCount, 0),
                PhotoBytes = Interlocked.Exchange(ref counters.PhotoBytes, 0),
                VideoCount = Interlocked.Exchange(ref counters.VideoCount, 0),
                VideoBytes = Interlocked.Exchange(ref counters.VideoBytes, 0),
                AudioCount = Interlocked.Exchange(ref counters.AudioCount, 0),
                AudioBytes = Interlocked.Exchange(ref counters.AudioBytes, 0),
                FileCount = Interlocked.Exchange(ref counters.FileCount, 0),
                FileBytes = Interlocked.Exchange(ref counters.FileBytes, 0),
                UpdatedAt = now
            };

            if (!row.IsEmpty)
            {
                rows.Add(row);
                continue;
            }

            // Пустой ключ прошедшего дня больше не наполнится — в него пишут только
            // сутки, к которым он относится, — и его можно убрать, чтобы словарь не рос
            // день за днём. Ключи сегодняшнего дня остаются: их ровно по числу активных
            // связок, зато нет гонки с потоком, который пишет в них прямо сейчас.
            if (key.Day != today)
                _buckets.TryRemove(key, out _);
        }

        return rows;
    }

    /// <summary>
    /// Возвращает снятые показатели обратно — если записать их не удалось. Без этого
    /// единственная неудачная транзакция (база занята, диск недоступен) означала бы
    /// потерю периода, хотя данные ещё целы и следующая попытка через несколько минут.
    /// </summary>
    public void Restore(IEnumerable<RelayStatsRow> rows)
    {
        foreach (var row in rows)
        {
            Record(row.AccountId, row.ChatLinkId, RepostDirectionExtensions.FromCode(row.Direction),
                new RelayStatsDelta
                {
                    Messages = row.Messages,
                    TextBytes = row.TextBytes,
                    PhotoCount = row.PhotoCount,
                    PhotoBytes = row.PhotoBytes,
                    VideoCount = row.VideoCount,
                    VideoBytes = row.VideoBytes,
                    AudioCount = row.AudioCount,
                    AudioBytes = row.AudioBytes,
                    FileCount = row.FileCount,
                    FileBytes = row.FileBytes
                });
        }
    }

    /// <summary>Текущие сутки в UTC — как и все остальные отметки времени в проекте.</summary>
    private static string Today() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

    /// <summary>
    /// Ключ ведра. День входит в ключ, поэтому смена суток сама заводит новое ведро,
    /// и выгрузке не нужно знать, где проходит граница.
    /// </summary>
    private readonly record struct StatsKey(long AccountId, long ChatLinkId, string Day, RepostDirection Direction);

    /// <summary>
    /// Счётчики одного ведра. Именно поля, а не свойства: <c>Interlocked</c> работает
    /// со ссылкой на переменную, а свойство её не даёт.
    /// </summary>
    private sealed class Counters
    {
        public long Messages;
        public long TextBytes;
        public long PhotoCount;
        public long PhotoBytes;
        public long VideoCount;
        public long VideoBytes;
        public long AudioCount;
        public long AudioBytes;
        public long FileCount;
        public long FileBytes;
    }
}
