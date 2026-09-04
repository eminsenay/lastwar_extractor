using CommunityToolkit.Mvvm.ComponentModel;

namespace LastWarExtractor.Models;

/// <summary>Persisted configuration (roster source + LLM endpoint). Secret API key is NOT stored here.</summary>
public sealed partial class AppConfig : ObservableObject
{
    [ObservableProperty] private string _provider = "openai";
    [ObservableProperty] private string _model = "gpt-5.6-terra";
    [ObservableProperty] private string _baseUrl = "https://api.openai.com/v1";
    [ObservableProperty] private string _apiStyle = "responses";
    [ObservableProperty] private int _requestsPerMinute = 28;
    [ObservableProperty] private bool _useCache = true;
    [ObservableProperty] private string _rosterSourceType = "xlsx";
    [ObservableProperty] private string _rosterXlsxPath = "";
    [ObservableProperty] private string _rosterGoogleSheetUrl = "";
    [ObservableProperty] private string _rosterSheetName = "Members";

    /// <summary>Last model selected per provider, so switching providers restores the previous choice.</summary>
    public Dictionary<string, string> ModelsByProvider { get; set; } = new();

    /// <summary>Last base URL used per provider, so switching providers restores the previous value.</summary>
    public Dictionary<string, string> BaseUrlsByProvider { get; set; } = new();

    // Runtime-only (not persisted), reflects SecureStorage / endpoint state.
    [ObservableProperty] private bool _apiKeyPresent;
    [ObservableProperty] private string _apiKeyHint = "";
    [ObservableProperty] private bool _apiKeyRequired = true;

    public AppConfig Clone() => new()
    {
        Provider = Provider,
        Model = Model,
        BaseUrl = BaseUrl,
        ApiStyle = ApiStyle,
        RequestsPerMinute = RequestsPerMinute,
        UseCache = UseCache,
        RosterSourceType = RosterSourceType,
        RosterXlsxPath = RosterXlsxPath,
        RosterGoogleSheetUrl = RosterGoogleSheetUrl,
        RosterSheetName = RosterSheetName,
        ModelsByProvider = new Dictionary<string, string>(ModelsByProvider),
        BaseUrlsByProvider = new Dictionary<string, string>(BaseUrlsByProvider),
        ApiKeyPresent = ApiKeyPresent,
        ApiKeyHint = ApiKeyHint,
        ApiKeyRequired = ApiKeyRequired,
    };
}

public sealed class AppSummary
{
    public int MemberCount { get; init; }
    public int ScreenshotCount { get; init; }
    public int ObservationCount { get; init; }
    public int UnmatchedCount { get; init; }
    public int FailedFileCount { get; init; }
    public int AvatarMemberCount { get; init; }
    public int AvatarSampleCount { get; init; }
}
