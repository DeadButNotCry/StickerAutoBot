using System;

namespace StickerAutoBot.Models;

public class FfmpegSettings
{
    public string Path { get; set; } = "ffmpeg";
    public string VideoCodec { get; set; } = "vp9";
    public int Crf { get; set; } = 30;
    public string Preset { get; set; } = "fast";
    public int MaxFileSizeKb { get; set; } = 256;
    public string AdditionalArgs { get; set; } = "-an -sn -dn";
}