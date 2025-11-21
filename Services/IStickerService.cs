using System;
using Telegram.Bot;

namespace StickerAutoBot.Services;

public interface IStickerService
{
    Task<string?> ConvertToStickerAsync(string inputPath, bool isAnimated);
    Task<string> GetOrCreateStickerSetAsync(long userId, string userName);
    Task AddStickerToSetAsync(long userId, string stickerSetName, string stickerPath, bool isAnimated, string? emoji = null);
    Task<bool> ValidateFileAsync(string filePath, long fileSize, string? mimeType);
    Task CleanupTempFilesAsync();
}