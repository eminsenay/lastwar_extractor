using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LastWarExtractor.Models;

namespace LastWarExtractor.Services;

public sealed record ExtractionProgressUpdate(
    int Completed, int Total, string Path, bool Cached, string? Error, string? DetectedDay, int? RowCount);

/// <summary>
/// UI-neutral workflow state and orchestration. In-process replacement for the Python
/// backend service (no sidecar / JSON-RPC).
/// </summary>
public sealed class WorkflowService
{
    private const string DefaultModel = "gpt-5.6-terra";
    private const string DefaultBaseUrl = "https://api.openai.com/v1";
    private static readonly string[] SupportedImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

    private readonly ISecretStore _secrets;
    private readonly string _configPath;
    private readonly AliasStore _aliasStore;
    private readonly AvatarStore _avatarStore;
    private readonly ExtractionCache _extractionCache;
    private readonly ExtractorService _extractor = new();

    private MemberMatcher? _matcher;
    private CancellationTokenSource? _extractionCts;

    public WorkflowService(string appDir, ISecretStore secrets)
    {
        _secrets = secrets;
        Directory.CreateDirectory(appDir);
        var db = new AppDatabase(Path.Combine(appDir, "app.sqlite3"));
        _aliasStore = new AliasStore(db);
        _avatarStore = new AvatarStore(db);
        _extractionCache = new ExtractionCache(db);
        _configPath = Path.Combine(appDir, "config.json");
        Config = LoadConfig();
    }

    // --- State ---
    public AppConfig Config { get; private set; }
    public List<Member> Members { get; private set; } = new();
    public string MemberSource { get; private set; } = "";
    public List<string> MemberWarnings { get; private set; } = new();
    public List<string> ScreenshotPaths { get; private set; } = new();
    public List<ExtractionResult> ExtractionResults { get; private set; } = new();
    public List<Observation> Observations { get; private set; } = new();
    public List<string> BaseIssues { get; private set; } = new();

    public bool IsExtracting => _extractionCts is not null;

    public AppSummary Summary
    {
        get
        {
            var (avatarMembers, avatarSamples) = _avatarStore.Stats();
            return new AppSummary
            {
                MemberCount = Members.Count,
                ScreenshotCount = ScreenshotPaths.Count,
                ObservationCount = Observations.Count,
                UnmatchedCount = Observations.Count(o => o.MatchedMemberId is null),
                FailedFileCount = ExtractionResults.Count(r => r.Error is not null),
                AvatarMemberCount = avatarMembers,
                AvatarSampleCount = avatarSamples,
            };
        }
    }

    // --- Config ---
    public async Task InitializeAsync()
    {
        await RefreshApiKeyStateAsync().ConfigureAwait(false);
    }

    /// <summary>Reloads ApiKeyPresent/Hint from the secret store for the currently configured provider.</summary>
    public async Task RefreshApiKeyStateAsync()
    {
        var key = await _secrets.GetApiKeyAsync(Config.Provider).ConfigureAwait(false);
        ApplyKeyState(key);
    }

    private void ApplyKeyState(string? key)
    {
        Config.ApiKeyPresent = !string.IsNullOrEmpty(key);
        Config.ApiKeyHint = string.IsNullOrEmpty(key) ? "" : MaskSecret(key!);
        Config.ApiKeyRequired = !ExtractorService.IsLocalEndpoint(Config.BaseUrl);
    }

    /// <summary>Stores an API key for the currently configured provider.</summary>
    public async Task SetApiKeyAsync(string key)
    {
        key = key.Trim();
        await _secrets.SetApiKeyAsync(Config.Provider, key).ConfigureAwait(false);
        ApplyKeyState(key);
    }

    /// <summary>Gets the API key stored for the currently configured provider.</summary>
    public Task<string?> GetApiKeyAsync() => _secrets.GetApiKeyAsync(Config.Provider);

    /// <summary>Gets the API key stored for an arbitrary provider (used for live UI previews before saving).</summary>
    public Task<string?> GetApiKeyAsync(string provider) => _secrets.GetApiKeyAsync(provider);

    public void SetConfig(AppConfig incoming)
    {
        if (IsExtracting)
            throw new InvalidOperationException("Cannot change configuration during extraction");

        var model = incoming.Model.Trim();
        var baseUrl = incoming.BaseUrl.Trim();
        var apiStyle = incoming.ApiStyle.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(baseUrl))
            throw new ArgumentException("model and baseUrl are required");
        if (apiStyle is not ("responses" or "chat"))
            throw new ArgumentException("apiStyle must be 'responses' or 'chat'");
        if (incoming.RequestsPerMinute is < 1 or > 30)
            throw new ArgumentException("requestsPerMinute must be between 1 and 30");

        var provider = incoming.Provider.Trim().ToLowerInvariant();
        if (provider is not ("openai" or "gemini" or "local" or "custom"))
        {
            var clean = baseUrl.TrimEnd('/');
            provider = clean == "https://api.openai.com/v1" ? "openai"
                : clean.StartsWith("https://generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ? "gemini"
                : clean == "http://localhost:1234/v1" ? "local"
                : "custom";
        }

        Config.Provider = provider;
        Config.Model = model;
        Config.BaseUrl = baseUrl;
        Config.ApiStyle = apiStyle;
        Config.RequestsPerMinute = incoming.RequestsPerMinute;
        Config.UseCache = incoming.UseCache;
        Config.RosterSourceType = incoming.RosterSourceType;
        Config.RosterXlsxPath = incoming.RosterXlsxPath;
        Config.RosterGoogleSheetUrl = incoming.RosterGoogleSheetUrl;
        Config.RosterSheetName = string.IsNullOrWhiteSpace(incoming.RosterSheetName) ? "Members" : incoming.RosterSheetName;
        Config.ApiKeyRequired = !ExtractorService.IsLocalEndpoint(baseUrl);

        SaveConfig();
    }

    private AppConfig LoadConfig()
    {
        var config = new AppConfig
        {
            Model = DefaultModel,
            BaseUrl = DefaultBaseUrl,
        };
        try
        {
            if (File.Exists(_configPath))
            {
                var saved = JsonSerializer.Deserialize<PersistedConfig>(File.ReadAllText(_configPath));
                if (saved is not null)
                {
                    config.Provider = saved.Provider ?? config.Provider;
                    config.Model = saved.Model ?? config.Model;
                    config.BaseUrl = saved.BaseUrl ?? config.BaseUrl;
                    config.ApiStyle = saved.ApiStyle ?? config.ApiStyle;
                    config.RequestsPerMinute = saved.RequestsPerMinute ?? config.RequestsPerMinute;
                    config.UseCache = saved.UseCache ?? config.UseCache;
                    config.RosterSourceType = saved.RosterSourceType ?? config.RosterSourceType;
                    config.RosterXlsxPath = saved.RosterXlsxPath ?? config.RosterXlsxPath;
                    config.RosterGoogleSheetUrl = saved.RosterGoogleSheetUrl ?? config.RosterGoogleSheetUrl;
                    config.RosterSheetName = saved.RosterSheetName ?? config.RosterSheetName;
                }
            }
        }
        catch
        {
            // Corrupt config falls back to defaults.
        }
        config.ApiKeyRequired = !ExtractorService.IsLocalEndpoint(config.BaseUrl);
        return config;
    }

    private void SaveConfig()
    {
        var persisted = new PersistedConfig
        {
            Provider = Config.Provider,
            Model = Config.Model,
            BaseUrl = Config.BaseUrl,
            ApiStyle = Config.ApiStyle,
            RequestsPerMinute = Config.RequestsPerMinute,
            UseCache = Config.UseCache,
            RosterSourceType = Config.RosterSourceType,
            RosterXlsxPath = Config.RosterXlsxPath,
            RosterGoogleSheetUrl = Config.RosterGoogleSheetUrl,
            RosterSheetName = Config.RosterSheetName,
        };
        File.WriteAllText(_configPath, JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true }));
    }

    // --- Members ---
    public async Task LoadMembersAsync(string sourceType, string source, string sheetName)
    {
        source = source.Trim();
        sheetName = string.IsNullOrWhiteSpace(sheetName) ? "Members" : sheetName.Trim();

        MemberLoadResult result;
        if (sourceType == "xlsx")
        {
            result = await Task.Run(() => MembersLoader.LoadFromXlsx(source, sheetName)).ConfigureAwait(false);
            Config.RosterSourceType = "xlsx";
            Config.RosterXlsxPath = source;
        }
        else if (sourceType == "google_sheet")
        {
            result = await MembersLoader.LoadFromGoogleSheetAsync(source, sheetName).ConfigureAwait(false);
            Config.RosterSourceType = "google_sheet";
            Config.RosterGoogleSheetUrl = source;
        }
        else
        {
            throw new ArgumentException("source_type must be 'xlsx' or 'google_sheet'");
        }
        Config.RosterSheetName = sheetName;

        Members = result.Members;
        MemberSource = result.SourceDescription;
        MemberWarnings = result.Warnings;
        _matcher = new MemberMatcher(Members, _aliasStore, _avatarStore);
        if (ExtractionResults.Count > 0)
            RebuildMatches();
        SaveConfig();
    }

    // --- Screenshots ---
    public void AddScreenshots(IEnumerable<string> rawPaths)
    {
        var collected = new List<string>(ScreenshotPaths);
        foreach (var raw in rawPaths)
        {
            var path = Path.GetFullPath(raw);
            if (Directory.Exists(path))
            {
                foreach (var child in Directory.EnumerateFiles(path).OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (SupportedImageExtensions.Contains(Path.GetExtension(child).ToLowerInvariant()))
                        collected.Add(child);
                }
            }
            else if (File.Exists(path) && SupportedImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                collected.Add(path);
            }
            else
            {
                throw new ArgumentException($"Unsupported or missing screenshot path: {raw}");
            }
        }
        ScreenshotPaths = collected.Distinct().ToList();
    }

    public void ClearScreenshots()
    {
        if (IsExtracting)
            throw new InvalidOperationException("Cancel extraction before clearing screenshots");
        ScreenshotPaths = new List<string>();
        ExtractionResults = new List<ExtractionResult>();
        Observations = new List<Observation>();
        BaseIssues = new List<string>();
    }

    // --- Extraction ---
    public async Task StartExtractionAsync(IProgress<ExtractionProgressUpdate> progress, string apiKey)
    {
        if (IsExtracting)
            throw new InvalidOperationException("An extraction is already running");
        if (Members.Count == 0)
            throw new InvalidOperationException("Load members before extracting screenshots");
        if (ScreenshotPaths.Count == 0)
            throw new InvalidOperationException("Add screenshots before extracting");

        var paths = new List<string>(ScreenshotPaths);
        ExtractionResults = new List<ExtractionResult>();
        Observations = new List<Observation>();
        BaseIssues = new List<string>();
        _extractionCts = new CancellationTokenSource();
        var ct = _extractionCts.Token;

        try
        {
            var cached = new Dictionary<string, ExtractionResult>();
            var uncached = new List<string>();
            foreach (var path in paths)
            {
                var raw = Config.UseCache ? _extractionCache.Get(CacheKey(path)) : null;
                if (raw is not null)
                {
                    try
                    {
                        var extraction = JsonSerializer.Deserialize<ScreenshotExtraction>(raw);
                        if (extraction is not null)
                        {
                            cached[path] = new ExtractionResult(path, extraction, null);
                            continue;
                        }
                    }
                    catch
                    {
                        // fall through to re-extract
                    }
                }
                uncached.Add(path);
            }

            int completed = 0;
            foreach (var path in paths)
            {
                if (cached.ContainsKey(path))
                {
                    completed++;
                    progress.Report(new ExtractionProgressUpdate(completed, paths.Count, path, true, null, null, null));
                }
            }

            var freshByPath = new Dictionary<string, ExtractionResult>();
            if (uncached.Count > 0 && !ct.IsCancellationRequested)
            {
                var config = new ExtractionConfig
                {
                    Provider = Config.Provider,
                    Model = Config.Model,
                    BaseUrl = Config.BaseUrl,
                    ApiKey = apiKey,
                    ApiStyle = Config.ApiStyle,
                    RequestsPerMinute = Config.RequestsPerMinute,
                };

                await _extractor.ExtractManyAsync(uncached, config, (_, _, result) =>
                {
                    completed++;
                    freshByPath[result.ImagePath] = result;
                    if (result.Extraction is not null && result.Error is null)
                        _extractionCache.Put(CacheKey(result.ImagePath), result.FileName, JsonSerializer.Serialize(result.Extraction));
                    progress.Report(new ExtractionProgressUpdate(
                        completed, paths.Count, result.ImagePath, false, result.Error,
                        result.Extraction?.DetectedDay, result.Extraction?.Rows.Count ?? 0));
                }, ct).ConfigureAwait(false);
            }

            ExtractionResults = paths
                .Where(p => cached.ContainsKey(p) || freshByPath.ContainsKey(p))
                .Select(p => cached.GetValueOrDefault(p) ?? freshByPath[p])
                .ToList();
            RebuildMatches();
        }
        finally
        {
            _extractionCts?.Dispose();
            _extractionCts = null;
        }
    }

    public void CancelExtraction() => _extractionCts?.Cancel();

    private void RebuildMatches()
    {
        if (_matcher is null)
            return;
        var (observations, issues) = ObservationBuilder.FromExtractions(ExtractionResults);
        Observations = observations;
        BaseIssues = issues;
        foreach (var obs in Observations)
            _matcher.MatchDeterministic(obs);
        foreach (var obs in Observations.Where(o => o.MatchedMemberId is not null))
            _matcher.LearnAvatar(obs);
        foreach (var obs in Observations.Where(o => o.MatchedMemberId is null))
            _matcher.MatchAvatar(obs);
    }

    // --- Review ---
    public void AssignObservation(Observation observation, int memberId, bool rememberAlias)
    {
        if (_matcher is null)
            throw new InvalidOperationException("Load members before assigning observations");
        _matcher.ManualAssign(observation, memberId, rememberAlias);
    }

    // --- Export ---
    public async Task<string> ExportAsync(string outputPath)
    {
        if (Members.Count == 0)
            throw new InvalidOperationException("Load members before exporting");
        if (Observations.Count == 0)
            throw new InvalidOperationException("Extract screenshots before exporting");

        var path = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, ".xlsx");

        var weekly = WeeklyBuilder.Build(Observations, Members, BaseIssues);
        await Task.Run(() => ExcelExporter.ExportWeeklyWorkbook(path, Members, weekly, _aliasStore, MemberSource)).ConfigureAwait(false);
        return path;
    }

    private string CacheKey(string path)
    {
        using var sha = SHA256.Create();
        var bytes = new List<byte>();
        bytes.AddRange(File.ReadAllBytes(path));
        bytes.AddRange(Encoding.UTF8.GetBytes(Config.Model));
        bytes.AddRange(Encoding.UTF8.GetBytes(Config.BaseUrl));
        bytes.AddRange(Encoding.ASCII.GetBytes(Config.ApiStyle));
        bytes.AddRange(Encoding.ASCII.GetBytes(ExtractionPrompt.CacheVersion));
        return Convert.ToHexString(sha.ComputeHash(bytes.ToArray())).ToLowerInvariant();
    }

    private static string MaskSecret(string value)
    {
        if (value.Length <= 8)
            return new string('*', value.Length);
        return $"{value[..4]}{new string('*', value.Length - 8)}{value[^4..]}";
    }

    private sealed class PersistedConfig
    {
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiStyle { get; set; }
        public int? RequestsPerMinute { get; set; }
        public bool? UseCache { get; set; }
        public string? RosterSourceType { get; set; }
        public string? RosterXlsxPath { get; set; }
        public string? RosterGoogleSheetUrl { get; set; }
        public string? RosterSheetName { get; set; }
    }
}
