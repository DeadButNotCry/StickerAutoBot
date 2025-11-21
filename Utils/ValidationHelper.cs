using System;
using StickerAutoBot.Models;

namespace StickerAutoBot.Utils;

public static class ValidationHelper
{
    public static bool IsValidMimeType(string mimeType, StickerSettings settings)
    {
        return settings.AllowedMimeTypes.Contains(mimeType?.ToLower() ?? string.Empty);
    }

    public static bool IsFileSizeValid(long fileSize, ApplicationSettings settings)
    {
        return fileSize <= settings.MaxFileSizeMb * 1024 * 1024;
    }

    public static string SanitizeStickerSetName(string name)
    {
        // Убираем недопустимые символы для имени стикерпака
        return new string(name
            .ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());
    }
}