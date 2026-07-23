using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SyncMax.Configuration;
using SyncMax.Data;
using SyncMax.Data.Migrations;
using SyncMax.Data.Repositories;
using SyncMax.Messengers;
using SyncMax.Messengers.Max;
using SyncMax.Messengers.Telegram;
using SyncMax.Services;
try
{
    // Сопоставление snake_case колонок с PascalCase свойствами моделей (user_id -> UserId).
    DefaultTypeMap.MatchNamesWithUnderscores = true;

    var builder = Host.CreateApplicationBuilder(args);

    // --- Конфигурация ---
    builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.Section));
    builder.Services.Configure<MaxOptions>(builder.Configuration.GetSection(MaxOptions.Section));
    builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));
    builder.Services.Configure<LinkingOptions>(builder.Configuration.GetSection(LinkingOptions.Section));

    // --- Данные ---
    builder.Services.AddSingleton<SqliteConnectionFactory>();
    builder.Services.AddSingleton<UserRepository>();
    builder.Services.AddSingleton<ChatLinkRepository>();

    // --- Миграции (регистрируйте здесь новые версии по возрастанию) ---
    builder.Services.AddSingleton<IMigration, M001_InitialSchema>();
    builder.Services.AddSingleton<IMigration, M002_AddLinkedToUser>();
    builder.Services.AddSingleton<IMigration, M003_ChatLinks>();
    builder.Services.AddSingleton<MigrationRunner>();

    // --- Сервисы ---
    builder.Services.AddSingleton<CodeGenerator>();
    builder.Services.AddSingleton<LinkingService>();
    builder.Services.AddSingleton<ChatLinkingService>();
    builder.Services.AddSingleton<SystemCommandService>();
    builder.Services.AddSingleton<MessageRelayService>();

    // --- Клиенты API мессенджеров: общий контракт IMessengerApiClient (отправка) ---
    builder.Services.AddSingleton<TelegramApiClient>();
    builder.Services.AddSingleton<IMessengerApiClient>(sp => sp.GetRequiredService<TelegramApiClient>());

    builder.Services.AddHttpClient<MaxApiClient>()
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler();
            MaxTrustedCertificates.ConfigureValidation(handler);
            return handler;
        });
    builder.Services.AddSingleton<IMessengerApiClient>(sp => sp.GetRequiredService<MaxApiClient>());

    // --- *BotService: только приём и разбор входящих обновлений (long polling) ---
    builder.Services.AddSingleton<TelegramBotService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramBotService>());

    builder.Services.AddSingleton<MaxBotService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MaxBotService>());

    var host = builder.Build();

    // Обновляем БД до последней версии ДО запуска ботов.
    using (var scope = host.Services.CreateScope())
    {
        var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
        await runner.MigrateAsync(CancellationToken.None);
    }

    host.Run();
}
catch (Exception ex)
{ 
    Console.WriteLine(ex.ToString());
}