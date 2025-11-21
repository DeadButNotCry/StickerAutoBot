using System;

namespace StickerAutoBot.Models;

public class StickerUser
{
    public long UserId { get; set; }
    public string? StickerSetName { get; set; }
    public int StickerCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    
    public void UpdateActivity() => LastActivity = DateTime.UtcNow;
}