using System.Text.Json.Serialization;

namespace LastWarExtractor.Models;

/// <summary>Normalized (0..1000) avatar bounding box over the whole screenshot.</summary>
public sealed class AvatarBBox
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    public (int X, int Y, int Width, int Height) AsTuple() => (X, Y, Width, Height);
}

public sealed class ExtractedRow
{
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("player_id")] public int? PlayerId { get; set; }
    [JsonPropertyName("raw_name")] public string RawName { get; set; } = "";
    [JsonPropertyName("alliance_name")] public string? AllianceName { get; set; }
    [JsonPropertyName("points")] public int Points { get; set; }
    [JsonPropertyName("extraction_confidence")] public double ExtractionConfidence { get; set; }
    [JsonPropertyName("avatar_bbox")] public AvatarBBox? AvatarBBox { get; set; }
}

public sealed class ScreenshotExtraction
{
    [JsonPropertyName("detected_day")] public string DetectedDay { get; set; } = "";
    [JsonPropertyName("day_confidence")] public double DayConfidence { get; set; }
    [JsonPropertyName("ui_language")] public string UiLanguage { get; set; } = "";
    [JsonPropertyName("rows")] public List<ExtractedRow> Rows { get; set; } = new();
    [JsonPropertyName("pinned_row")] public ExtractedRow? PinnedRow { get; set; }
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
}

public sealed class ExtractionResult
{
    public ExtractionResult(string imagePath, ScreenshotExtraction? extraction, string? error = null)
    {
        ImagePath = imagePath;
        Extraction = extraction;
        Error = error;
    }

    public string ImagePath { get; }
    public ScreenshotExtraction? Extraction { get; }
    public string? Error { get; }

    public string FileName => Path.GetFileName(ImagePath);
}
