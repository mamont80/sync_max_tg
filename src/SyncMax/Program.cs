using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Data;
using SyncMax.Data.Migrations;
using SyncMax.Data.Repositories;
using SyncMax.Logging;
using SyncMax.Messengers;
using SyncMax.Messengers.Max;
using SyncMax.Messengers.Telegram;
using SyncMax.Services;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
try
{
    // Сопоставление snake_case колонок с PascalCase свойствами моделей (user_id -> UserId).
    DefaultTypeMap.MatchNamesWithUnderscores = true;

    var builder = WebApplication.CreateBuilder(args);

    // --- Настройки мессенджеров нужны ДО Build(): от режима каждого зависит, какие
    // webhook-эндпоинты маппить. ---
    var telegramOptions = builder.Configuration.GetSection(TelegramOptions.Section).Get<TelegramOptions>() ?? new TelegramOptions();
    var maxOptions = builder.Configuration.GetSection(MaxOptions.Section).Get<MaxOptions>() ?? new MaxOptions();
    var httpServer = builder.Configuration.GetSection(HttpServerOptions.Section).Get<HttpServerOptions>() ?? new HttpServerOptions();

    // HTTP-сервер один на процесс и поднимается всегда, когда задан HttpServer:ListenUrl —
    // независимо от режимов ботов (доступность можно проверить через GET /test). Пустой
    // ListenUrl — наружу не открываемся, слушаем случайный порт на loopback.
    builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(httpServer.ListenUrl) ? "http://127.0.0.1:0" : httpServer.ListenUrl);

    // --- Логирование: консоль (по умолчанию от хоста) + дублирование в файл (logs/syncmax-*.log). ---
    builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs")));

    // --- Конфигурация ---
    builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.Section));
    builder.Services.Configure<MaxOptions>(builder.Configuration.GetSection(MaxOptions.Section));
    builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));
    builder.Services.Configure<LinkingOptions>(builder.Configuration.GetSection(LinkingOptions.Section));
    builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.Section));

    // --- Данные ---
    builder.Services.AddSingleton<SqliteConnectionFactory>();
    builder.Services.AddSingleton<UserRepository>();
    builder.Services.AddSingleton<ChatLinkRepository>();
    builder.Services.AddSingleton<MessageLinkRepository>();

    // --- Миграции (регистрируйте здесь новые версии по возрастанию) ---
    builder.Services.AddSingleton<IMigration, M001_InitialSchema>();
    builder.Services.AddSingleton<IMigration, M002_AddLinkedToUser>();
    builder.Services.AddSingleton<IMigration, M003_ChatLinks>();
    builder.Services.AddSingleton<IMigration, M004_MessageLinks>();
    builder.Services.AddSingleton<MigrationRunner>();

    // --- Сервисы ---
    builder.Services.AddSingleton<CodeGenerator>();
    builder.Services.AddSingleton<LinkingService>();
    builder.Services.AddSingleton<ChatLinkingService>();
    builder.Services.AddSingleton<SystemCommandService>();
    builder.Services.AddSingleton<MessageRelayService>();
    builder.Services.AddSingleton<MediaConverter>();

    // --- Клиенты API мессенджеров: общий контракт IMessengerApiClient (отправка) ---
    builder.Services.AddSingleton<TelegramApiClient>();
    builder.Services.AddSingleton<IMessengerApiClient>(sp => sp.GetRequiredService<TelegramApiClient>());

    builder.Services.AddHttpClient<MaxApiClient>()
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            // AutomaticDecompression: MAX может отдавать крупные ответы (напр. сообщение с
            // аудио) в gzip; без разжатия они читались бы как «мусор» и терялись при разборе.
            var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
            MaxTrustedCertificates.ConfigureValidation(handler);
            return handler;
        });
    builder.Services.AddSingleton<IMessengerApiClient>(sp => sp.GetRequiredService<MaxApiClient>());

    // Отдельный клиент для скачивания/загрузки медиа MAX (CDN): тот же TLS-handler
    // (доверие Russian CA), но без заголовка Authorization API.
    builder.Services.AddHttpClient(MaxApiClient.MediaHttpClientName)
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
            MaxTrustedCertificates.ConfigureValidation(handler);
            return handler;
        });

    // --- *BotService: приём и разбор входящих обновлений (long polling или webhook, см. Mode) ---
    builder.Services.AddSingleton<TelegramBotService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramBotService>());

    builder.Services.AddSingleton<MaxBotService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MaxBotService>());

    var app = builder.Build();

    // Проверка доступности сервера снаружи (в т.ч. что reverse proxy/порт настроены верно):
    // отвечает всегда, независимо от режимов ботов.
    app.MapGet("/test", () => Results.Text("ok"));

    var webhookLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Webhook");

    // Тело webhook-запроса читаем целиком в строку, а не десериализуем прямо из потока:
    // сырой JSON видно в логе, его можно разобрать повторно/руками при отладке.
    static async Task<string> ReadBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    // --- Webhook-эндпоинты: каждый маппится только если его мессенджер в режиме Webhook,
    // по своему пути ({Мессенджер}:Webhook:Path). Секрет для проверки — производная от
    // токена этого бота (см. WebhookSecret), отдельной настройки не требует. ---
    if (telegramOptions.Mode == BotMode.Webhook && !string.IsNullOrWhiteSpace(telegramOptions.Token))
    {
        var telegramSecret = WebhookSecret.FromToken(telegramOptions.Token);
        app.MapPost(telegramOptions.Webhook.Path, async Task<IResult> (HttpContext ctx, TelegramBotService bot, CancellationToken ct) =>
        {
            // Telegram присылает secret_token, заданный в setWebhook, отдельным заголовком.
            if (ctx.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] != telegramSecret)
                return Results.Unauthorized();

            var body = await ReadBodyAsync(ctx, ct);
            webhookLogger.LogInformation("[Telegram] webhook body:\n{Body}", body);
            if (string.IsNullOrWhiteSpace(body))
                return Results.Ok();

            Update? update;
            try
            {
                // Модели Telegram.Bot размечены не атрибутами, а общей политикой имён
                // (snake_case) из JsonBotAPI.Options: с настройками по умолчанию поля
                // не сопоставляются и получается пустой Update.
                update = JsonSerializer.Deserialize<Update>(body, JsonBotAPI.Options);
            }
            catch (JsonException ex)
            {
                webhookLogger.LogError(ex, "[Telegram] не разобрано тело webhook-запроса:\n{Body}", body);
                return Results.Ok();
            }

            if (update is not null)
                await bot.HandleWebhookUpdateAsync(update, ct);
            return Results.Ok();
        });
    }

    if (maxOptions.Mode == BotMode.Webhook && !string.IsNullOrWhiteSpace(maxOptions.Token))
    {
        var maxSecret = WebhookSecret.FromToken(maxOptions.Token);
        app.MapPost(maxOptions.Webhook.Path, async Task<IResult> (HttpContext ctx, MaxBotService bot, CancellationToken ct) =>
        {
            // У MAX нет штатной подписи запроса — секрет пришит к зарегистрированному url как ?token=.
            if (ctx.Request.Query["token"] != maxSecret)
                return Results.Unauthorized();

            var body = await ReadBodyAsync(ctx, ct);
            webhookLogger.LogInformation("[MAX] webhook body:\n{Body}", body);
            if (string.IsNullOrWhiteSpace(body))
                return Results.Ok();

            MaxUpdate? update;
            try
            {
                // Поля MaxUpdate размечены [JsonPropertyName] — хватает настроек по умолчанию.
                update = JsonSerializer.Deserialize<MaxUpdate>(body);
            }
            catch (JsonException ex)
            {
                webhookLogger.LogError(ex, "[MAX] не разобрано тело webhook-запроса:\n{Body}", body);
                return Results.Ok();
            }

            if (update is not null)
                await bot.HandleWebhookUpdateAsync(update, ct);
            return Results.Ok();
        });
    }

    // Обновляем БД до последней версии ДО запуска ботов.
    using (var scope = app.Services.CreateScope())
    {
        var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
        await runner.MigrateAsync(CancellationToken.None);
    }

    app.Run();
}
catch (Exception ex)
{ 
    Console.WriteLine(ex.ToString());
}