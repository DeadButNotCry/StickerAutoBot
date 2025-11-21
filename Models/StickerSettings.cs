using System;

namespace StickerAutoBot.Models;

public class StickerSettings
{
    public int MaxStaticSizeKb { get; set; } = 512;
    public int MaxAnimatedSizeKb { get; set; } = 256;
    public int Quality { get; set; } = 80;
    public int Resolution { get; set; } = 512;
    public string DefaultEmoji { get; set; } = "😀";
    public int MaxStickersPerUser { get; set; } = 100;
    public int MaxStickerSetsPerUser { get; set; } = 3;
    public List<string> AllowedMimeTypes { get; set; } = new()
    {
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp",
        "video/mp4",
        "video/quicktime"
    };
}