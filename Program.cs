using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StickerAutoBot.Models;
using StickerAutoBot.Services;
using Telegram.Bot;

// Создание хоста
var builder = Host.CreateApplicationBuilder(args);

// Конфигурация
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

// Настройка сервисов
ConfigureServices(builder.Services, builder.Configuration);

var host = builder.Build();

// Проверка конфигурации
var config = host.Services.GetRequiredService<BotConfiguration>();
if (string.IsNullOrEmpty(config.BotToken))
{
    throw new InvalidOperationException(
        "Bot token is not configured. " +
        "Please set BotConfiguration:BotToken in appsettings.json or STICKERBOT_BOTCONFIGURATION__BOTTOKEN environment variable."
    );
}

await host.RunAsync();

static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    // Регистрация конфигурации
    services.Configure<BotConfiguration>(configuration.GetSection("BotConfiguration"));
    services.AddSingleton(resolver => resolver.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotConfiguration>>().Value);

    // Регистрация Telegram Bot Client
    services.AddSingleton<ITelegramBotClient>(sp =>
    {
        var botConfig = sp.GetRequiredService<BotConfiguration>();
        return new TelegramBotClient(botConfig.BotToken);
    });

    // Регистрация сервисов приложения
    services.AddScoped<IStickerService, StickerService>();
    services.AddScoped<BotUpdateHandler>();

    // Регистрация hosted service
    services.AddHostedService<PollingService>();

    // Настройка логирования уже выполняется в CreateApplicationBuilder
}