using System;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Text.RegularExpressions;

namespace StickerAutoBot.Services;

public class BotUpdateHandler : IUpdateHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly IStickerService _stickerService;
    private readonly ILogger<BotUpdateHandler> _logger;

    public BotUpdateHandler(
        ITelegramBotClient botClient,
        IStickerService stickerService,
        ILogger<BotUpdateHandler> logger)
    {
        _botClient = botClient;
        _stickerService = stickerService;
        _logger = logger;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        var handler = update.Type switch
        {
            UpdateType.Message => OnMessageReceived(update.Message!),
            _ => Task.CompletedTask
        };

        try
        {
            await handler;
        }
        catch (Exception ex)
        {
            await HandleErrorAsync(ex);
        }
    }

    private async Task OnMessageReceived(Message message)
    {
        if (message.From == null) return;

        // Extract emoji from message text or caption
        string? emoji = ExtractEmoji(message.Text ?? message.Caption ?? "");

        try
        {
            // Обработка команды /start
            if (message.Text == "/start")
            {
                await _botClient.SendMessage(
                    message.Chat.Id,
                    RemoveEmojis($"Привет {message.From.FirstName}!\n\n" +
                    "Отправь мне фото, видео или GIF, и я преобразую его в стикер!\n" +
                    "Просто отправь любое изображение или видео.")
                );
                return;
            }

            // Обработка медиафайлов
            string? filePath = null;
            bool isAnimated = false;

            if (message.Photo != null && message.Photo.Length > 0)
            {
                // Обработка фото
                filePath = await DownloadFileAsync(message.Photo[^1].FileId);
            }
            else if (message.Video != null)
            {
                // Обработка видео
                filePath = await DownloadFileAsync(message.Video.FileId);
                isAnimated = true;
            }
            else if (message.Document != null)
            {
                // Обработка документов (GIF, MP4, WebP)
                var mimeType = message.Document.MimeType?.ToLower();
                if (mimeType == "image/gif" || mimeType == "video/mp4" || mimeType == "image/webp")
                {
                    filePath = await DownloadFileAsync(message.Document.FileId);
                    isAnimated = mimeType == "image/gif" || mimeType == "video/mp4";
                }
            }
            else if (message.Sticker != null)
            {
                // Обработка стикеров
                filePath = await DownloadFileAsync(message.Sticker.FileId);
                isAnimated = message.Sticker.IsAnimated;
                // Использовать эмодзи стикера, если доступен
                if (!string.IsNullOrEmpty(message.Sticker.Emoji))
                {
                    emoji = message.Sticker.Emoji;
                }
            }

            if (filePath == null)
            {
                await _botClient.SendMessage(
                    message.Chat.Id,
                    RemoveEmojis("Пожалуйста, отправьте изображение, видео или GIF.")
                );
                return;
            }

            // Уведомление о начале обработки
            var processingMessage = await _botClient.SendMessage(
                message.Chat.Id,
                RemoveEmojis("🔄 Обрабатываю ваш файл...")
            );

            try
            {
                // Конвертация в стикер
                var stickerPath = await _stickerService.ConvertToStickerAsync(filePath, isAnimated);

                if (stickerPath == null)
                {
                    await _botClient.SendMessage(
                        message.Chat.Id,
                        RemoveEmojis("❌ Ошибка при конвертации файла.")
                    );
                    return;
                }

                // Получение или создание стикерпака
                var stickerSetName = await _stickerService.GetOrCreateStickerSetAsync(
                    message.From.Id,
                    message.From.FirstName
                );

                // Добавление стикера в стикерпак
                await _stickerService.AddStickerToSetAsync(
                    message.From.Id,
                    stickerSetName,
                    stickerPath,
                    isAnimated,
                    emoji
                );

                // Отправка результата
                await _botClient.SendMessage(
                    message.Chat.Id,
                    RemoveEmojis($"✅ Стикер успешно добавлен в ваш стикерпак!\n\n" +
                    $"📦 Используйте ссылку чтобы посмотреть все стикеры:\n" +
                    $"https://t.me/addstickers/{stickerSetName}")
                );
            }
            finally
            {
                // Очистка временных файлов
                try
                {
                    if (File.Exists(filePath)) File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temp file");
                }
            }
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            await _botClient.SendMessage(
                message.Chat.Id,
                RemoveEmojis("❌ Не удалось добавить стикер. Возможно, достигнут лимит стикеров в пакете или неверный формат файла.")
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
            await _botClient.SendMessage(
                message.Chat.Id,
                RemoveEmojis("❌ Произошла ошибка при обработке файла.")
            );
        }
    }
    private async Task<string> DownloadFileAsync(string fileId)
    {
        var file = await _botClient.GetFile(fileId);
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{file.FilePath?.Split('/').Last() ?? "file"}");

        await using var stream = System.IO.File.Create(tempPath);

        if (string.IsNullOrEmpty(file.FilePath))
            throw new InvalidOperationException("File download path is missing from Telegram API response.");

        // If the concrete client is TelegramBotClient we can build the file URL using its token.
        if (_botClient is TelegramBotClient concreteClient)
        {
            var fileUrl = $"https://api.telegram.org/file/bot{concreteClient.Token}/{file.FilePath}";
            using var http = new HttpClient();
            using var response = await http.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var remoteStream = await response.Content.ReadAsStreamAsync();
            await remoteStream.CopyToAsync(stream);
        }
        else
        {
            // Try to find a DownloadFileAsync method via reflection as a last resort (some versions expose it as an extension/concrete method).
            var downloadMethod = _botClient.GetType().GetMethod("DownloadFileAsync", new[] { typeof(string), typeof(System.IO.Stream) });
            if (downloadMethod != null)
            {
                var task = (Task)downloadMethod.Invoke(_botClient, new object[] { file.FilePath, stream })!;
                await task;
            }
            else
            {
                throw new NotSupportedException("Unable to download file: no supported download method found on the provided ITelegramBotClient implementation.");
            }
        }

        return tempPath;
    }

    public Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException =>
                $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogError(exception, "Polling error occurred");
        return Task.CompletedTask;
    }

    private Task HandleErrorAsync(Exception exception)
    {
        _logger.LogError(exception, "Update handling error");
        return Task.CompletedTask;
    }

    private string? ExtractEmoji(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var emojiRegex = new Regex(@"[\uD83C-\uDBFF\uDC00-\uDFFF\u2600-\u26FF\u2700-\u27BF\u1f300-\u1f5ff\u1f600-\u1f64f\u1f680-\u1f6ff\u1f900-\u1f9ff]+", RegexOptions.Compiled);
        var match = emojiRegex.Match(text);
        return match.Success ? match.Value : null;
    }

    private string RemoveEmojis(string text)
    {
       return text;
    }


    public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}