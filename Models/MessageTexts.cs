using System;

namespace StickerAutoBot.Models;

public class MessageTexts
{
    public string StartMessage { get; set; } = "";
    public string InvalidMedia { get; set; } = "";
    public string Processing { get; set; } = "";
    public string ConversionError { get; set; } = "";
    public string Success { get; set; } = "";
    public string AddError { get; set; } = "";
    public string GeneralError { get; set; } = "";
}