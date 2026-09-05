namespace lawar4.Models;

public readonly record struct Alternative(int MemberId, string Name, double Score);

public readonly record struct AvatarMatch(int MemberId, double Score, int Samples);

/// <summary>A single extracted player observation plus its match metadata.</summary>
public sealed class Observation
{
    public required string SourceFile { get; init; }
    public required string Day { get; init; }
    public required int Rank { get; init; }
    public int? RawPlayerId { get; init; }
    public required string RawName { get; init; }
    public required int Points { get; init; }
    public required double ExtractionConfidence { get; init; }
    public required bool IsPinnedRow { get; init; }
    public (int X, int Y, int Width, int Height)? AvatarBBox { get; init; }
    public string? AvatarFingerprint { get; init; }

    public int? MatchedMemberId { get; set; }
    public string? MatchedMemberName { get; set; }
    public string MatchMethod { get; set; } = "unmatched";
    public double MatchConfidence { get; set; }
    public string? Issue { get; set; }
    public List<Alternative> Alternatives { get; set; } = new();
}

public sealed class WeeklyData
{
    public WeeklyData(
        List<Observation> observations,
        Dictionary<(int MemberId, string Day), int> scores,
        List<string> issues,
        Dictionary<string, List<Member>> missingByDay)
    {
        Observations = observations;
        Scores = scores;
        Issues = issues;
        MissingByDay = missingByDay;
    }

    public List<Observation> Observations { get; }
    public Dictionary<(int MemberId, string Day), int> Scores { get; }
    public List<string> Issues { get; }
    public Dictionary<string, List<Member>> MissingByDay { get; }
}
