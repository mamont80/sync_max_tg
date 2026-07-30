namespace SyncMax.WebApp;

/// <summary>
/// Ответы API мини-приложения. Отдельные DTO, а не модели из <c>Models/</c>: наружу
/// уходит только то, что нужно интерфейсу, без внутренних идентификаторов пользователей
/// и служебных полей.
/// </summary>
public sealed record ProfileResponse
{
    /// <summary>Мессенджер, из которого открыто приложение ("tg"/"max").</summary>
    public required string Messenger { get; init; }

    public string? Name { get; init; }

    /// <summary>Аккаунты двух мессенджеров связаны между собой.</summary>
    public required bool Linked { get; init; }

    /// <summary>Мессенджер связанного аккаунта ("tg"/"max"), либо null.</summary>
    public string? LinkedMessenger { get; init; }

    /// <summary>
    /// Текущий 6-значный код связки — только пока аккаунты не связаны. Показывается
    /// в интерфейсе, чтобы ввести его во втором мессенджере.
    /// </summary>
    public string? LinkCode { get; init; }
}

/// <summary>Связка чатов в том виде, в каком её показывает список.</summary>
public sealed record ChatLinkResponse
{
    public required long Id { get; init; }

    /// <summary>Общее название «чат1 &lt;=&gt; чат2» — заголовок карточки.</summary>
    public required string Title { get; init; }

    /// <summary>Название чата на стороне MAX; null у связок, созданных до миграции M005.</summary>
    public string? MaxTitle { get; init; }

    /// <summary>Название чата на стороне Telegram; null у связок, созданных до миграции M005.</summary>
    public string? TgTitle { get; init; }

    /// <summary>"chat" | "channel" — интерфейс рисует по нему подпись стороны.</summary>
    public required string MaxKind { get; init; }

    public required string TgKind { get; init; }

    public required bool Active { get; init; }

    /// <summary>"max_to_tg" | "tg_to_max" | "both".</summary>
    public required string Direction { get; init; }

    /// <summary>Включена ли функция «видео из ссылок» (см. VideoEmbedRelayService).</summary>
    public required bool VideoEmbed { get; init; }

    public required string CreatedAt { get; init; }
}

/// <summary>Тело PATCH: поля необязательны, меняется только присланное.</summary>
public sealed record UpdateChatLinkRequest
{
    public bool? Active { get; init; }

    /// <summary>"max_to_tg" | "tg_to_max" | "both".</summary>
    public string? Direction { get; init; }

    public bool? VideoEmbed { get; init; }
}

/// <summary>
/// Экран статистики целиком — одним ответом. Три отдельных запроса ради одного экрана
/// на мобильном интернете дороже, чем этот ответ целиком: данных здесь единицы килобайт.
/// </summary>
public sealed record StatsResponse
{
    /// <summary>Аккаунты ещё не связаны — считать нечего, интерфейс зовёт связать.</summary>
    public required bool Linked { get; init; }

    public required StatsTotalsResponse Total { get; init; }

    /// <summary>Последние сутки с активностью, свежие сверху.</summary>
    public required IReadOnlyList<StatsPeriodResponse> Days { get; init; }

    /// <summary>Последние месяцы с активностью, свежие сверху.</summary>
    public required IReadOnlyList<StatsPeriodResponse> Months { get; init; }

    /// <summary>Итоги по связкам чатов за всё время.</summary>
    public required IReadOnlyList<StatsLinkResponse> Links { get; init; }
}

/// <summary>Итог за всё время: сообщения, объём и из чего этот объём состоит.</summary>
public sealed record StatsTotalsResponse
{
    public required long Messages { get; init; }

    public required long Bytes { get; init; }

    public required long MaxToTg { get; init; }

    public required long TgToMax { get; init; }

    public required long TextBytes { get; init; }

    public required long PhotoCount { get; init; }

    public required long PhotoBytes { get; init; }

    public required long VideoCount { get; init; }

    public required long VideoBytes { get; init; }

    public required long AudioCount { get; init; }

    public required long AudioBytes { get; init; }

    public required long FileCount { get; init; }

    public required long FileBytes { get; init; }
}

/// <summary>Строка периода: "2026-07-29" для суток, "2026-07" для месяца.</summary>
public sealed record StatsPeriodResponse
{
    public required string Period { get; init; }

    public required long Messages { get; init; }

    public required long Bytes { get; init; }

    public required long MaxToTg { get; init; }

    public required long TgToMax { get; init; }
}

/// <summary>Итог по связке чатов. У удалённой связки названия нет — см. <see cref="Deleted"/>.</summary>
public sealed record StatsLinkResponse
{
    public required long Id { get; init; }

    public string? Title { get; init; }

    /// <summary>Связка удалена, но её вклад в суммы аккаунта сохранён.</summary>
    public required bool Deleted { get; init; }

    public required long Messages { get; init; }

    public required long Bytes { get; init; }
}

/// <summary>Единый вид ошибки — интерфейс показывает <c>error</c> как есть.</summary>
public sealed record ErrorResponse(string Error);
