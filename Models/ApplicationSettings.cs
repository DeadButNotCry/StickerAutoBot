using System;

namespace StickerAutoBot.Models;

public class ApplicationSettings
{
    public string TempDirectory { get; set; } = "./temp";
    public int MaxFileSizeMb { get; set; } = 20;
    public int ProcessingTimeoutSeconds { get; set; } = 30;
    public bool EnableLogging { get; set; } = true;
    public string LogLevel { get; set; } = "Information";
    public bool DeleteTempFiles { get; set; } = true;
    public int MaxConcurrentOperations { get; set; } = 5;
}