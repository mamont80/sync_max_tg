using SyncMax.Models;

namespace SyncMax.Services;

/// <summary>
/// Служебная «шапка» пересланного сообщения. Вынесена из <see cref="MessageRelayService"/>
/// отдельно: это чистое форматирование, не зависящее ни от связок, ни от клиентов, — и
/// проверять его удобнее в отрыве от всей машинерии пересылки.
/// </summary>
public static class RelayHeader
{
    /// <summary>
    /// Собирает «шапку»: "👤 {Имя} · (из MAX)" или "👤 {Имя} · (из TG)". Метка источника
    /// отражает мессенджер, ОТКУДА пересылают, независимо от того, куда.
    ///
    /// <paramref name="modified"/> — текст правил бот (замаскирована брань): читатель должен
    /// понимать, что видит не дословную копию.
    ///
    /// <paramref name="forward"/> — исходное сообщение само было репостом. Тогда добавляется
    /// вторая строка с источником: без неё пересланная копия выглядит как собственное
    /// сообщение отправителя, и понять, что это репост и откуда, невозможно. Название
    /// источника оформляется ссылкой на оригинал, если платформа-источник позволяет её
    /// построить (у Telegram — да, у MAX сведений об источнике нет вовсе).
    /// </summary>
    public static FormattedText Build(
        MessengerType source, string? senderName, bool modified, ForwardOrigin? forward = null)
    {
        var sourceTag = source == MessengerType.Max ? "MAX" : "TG";
        var mark = modified ? " [изменено ботом]" : string.Empty;

        var header = string.IsNullOrWhiteSpace(senderName)
            ? $"👤 · (из {sourceTag}){mark}"
            : $"👤 {senderName} · (из {sourceTag}){mark}";

        if (forward is null)
            return FormattedText.Plain(header);

        // Источник неизвестен (так приходит репост из MAX) — сообщаем хотя бы сам факт.
        if (string.IsNullOrWhiteSpace(forward.Title))
            return FormattedText.Plain($"{header}\n↪️ Переслано из приватного источника");

        var lead = $"{header}\n↪️ Переслано из «";
        var text = $"{lead}{forward.Title}»";

        if (string.IsNullOrWhiteSpace(forward.Url))
            return FormattedText.Plain(text);

        // Ссылкой оформляется только само название — кавычки и слово «Переслано» остаются
        // обычным текстом. Офсет считается в единицах UTF-16, как и везде в TextSpan.
        return new FormattedText
        {
            Text = text,
            Spans = [new TextSpan
            {
                Kind = TextSpanKind.Link,
                Offset = lead.Length,
                Length = forward.Title.Length,
                Url = forward.Url
            }]
        };
    }
}
