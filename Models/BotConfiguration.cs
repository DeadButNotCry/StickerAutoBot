using System;

namespace StickerAutoBot.Models;

public class BotConfiguration
{
    public string BotToken { get; set; } = string.Empty;
    public string HostAddress { get; set; } = string.Empty;
    public string Route { get; set; } = "/bot";
    public string SecretToken { get; set; } = string.Empty;
    public StickerSettings StickerSettings { get; set; } = new();
    public FfmpegSettings FfmpegSettings { get; set; } = new();
    public ApplicationSettings ApplicationSettings { get; set; } = new();
}