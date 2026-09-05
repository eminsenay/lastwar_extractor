using Microsoft.Data.Sqlite;

namespace lawar4.Services;

/// <summary>Shared SQLite database (aliases, extraction cache, avatar fingerprints).</summary>
public sealed class AppDatabase
{
    private readonly string _connectionString;

    public AppDatabase(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
        }.ToString();
        Initialize();
    }

    public SqliteConnection Open()
    {
        var con = new SqliteConnection(_connectionString);
        con.Open();
        return con;
    }

    private void Initialize()
    {
        using var con = Open();
        Execute(con, """
            CREATE TABLE IF NOT EXISTS aliases (
                normalized_alias TEXT PRIMARY KEY,
                alias TEXT NOT NULL,
                member_id INTEGER NOT NULL,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
            """);
        Execute(con, """
            CREATE TABLE IF NOT EXISTS extraction_cache (
                cache_key TEXT PRIMARY KEY,
                result_json TEXT NOT NULL,
                source_name TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
            """);
        Execute(con, """
            CREATE TABLE IF NOT EXISTS avatar_fingerprints (
                member_id INTEGER NOT NULL,
                fingerprint TEXT NOT NULL,
                source_file TEXT NOT NULL,
                match_method TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY(member_id, fingerprint)
            )
            """);
        Execute(con, "CREATE INDEX IF NOT EXISTS idx_avatar_member ON avatar_fingerprints(member_id)");
    }

    internal static void Execute(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

public sealed class AliasStore
{
    private readonly AppDatabase _db;

    public AliasStore(AppDatabase db) => _db = db;

    public int? GetMemberId(string normalizedAlias)
    {
        using var con = _db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT member_id FROM aliases WHERE normalized_alias = $a";
        cmd.Parameters.AddWithValue("$a", normalizedAlias);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    public void SaveAlias(string normalizedAlias, string alias, int memberId)
    {
        using var con = _db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO aliases(normalized_alias, alias, member_id, updated_at)
            VALUES ($n, $a, $m, CURRENT_TIMESTAMP)
            ON CONFLICT(normalized_alias) DO UPDATE SET
                alias=excluded.alias,
                member_id=excluded.member_id,
                updated_at=CURRENT_TIMESTAMP
            """;
        cmd.Parameters.AddWithValue("$n", normalizedAlias);
        cmd.Parameters.AddWithValue("$a", alias);
        cmd.Parameters.AddWithValue("$m", memberId);
        cmd.ExecuteNonQuery();
    }

    public List<(string Alias, int MemberId)> ListAliases()
    {
        using var con = _db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT alias, member_id FROM aliases ORDER BY alias";
        using var reader = cmd.ExecuteReader();
        var list = new List<(string, int)>();
        while (reader.Read())
            list.Add((reader.GetString(0), reader.GetInt32(1)));
        return list;
    }
}

public sealed class ExtractionCache
{
    private readonly AppDatabase _db;

    public ExtractionCache(AppDatabase db) => _db = db;

    public string? Get(string cacheKey)
    {
        using var con = _db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT result_json FROM extraction_cache WHERE cache_key = $k";
        cmd.Parameters.AddWithValue("$k", cacheKey);
        return cmd.ExecuteScalar() as string;
    }

    public void Put(string cacheKey, string sourceName, string resultJson)
    {
        using var con = _db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO extraction_cache(cache_key, result_json, source_name, created_at)
            VALUES ($k, $j, $s, CURRENT_TIMESTAMP)
            ON CONFLICT(cache_key) DO UPDATE SET
                result_json=excluded.result_json,
                source_name=excluded.source_name,
                created_at=CURRENT_TIMESTAMP
            """;
        cmd.Parameters.AddWithValue("$k", cacheKey);
        cmd.Parameters.AddWithValue("$j", resultJson);
        cmd.Parameters.AddWithValue("$s", sourceName);
        cmd.ExecuteNonQuery();
    }

    public void Clear()
    {
        using var con = _db.Open();
        AppDatabase.Execute(con, "DELETE FROM extraction_cache");
    }
}
