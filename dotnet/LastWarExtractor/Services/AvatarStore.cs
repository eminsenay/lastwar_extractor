using LastWarExtractor.Models;

namespace LastWarExtractor.Services;

/// <summary>Persistent avatar reference fingerprints keyed by active member ID.</summary>
public sealed class AvatarStore
{
    private readonly AppDatabase _db;

    public AvatarStore(AppDatabase db) => _db = db;

    public bool SaveReference(int memberId, string? fingerprint, string sourceFile = "", string matchMethod = "")
    {
        if (string.IsNullOrEmpty(fingerprint))
            return false;
        using var con = _db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO avatar_fingerprints(member_id, fingerprint, source_file, match_method, created_at)
            VALUES ($m, $f, $s, $mm, CURRENT_TIMESTAMP)
            """;
        cmd.Parameters.AddWithValue("$m", memberId);
        cmd.Parameters.AddWithValue("$f", fingerprint);
        cmd.Parameters.AddWithValue("$s", sourceFile);
        cmd.Parameters.AddWithValue("$mm", matchMethod);
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<AvatarMatch> BestMatches(string fingerprint, IEnumerable<int>? memberIds = null, int limit = 5)
    {
        HashSet<int>? allowed = memberIds is null ? null : new HashSet<int>(memberIds);
        var grouped = new Dictionary<int, List<string>>();

        using (var con = _db.Open())
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT member_id, fingerprint FROM avatar_fingerprints";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int mid = reader.GetInt32(0);
                if (allowed is not null && !allowed.Contains(mid))
                    continue;
                if (!grouped.TryGetValue(mid, out var list))
                    grouped[mid] = list = new List<string>();
                list.Add(reader.GetString(1));
            }
        }

        var matches = new List<AvatarMatch>();
        foreach (var (memberId, samples) in grouped)
        {
            double score = samples.Max(sample => Fingerprinter.FingerprintSimilarity(fingerprint, sample));
            matches.Add(new AvatarMatch(memberId, score, samples.Count));
        }
        matches.Sort((x, y) => y.Score.CompareTo(x.Score));
        return matches.Take(limit).ToList();
    }

    public (int Members, int Samples) Stats()
    {
        using var con = _db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT member_id), COUNT(*) FROM avatar_fingerprints";
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return (reader.GetInt32(0), reader.GetInt32(1));
        return (0, 0);
    }

    public void Clear()
    {
        using var con = _db.Open();
        AppDatabase.Execute(con, "DELETE FROM avatar_fingerprints");
    }
}
