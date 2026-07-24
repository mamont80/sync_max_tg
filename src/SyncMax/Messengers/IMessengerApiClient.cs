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
    /// Отправка сообщения (текст и/или медиа-вложения) в групповой чат/канал по его id —
    /// для пересылки между связанными чатами. Каждый клиент кодирует содержимое по-своему
    /// (Telegram — entities + родные Send*, MAX — markdown + upload-flow вложений), исходя
    /// из платформо-независимого <see cref="RelayMessage"/>. Вложения читаются из временных
    /// файлов на диске (см. <see cref="MediaAttachment.FilePath"/>). При наличии
    /// <see cref="RelayMessage.ReplyToTargetMessageId"/> первое сообщение оформляется как ответ.
    /// Возвращает id первого отправленного сообщения (для карты ответов) или null.
    /// </summary>
    Task<string?> SendChatMessageAsync(string chatId, RelayMessage message, CancellationToken ct);
}
