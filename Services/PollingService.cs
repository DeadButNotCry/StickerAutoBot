using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
namespace StickerAutoBot.Services;

public class PollingService : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly BotUpdateHandler _updateHandler;
    private readonly ILogger<PollingService> _logger;

    public PollingService(
        ITelegramBotClient botClient,
        BotUpdateHandler updateHandler,
        ILogger<PollingService> logger)
    {
        _botClient = botClient;
        _updateHandler = updateHandler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting bot polling service...");

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>(),
        };


        await _botClient.ReceiveAsync(
            updateHandler: _updateHandler,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken
        );
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping bot polling service...");
        await base.StopAsync(cancellationToken);
    }
}
