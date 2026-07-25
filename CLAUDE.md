# SyncMax — заметки для агента

Бот-мост между MAX и Telegram. Общее описание проекта, установка, конфигурация,
Docker, БД — всё в [README.md](README.md). Здесь — только то, что нужно именно
для работы с кодом: команды и внутренние конвенции.

## Сборка / запуск

```powershell
dotnet build SyncMax.sln
dotnet run --project src/SyncMax
```

Тестового проекта в решении нет — автоматических тестов не запустить.

Единственный проект — `src/SyncMax/SyncMax.csproj`, `net10.0`. Локальная БД
(`Database:ConnectionString` в `appsettings.json`) должна быть абсолютным
путём: относительный резолвится от текущей рабочей директории процесса, а она
разная при запуске из Visual Studio и из `bin\Debug\...`.

## Устройство кода

- **Приём и отправка разделены.** `*ApiClient` (`MaxApiClient`, `TelegramApiClient`)
  реализуют общий `IMessengerApiClient` и отвечают только за исходящие запросы
  к API. `*BotService` (`MaxBotService`, `TelegramBotService`) — фоновые сервисы,
  которые занимаются только приёмом (long polling/webhook) и разбором входящих
  обновлений; для отправки сами по себе никому, кроме себя, не нужны.
- `LinkingService` не зависит ни от платформы, ни от `*BotService` — только от
  `IMessengerApiClient` (все реализации приходят через DI, нужная выбирается по
  `MessengerType`). Добавление нового мессенджера = новый `*ApiClient` (реализует
  `IMessengerApiClient`) + новый `*BotService` для приёма.
- Платформо-независимые модели — `FormattedText`/`TextSpan` (разметка) и
  `RelayMessage`/`MediaAttachment` (медиа). Вся логика пересылки в
  `MessageRelayService` работает только с ними, не зная деталей конкретной платформы.
- Миграции схемы БД — классы `IMigration` в `Data/Migrations` (`M001`, `M002`, ...),
  версия хранится в `PRAGMA user_version`. Чтобы добавить изменение схемы: новый
  класс `M00X_Описание : IMigration` с бОльшим `Version`, зарегистрировать в
  `Program.cs` рядом с `M001_InitialSchema`. Ничего вручную запускать не нужно —
  применяется на старте.

## На что обратить внимание

- **DTO и часть эндпоинтов MAX API не задокументированы публично** на момент
  реализации — `MaxModels.cs`, `MaxApiClient`, включая `SubscribeWebhookAsync`
  (`POST /subscriptions`), заполнены по типовому шаблону Bot API. Если реальный
  API отличается — править нужно именно эти места (см. также комментарий в
  `MaxModels.cs`).
- Для доверия TLS-цепочке `platform-api2.max.ru` в проект добавлены сертификаты
  российского Минцифры (`Certificates/russian_trusted_*.cer`, см.
  `MaxTrustedCertificates`) — при переносе/докеризации их нужно копировать вместе
  с бинарником (уже настроено в `.csproj`).
- Сообщения от собственного бота (в т.ч. пересланные им же) и от чужих ботов
  вообще — не обрабатываются повторно; это защита от зацикливания при
  `repost_type = both`, реализована в каждом `*BotService` по данным платформы
  об отправителе.
