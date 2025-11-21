using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using StickerAutoBot.Models;
using StickerAutoBot.Utils;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace StickerAutoBot.Services;

public class StickerService : IStickerService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<StickerService> _logger;
    private readonly BotConfiguration _config;
    private readonly SemaphoreSlim _processingSemaphore;

    public StickerService(
        ITelegramBotClient botClient,
        ILogger<StickerService> logger,
        IOptions<BotConfiguration> config)
    {
        _botClient = botClient;
        _logger = logger;
        _config = config.Value;

        // Создание временной директории
        if (!Directory.Exists(_config.ApplicationSettings.TempDirectory))
        {
            Directory.CreateDirectory(_config.ApplicationSettings.TempDirectory);
            _logger.LogInformation("Created temp directory: {TempDirectory}", _config.ApplicationSettings.TempDirectory);
        }

        // Ограничение одновременных операций
        _processingSemaphore = new SemaphoreSlim(_config.ApplicationSettings.MaxConcurrentOperations);
    }

    public async Task<string?> ConvertToStickerAsync(string inputPath, bool isAnimated)
    {
        await _processingSemaphore.WaitAsync();

        var extension = isAnimated ? "webm" : "webp";
        var outputPath = Path.Combine(_config.ApplicationSettings.TempDirectory, $"sticker_{Guid.NewGuid()}.{extension}");

        try
        {
            _logger.LogDebug("Starting conversion: {InputPath} -> {OutputPath} (Animated: {IsAnimated})",
                inputPath, outputPath, isAnimated);

            if (isAnimated)
            {
                await ConvertToWebMAsync(inputPath, outputPath);
            }
            else
            {
                await ConvertToWebPAsync(inputPath, outputPath);
            }

            if (!File.Exists(outputPath))
            {
                _logger.LogError("Output file was not created: {OutputPath}", outputPath);
                return null;
            }

            // Проверка размера файла
            var fileInfo = new FileInfo(outputPath);
            var maxSizeKb = isAnimated ? _config.StickerSettings.MaxAnimatedSizeKb : _config.StickerSettings.MaxStaticSizeKb;
            var maxSizeBytes = maxSizeKb * 1024;

            if (fileInfo.Length > maxSizeBytes)
            {
                _logger.LogWarning("Sticker file too large: {Size} bytes, max: {MaxSize} bytes",
                    fileInfo.Length, maxSizeBytes);

                // Пытаемся оптимизировать дальше
                var optimizedPath = await OptimizeStickerSizeAsync(outputPath, isAnimated, maxSizeBytes);
                if (optimizedPath != null)
                {
                    FileHelper.SafeDelete(outputPath);
                    outputPath = optimizedPath;
                    fileInfo = new FileInfo(outputPath);
                }

                // Если все еще слишком большой
                if (fileInfo.Length > maxSizeBytes)
                {
                    _logger.LogError("Sticker file still too large after optimization: {Size} bytes", fileInfo.Length);
                    FileHelper.SafeDelete(outputPath);
                    throw new InvalidOperationException($"Sticker file is too large. Maximum size: {maxSizeKb}KB");
                }
            }

            _logger.LogInformation("Successfully converted sticker: {OutputPath} ({Size} bytes)",
                outputPath, fileInfo.Length);

            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting file to sticker: {InputPath}", inputPath);

            // Очистка в случае ошибки
            FileHelper.SafeDelete(outputPath);
            return null;
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private async Task ConvertToWebPAsync(string inputPath, string outputPath)
    {
        using var image = await Image.LoadAsync(inputPath);

        _logger.LogDebug("Original image dimensions: {Width}x{Height}", image.Width, image.Height);

        // Ресайз с настройками из конфигурации
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(_config.StickerSettings.Resolution, _config.StickerSettings.Resolution),
            Mode = ResizeMode.Max
        }));

        _logger.LogDebug("Resized image dimensions: {Width}x{Height}", image.Width, image.Height);

        var encoder = new WebpEncoder
        {
            Quality = _config.StickerSettings.Quality,
            Method = WebpEncodingMethod.Default,
            UseAlphaCompression = true,
            FileFormat = WebpFileFormatType.Lossy
        };

        await image.SaveAsync(outputPath, encoder);
        _logger.LogDebug("WebP sticker saved: {OutputPath}", outputPath);
    }

    private async Task ConvertToWebMAsync(string inputPath, string outputPath)
    {
        var ffmpegSettings = _config.FfmpegSettings;

        var ffmpegArgs = $"-i \"{inputPath}\" " +
                        $"-c:v {ffmpegSettings.VideoCodec} " +
                        $"-b:v 0 " +
                        $"-crf {ffmpegSettings.Crf} " +
                        $"-preset {ffmpegSettings.Preset} " +
                        $"{ffmpegSettings.AdditionalArgs} " +
                        $"-fs {ffmpegSettings.MaxFileSizeKb}K " +
                        $"-y \"{outputPath}\"";

        _logger.LogDebug("FFmpeg command: {FfmpegPath} {FfmpegArgs}", ffmpegSettings.Path, ffmpegArgs);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = ffmpegSettings.Path,
            Arguments = ffmpegArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(processStartInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to start FFmpeg process");

        // Читаем вывод для логирования
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        if (!string.IsNullOrEmpty(output))
            _logger.LogDebug("FFmpeg output: {Output}", output);

        if (process.ExitCode != 0)
        {
            _logger.LogError("FFmpeg error (exit code {ExitCode}): {Error}", process.ExitCode, error);
            throw new Exception($"FFmpeg error (exit code {process.ExitCode}): {error}");
        }

        _logger.LogDebug("WebM sticker successfully created: {OutputPath}", outputPath);
    }

    private async Task<string?> OptimizeStickerSizeAsync(string inputPath, bool isAnimated, long maxSizeBytes)
    {
        _logger.LogInformation("Optimizing sticker size: {InputPath}", inputPath);

        if (isAnimated)
        {
            return await OptimizeWebMSizeAsync(inputPath, maxSizeBytes);
        }
        else
        {
            return await OptimizeWebPSizeAsync(inputPath, maxSizeBytes);
        }
    }

    private async Task<string?> OptimizeWebPSizeAsync(string inputPath, long maxSizeBytes)
    {
        var optimizedPath = Path.Combine(_config.ApplicationSettings.TempDirectory, $"optimized_{Guid.NewGuid()}.webp");

        try
        {
            using var image = await Image.LoadAsync(inputPath);

            // Постепенно уменьшаем качество
            for (int quality = _config.StickerSettings.Quality - 10; quality >= 30; quality -= 10)
            {
                var encoder = new WebpEncoder
                {
                    Quality = quality,
                    Method = WebpEncodingMethod.Default
                };

                await image.SaveAsync(optimizedPath, encoder);

                var fileInfo = new FileInfo(optimizedPath);
                if (fileInfo.Length <= maxSizeBytes)
                {
                    _logger.LogInformation("WebP optimized with quality {Quality}, size: {Size} bytes", quality, fileInfo.Length);
                    return optimizedPath;
                }

                FileHelper.SafeDelete(optimizedPath);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error optimizing WebP size");
            FileHelper.SafeDelete(optimizedPath);
            return null;
        }
    }

    private async Task<string?> OptimizeWebMSizeAsync(string inputPath, long maxSizeBytes)
    {
        var optimizedPath = Path.Combine(_config.ApplicationSettings.TempDirectory, $"optimized_{Guid.NewGuid()}.webm");
        var ffmpegSettings = _config.FfmpegSettings;

        try
        {
            // Постепенно увеличиваем CRF (ухудшаем качество) для уменьшения размера
            for (int crf = ffmpegSettings.Crf + 5; crf <= 50; crf += 5)
            {
                var ffmpegArgs = $"-i \"{inputPath}\" " +
                                $"-c:v {ffmpegSettings.VideoCodec} " +
                                $"-b:v 0 " +
                                $"-crf {crf} " +
                                $"-preset {ffmpegSettings.Preset} " +
                                $"{ffmpegSettings.AdditionalArgs} " +
                                $"-y \"{optimizedPath}\"";

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegSettings.Path,
                    Arguments = ffmpegArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(processStartInfo);
                if (process == null) continue;

                await process.WaitForExitAsync();

                if (process.ExitCode == 0 && File.Exists(optimizedPath))
                {
                    var fileInfo = new FileInfo(optimizedPath);
                    if (fileInfo.Length <= maxSizeBytes)
                    {
                        _logger.LogInformation("WebM optimized with CRF {Crf}, size: {Size} bytes", crf, fileInfo.Length);
                        return optimizedPath;
                    }

                    FileHelper.SafeDelete(optimizedPath);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error optimizing WebM size");
            FileHelper.SafeDelete(optimizedPath);
            return null;
        }
    }

    public async Task<string> GetOrCreateStickerSetAsync(long userId, string userName)
    {
        var botInfo = await _botClient.GetMe();
        
        var stickerSetName = $"Split_Second_by_{botInfo.Username}";
        var botOwnerId = 415604189 ;

        _logger.LogDebug("Getting or creating global sticker set: {StickerSetName}", stickerSetName);

        try
        {
            // Пытаемся получить существующий стикерпак
            var stickerSet = await _botClient.GetStickerSet(stickerSetName);

            // Проверяем лимит стикеров
            if (stickerSet.Stickers.Length >= _config.StickerSettings.MaxStickersPerUser)
            {
                throw new InvalidOperationException(
                    $"Maximum stickers limit reached: {_config.StickerSettings.MaxStickersPerUser}. " +
                    "Please remove some stickers from the pack.");
            }

            _logger.LogDebug("Found existing sticker set with {StickerCount} stickers", stickerSet.Stickers.Length);
            return stickerSetName;
        }
        catch (Exception ex) when (ex.Message.Contains("STICKERSET_INVALID") || ex.Message.Contains("not found"))
        {
            // Стикерпак не существует, создаем новый
            _logger.LogInformation("Creating new global sticker set: {StickerSetName}", stickerSetName);

            var stickerSetTitle = $"Split Second";

            var placeholderPath = await CreatePlaceholderStickerAsync();

            try
            {
                await using var stream = File.OpenRead(placeholderPath);
                try
                {
                    await _botClient.CreateNewStickerSet(
                        userId: botOwnerId,
                        name: stickerSetName,
                        title: stickerSetTitle,
                        stickers:
                        [
                            new InputSticker(
                                new InputFileStream(stream, "placeholder.webp"),
                                StickerFormat.Static,
                                [_config.StickerSettings.DefaultEmoji]
                            )
                        ]
                    );
                }
                catch (ApiRequestException apiEx)
                {
                    _logger.LogError(apiEx, "Telegram API error creating sticker set: {ErrorCode} - {Message}",
                        apiEx.ErrorCode, apiEx.Message);
                    throw;
                }

                _logger.LogInformation("Successfully created new global sticker set: {StickerSetName}", stickerSetName);
                return stickerSetName;
            }
            finally
            {
                // Очистка временного файла
                if (_config.ApplicationSettings.DeleteTempFiles)
                    FileHelper.SafeDelete(placeholderPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting or creating global sticker set");
            throw;
        }
    }

    public async Task AddStickerToSetAsync(long userId, string stickerSetName, string stickerPath, bool isAnimated, string? emoji = null)
    {
        var botInfo = await _botClient.GetMe();
        var botOwnerId = botInfo.Id;

        _logger.LogDebug("Adding sticker to global set: {StickerSetName}, Animated: {IsAnimated}",
            stickerSetName, isAnimated);

        try
        {
            await using var stream = File.OpenRead(stickerPath);
            var inputFile = new InputFileStream(stream, Path.GetFileName(stickerPath));
            var emojis = new[] { emoji ?? _config.StickerSettings.DefaultEmoji };

            if (isAnimated)
            {
                await _botClient.AddStickerToSet(
                    userId: botOwnerId,
                    name: stickerSetName,
                    sticker: new InputSticker(inputFile, StickerFormat.Animated, emojis)
                );
            }
            else
            {
                await _botClient.AddStickerToSet(
                    userId: botOwnerId,
                    name: stickerSetName,
                    sticker: new InputSticker(inputFile, StickerFormat.Static, emojis)
                );
            }

            _logger.LogInformation("Successfully added sticker to global set: {StickerSetName}", stickerSetName);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            _logger.LogError("Telegram API error adding sticker: {ErrorCode} - {Message}", ex.ErrorCode, ex.Message);

            if (ex.Message.Contains("STICKERS_TOO_MUCH"))
            {
                throw new InvalidOperationException(
                    $"Sticker pack is full. Maximum {_config.StickerSettings.MaxStickersPerUser} stickers allowed.");
            }
            else if (ex.Message.Contains("STICKER_PNG_DIMENSIONS"))
            {
                throw new InvalidOperationException("Invalid sticker dimensions. Sticker must be 512x512 pixels.");
            }
            else if (ex.Message.Contains("STICKER_TGS_NOTGS"))
            {
                throw new InvalidOperationException("Invalid animated sticker format. Please use WebM format.");
            }
            else
            {
                throw new InvalidOperationException($"Failed to add sticker: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding sticker to global set");
            throw;
        }
        finally
        {
            // Очистка временного файла стикера
            if (_config.ApplicationSettings.DeleteTempFiles)
                FileHelper.SafeDelete(stickerPath);
        }
    }

    public async Task<bool> ValidateFileAsync(string filePath, long fileSize, string? mimeType)
    {
        // Проверка размера файла
        if (!ValidationHelper.IsFileSizeValid(fileSize, _config.ApplicationSettings))
        {
            _logger.LogWarning("File size too large: {FileSize} bytes", fileSize);
            return false;
        }

        // Проверка MIME типа
        if (mimeType != null && !ValidationHelper.IsValidMimeType(mimeType, _config.StickerSettings))
        {
            _logger.LogWarning("Invalid MIME type: {MimeType}", mimeType);
            return false;
        }

        // Проверка существования файла
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File does not exist: {FilePath}", filePath);
            return false;
        }

        // Дополнительные проверки для изображений
        if (mimeType?.StartsWith("image/") == true)
        {
            try
            {
                var imageInfo = await Image.IdentifyAsync(filePath);
                if (imageInfo == null)
                {
                    _logger.LogWarning("Invalid image file: {FilePath}", filePath);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to validate image file: {FilePath}", filePath);
                return false;
            }
        }

        return true;
    }

    public async Task CleanupTempFilesAsync()
    {
        try
        {
            var tempDir = _config.ApplicationSettings.TempDirectory;
            if (!Directory.Exists(tempDir))
                return;

            var files = Directory.GetFiles(tempDir, "sticker_*.*")
                .Concat(Directory.GetFiles(tempDir, "optimized_*.*"))
                .Concat(Directory.GetFiles(tempDir, "placeholder_*.*"))
                .Concat(Directory.GetFiles(tempDir, "temp_*.*"));

            var cleanupCount = 0;
            foreach (var file in files)
            {
                try
                {
                    // Удаляем только старые файлы (старше 1 часа)
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime < DateTime.Now.AddHours(-1))
                    {
                        File.Delete(file);
                        cleanupCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to delete temp file: {File}", file);
                }
            }

            if (cleanupCount > 0)
            {
                _logger.LogInformation("Cleaned up {FileCount} temporary files", cleanupCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during temp files cleanup");
        }
    }

    private async Task<string> CreatePlaceholderStickerAsync()
    {
        var path = Path.Combine(_config.ApplicationSettings.TempDirectory, $"placeholder_{Guid.NewGuid()}.webp");

        try
        {
            using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(512, 512);

            // Создаем простой градиентный фон
            image.Mutate(x => x.BackgroundColor(SixLabors.ImageSharp.Color.LightGray));

            var encoder = new WebpEncoder { Quality = 1 };
            await image.SaveAsync(path, encoder);

            return path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating placeholder sticker");
            throw;
        }
    }

    public void Dispose()
    {
        _processingSemaphore?.Dispose();

        if (_config.ApplicationSettings.DeleteTempFiles)
        {
            Task.Run(CleanupTempFilesAsync).Wait(5000);
        }
    }
}

internal static class FileHelper
{
    public static void SafeDelete(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore failures during cleanup
        }
    }
}