using CommunityToolkit.Mvvm.ComponentModel;
using LastWarExtractor.Models;
using LastWarExtractor.Services;

namespace LastWarExtractor.ViewModels;

/// <summary>Review-list wrapper around a domain <see cref="Observation"/>.</summary>
public sealed class ObservationItem : ObservableObject
{
    public ObservationItem(Observation model) => Model = model;

    public Observation Model { get; }

    public string Header => $"{TitleCase(Model.Day)} · rank {Model.Rank}";
    public string Name => Model.RawName;
    public string Detail => $"{Model.Points:N0} points · {Model.MatchMethod} ({Model.MatchConfidence:P0})";
    public string Matched => Model.MatchedMemberName ?? "Unassigned";
    public bool NeedsReview => Model.MatchedMemberId is null;
    public bool HasAlternatives => Model.Alternatives.Count > 0;
    public string AssignLabel => NeedsReview ? "Assign" : "Reassign";

    public string StatusIcon => !NeedsReview
        ? "matched_status_badge.png"
        : (HasAlternatives ? "needs_review_badge.png" : "unmatched_target_skull_badge.png");

    public Color AccentColor => !NeedsReview
        ? Color.FromArgb("#00E676")
        : (HasAlternatives ? Color.FromArgb("#FF9800") : Color.FromArgb("#FF3333"));

    public void Refresh()
    {
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Matched));
        OnPropertyChanged(nameof(NeedsReview));
        OnPropertyChanged(nameof(HasAlternatives));
        OnPropertyChanged(nameof(AssignLabel));
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(AccentColor));
    }

    private static string TitleCase(string day) =>
        day.Length == 0 ? day : char.ToUpperInvariant(day[0]) + day[1..];
}
