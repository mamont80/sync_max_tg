using SyncMax.Models;

namespace SyncMax.Messengers;

/// <summary>
/// Общий контракт клиента API мессенджера: отправка текста, изображений, файлов,
/// видео и прочего контента пользователю. Реализуется каждым платформенным клиентом
/// (<c>MaxApiClient</c>, <c>TelegramApiClient</c>).
///
/// Не отвечает за приём/обработку входящих сообщений — этим занимается
/// соответствующий <c>*BotService</c> (long polling), который сам зависит от клиента,
/// а не наоборот. <see cref="Services.LinkingService"/> работает только через этот
/// интерфейс и ничего не знает о *BotService.
/// </summary>
public interface IMessengerApiClient
{
    MessengerType Messenger { get; }

    /// <summary>Токен/учётные данные заданы — клиент готов к работе.</summary>
    bool IsConfigured { get; }

    Task SendTextAsync(string userId, string text, CancellationToken ct);

    /// <summary>
    /// Отправка форматированного текста в групповой чат/канал по его id — для пересылки
    /// между связанными чатами. Разметку каждый клиент кодирует по-своему (Telegram —
    /// entities, MAX — markdown), исходя из платформо-независимого <see cref="FormattedText"/>.
    /// </summary>
    Task SendChatTextAsync(string chatId, FormattedText content, CancellationToken ct);

    // Изображения/файлы/видео добавятся сюда по мере реализации в конкретных
    // клиентах (для MAX пока не описан upload-flow вложений в Bot API).
}
