using LastWarExtractor.Models;

namespace LastWarExtractor.Services;

/// <summary>
/// Match extracted observations to active members. Two phases: deterministic identity
/// signals (ID, exact name, saved alias), then conservative avatar-assisted matching.
/// Ported from matcher.py.
/// </summary>
public sealed class MemberMatcher
{
    private readonly AliasStore _aliasStore;
    private readonly AvatarStore? _avatarStore;
    private readonly double _avatarAutoThreshold;
    private readonly double _avatarMinMargin;
    private readonly double _avatarSuggestionThreshold;

    private readonly Dictionary<int, List<Member>> _byId = new();
    private readonly Dictionary<string, List<Member>> _byNorm = new();
    private readonly Dictionary<int, string> _nameChoices = new();

    public MemberMatcher(
        IEnumerable<Member> members,
        AliasStore aliasStore,
        AvatarStore? avatarStore = null,
        double avatarAutoThreshold = 0.92,
        double avatarMinMargin = 0.06,
        double avatarSuggestionThreshold = 0.78)
    {
        _aliasStore = aliasStore;
        _avatarStore = avatarStore;
        _avatarAutoThreshold = avatarAutoThreshold;
        _avatarMinMargin = avatarMinMargin;
        _avatarSuggestionThreshold = avatarSuggestionThreshold;

        foreach (var m in members)
        {
            if (!_byId.TryGetValue(m.MemberId, out var idList))
                _byId[m.MemberId] = idList = new List<Member>();
            idList.Add(m);

            var norm = TextUtil.NormalizeName(m.Name);
            if (!_byNorm.TryGetValue(norm, out var normList))
                _byNorm[norm] = normList = new List<Member>();
            normList.Add(m);

            _nameChoices[m.MemberId] = norm;
        }
    }

    public IEnumerable<int> MemberIds => _byId.Keys;

    private static void SetMatch(Observation obs, Member member, string method, double confidence)
    {
        obs.MatchedMemberId = member.MemberId;
        obs.MatchedMemberName = member.Name;
        obs.MatchMethod = method;
        obs.MatchConfidence = confidence;
        obs.Issue = null;
    }

    public void MatchDeterministic(Observation obs)
    {
        if (obs.RawPlayerId is int playerId)
        {
            var candidates = _byId.GetValueOrDefault(playerId) ?? new List<Member>();
            if (candidates.Count == 1)
            {
                SetMatch(obs, candidates[0], "id", 1.0);
                return;
            }
            if (candidates.Count > 1)
            {
                obs.Issue = $"Duplicate active member ID {playerId}; manual review required.";
                return;
            }
        }

        var norm = TextUtil.NormalizeName(obs.RawName);
        var exact = _byNorm.GetValueOrDefault(norm) ?? new List<Member>();
        if (exact.Count == 1)
        {
            SetMatch(obs, exact[0], "exact_name", 1.0);
            return;
        }
        if (exact.Count > 1)
        {
            obs.Issue = $"Normalized name '{obs.RawName}' maps to multiple active members.";
            return;
        }

        var aliasMemberId = _aliasStore.GetMemberId(norm);
        if (aliasMemberId is int aliasId)
        {
            var candidates = _byId.GetValueOrDefault(aliasId) ?? new List<Member>();
            if (candidates.Count == 1)
            {
                SetMatch(obs, candidates[0], "saved_alias", 1.0);
                return;
            }
        }

        SetFuzzySuggestions(obs);
        obs.Issue = "No deterministic ID/name/alias match.";
    }

    private void SetFuzzySuggestions(Observation obs)
    {
        var norm = TextUtil.NormalizeName(obs.RawName);
        var fuzzy = new List<Alternative>();
        if (norm.Length > 0)
        {
            var scored = _nameChoices
                .Select(kv => (Score: TextUtil.SequenceRatio(norm, kv.Value), MemberId: kv.Key))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.MemberId)
                .Take(3);
            foreach (var (score, memberId) in scored)
            {
                var member = _byId.GetValueOrDefault(memberId)?.FirstOrDefault();
                if (member is not null)
                    fuzzy.Add(new Alternative(member.MemberId, member.Name, score));
            }
        }
        obs.Alternatives = fuzzy;
    }

    public bool LearnAvatar(Observation obs)
    {
        if (_avatarStore is null
            || obs.MatchedMemberId is null
            || string.IsNullOrEmpty(obs.AvatarFingerprint)
            || obs.MatchMethod is not ("id" or "exact_name" or "saved_alias" or "manual" or "avatar_auto"))
            return false;
        return _avatarStore.SaveReference(obs.MatchedMemberId.Value, obs.AvatarFingerprint, obs.SourceFile, obs.MatchMethod);
    }

    public void MatchAvatar(Observation obs)
    {
        if (obs.MatchedMemberId is not null || _avatarStore is null || string.IsNullOrEmpty(obs.AvatarFingerprint))
            return;

        var matches = _avatarStore.BestMatches(obs.AvatarFingerprint!, _byId.Keys, limit: 5);
        if (matches.Count == 0)
            return;

        var best = matches[0];
        double secondScore = matches.Count > 1 ? matches[1].Score : 0.0;
        double margin = best.Score - secondScore;
        var memberCandidates = _byId.GetValueOrDefault(best.MemberId) ?? new List<Member>();

        if (memberCandidates.Count == 1 && best.Score >= _avatarAutoThreshold && margin >= _avatarMinMargin)
        {
            SetMatch(obs, memberCandidates[0], "avatar_auto", best.Score);
            LearnAvatar(obs);
            return;
        }

        var avatarAlternatives = new List<Alternative>();
        foreach (var match in matches)
        {
            if (match.Score < _avatarSuggestionThreshold)
                continue;
            var members = _byId.GetValueOrDefault(match.MemberId) ?? new List<Member>();
            if (members.Count == 1)
                avatarAlternatives.Add(new Alternative(match.MemberId, members[0].Name, match.Score));
        }

        var merged = new Dictionary<int, (string Name, double Score)>();
        foreach (var alt in obs.Alternatives)
            merged[alt.MemberId] = (alt.Name, alt.Score);
        foreach (var alt in avatarAlternatives)
        {
            if (!merged.TryGetValue(alt.MemberId, out var current) || alt.Score > current.Score)
                merged[alt.MemberId] = (alt.Name, alt.Score);
        }
        obs.Alternatives = merged
            .Select(kv => new Alternative(kv.Key, kv.Value.Name, kv.Value.Score))
            .OrderByDescending(a => a.Score)
            .Take(5)
            .ToList();

        if (best.Score >= _avatarSuggestionThreshold)
            obs.Issue = $"Avatar suggests member {best.MemberId} at {best.Score:P0} (margin {margin:P0}); manual review required.";
    }

    public void ManualAssign(Observation obs, int memberId, bool rememberAlias = true)
    {
        var candidates = _byId.GetValueOrDefault(memberId) ?? new List<Member>();
        if (candidates.Count != 1)
            throw new InvalidOperationException($"Member ID {memberId} is not uniquely active");
        SetMatch(obs, candidates[0], "manual", 1.0);
        if (rememberAlias)
        {
            var norm = TextUtil.NormalizeName(obs.RawName);
            if (norm.Length > 0)
                _aliasStore.SaveAlias(norm, obs.RawName, memberId);
        }
        LearnAvatar(obs);
    }
}

public static class ObservationBuilder
{
    public static (List<Observation> Observations, List<string> Issues) FromExtractions(IReadOnlyList<ExtractionResult> results)
    {
        var observations = new List<Observation>();
        var issues = new List<string>();
        foreach (var result in results)
        {
            if (result.Error is not null)
            {
                issues.Add($"{result.FileName}: extraction failed: {result.Error}");
                continue;
            }
            var extraction = result.Extraction!;
            foreach (var row in extraction.Rows)
            {
                var (obs, issue) = MakeObservation(result.ImagePath, extraction.DetectedDay, row, false);
                observations.Add(obs);
                if (issue is not null)
                    issues.Add(issue);
            }
            if (extraction.PinnedRow is not null)
            {
                var (obs, issue) = MakeObservation(result.ImagePath, extraction.DetectedDay, extraction.PinnedRow, true);
                observations.Add(obs);
                if (issue is not null)
                    issues.Add(issue);
            }
            foreach (var warning in extraction.Warnings)
                issues.Add($"{result.FileName}: model warning: {warning}");
        }
        return (observations, issues);
    }

    private static (Observation, string?) MakeObservation(string path, string day, ExtractedRow row, bool pinned)
    {
        (int, int, int, int)? bbox = row.AvatarBBox?.AsTuple();
        var fingerprint = Fingerprinter.FingerprintFromBBox(path, bbox);
        string? issue = null;
        if (bbox is not null && fingerprint is null)
            issue = $"{Path.GetFileName(path)}: could not fingerprint avatar for '{row.RawName}' at rank {row.Rank}.";

        var obs = new Observation
        {
            SourceFile = Path.GetFileName(path),
            Day = day,
            Rank = row.Rank,
            RawPlayerId = row.PlayerId,
            RawName = row.RawName,
            Points = row.Points,
            ExtractionConfidence = row.ExtractionConfidence,
            IsPinnedRow = pinned,
            AvatarBBox = bbox,
            AvatarFingerprint = fingerprint,
        };
        return (obs, issue);
    }
}

public static class WeeklyBuilder
{
    public static WeeklyData Build(List<Observation> observations, List<Member> members, List<string>? baseIssues = null)
    {
        var issues = new List<string>(baseIssues ?? new List<string>());
        var scores = new Dictionary<(int, string), int>();
        var grouped = new Dictionary<(int, string), List<Observation>>();

        foreach (var obs in observations)
        {
            if (obs.MatchedMemberId is null)
            {
                issues.Add($"Unmatched: {obs.Day} rank {obs.Rank} '{obs.RawName}' ({obs.Points:N0}) from {obs.SourceFile}");
                continue;
            }
            var key = (obs.MatchedMemberId.Value, obs.Day);
            if (!grouped.TryGetValue(key, out var list))
                grouped[key] = list = new List<Observation>();
            list.Add(obs);
        }

        foreach (var (key, group) in grouped)
        {
            var values = group.Select(o => o.Points).Distinct().OrderBy(v => v).ToList();
            scores[key] = values.Max();
            if (values.Count > 1)
            {
                var (memberId, day) = key;
                issues.Add(
                    $"Conflicting duplicate scores for member {memberId} on {day}: " +
                    string.Join(", ", values.Select(v => v.ToString("N0"))) + ". Highest value used.");
            }
        }

        var observedDays = observations
            .Select(o => o.Day)
            .Distinct()
            .OrderBy(d => Array.IndexOf(TextUtil.DayOrder, d) is var i && i >= 0 ? i : 99)
            .ToList();

        var missingByDay = new Dictionary<string, List<Member>>();
        foreach (var day in observedDays)
        {
            var matchedIds = scores.Where(kv => kv.Key.Item2 == day).Select(kv => kv.Key.Item1).ToHashSet();
            missingByDay[day] = members.Where(m => !matchedIds.Contains(m.MemberId)).ToList();
        }

        return new WeeklyData(observations, scores, issues, missingByDay);
    }
}
