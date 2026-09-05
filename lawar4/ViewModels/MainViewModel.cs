using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using lawar4.Models;
using lawar4.Services;

namespace lawar4.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly string[] GeminiModels =
    {
        "gemini-3.8-flash",
        "gemini-3.7-flash",
        "gemini-3.6-flash",
        "gemini-3.5-flash",
        "gemini-3.5-flash-lite",
        "gemini-3.1-flash-lite",
        "gemini-3.1-pro-preview",
        "gemini-3-flash-preview"
    };

    private static readonly string[] OpenAiModels =
    {
        "gpt-6-astra",
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5.6-luna",
    };

    private static readonly string[] LocalModels =
    {
        "ministral-3-3b",
        "olmocr-2-7b",
        "unlimited-ocr",
        "glm-ocr",
        "qwen-3.5-9b",
    };

    private static readonly string[] GeneralModels =
    {
        "qwen-3.8-27b",
        "deepseek-v4-flash",
        "ministral-3-14b-instruct-2512",
    };

    private readonly WorkflowService _service;

    public MainViewModel(WorkflowService service)
    {
        _service = service;
        Providers = new ObservableCollection<string> { "openai", "gemini", "local", "custom" };
        ApiStyles = new ObservableCollection<string> { "responses", "chat" };
        LoadFromConfig();
    }

    public async Task InitializeAsync()
    {
        await _service.InitializeAsync();
        RefreshKeyState();
    }

    // --- Step navigation ---
    [ObservableProperty] private int _activeStep;

    public bool IsSettings => ActiveStep == 0;
    public bool IsRoster => ActiveStep == 1;
    public bool IsScreenshots => ActiveStep == 2;
    public bool IsReview => ActiveStep == 3;
    public bool IsExport => ActiveStep == 4;

    partial void OnActiveStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsRoster));
        OnPropertyChanged(nameof(IsScreenshots));
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(IsExport));
    }

    [RelayCommand]
    private void GoTo(string step) => ActiveStep = int.Parse(step);

    // --- Settings ---
    public ObservableCollection<string> Providers { get; }
    public ObservableCollection<string> ApiStyles { get; }
    public ObservableCollection<string> AvailableModels { get; } = new();
    public ObservableCollection<string> FilteredModels { get; } = new();

    [ObservableProperty] private string _provider = "openai";
    [ObservableProperty] private string _baseUrl = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string? _selectedModelSuggestion;
    [ObservableProperty] private bool _showModelSuggestions;
    [ObservableProperty] private string _apiStyle = "responses";
    [ObservableProperty] private int _requestsPerMinute = 28;
    [ObservableProperty] private bool _useCache = true;
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _apiKeyStatus = "";
    [ObservableProperty] private bool _apiKeyOk;
    [ObservableProperty] private string _settingsMessage = "";
    [ObservableProperty] private bool _settingsIsError;

    private bool _modelEntryFocused;

    public bool IsBaseUrlEditable => Provider is not ("openai" or "gemini");
    public bool IsApiStyleEditable => Provider is not ("openai" or "gemini");
    public bool IsEndpointEditable => IsBaseUrlEditable;
    public string ApiKeyFieldLabel => $"API KEY ({Provider.ToUpperInvariant()})";

    partial void OnModelChanged(string value) => RefreshFilteredModels(value);

    partial void OnSelectedModelSuggestionChanged(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        Model = value;
        ShowModelSuggestions = false;
        SelectedModelSuggestion = null;
    }

    public void OnModelEntryFocusChanged(bool focused)
    {
        _modelEntryFocused = focused;
        if (focused)
            RefreshFilteredModels(Model);
        else
            ShowModelSuggestions = false;
    }

    private void RefreshFilteredModels(string query)
    {
        FilteredModels.Clear();
        var q = query.Trim();
        var matches = q.Length == 0
            ? AvailableModels
            : AvailableModels.Where(m => m.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var m in matches)
            FilteredModels.Add(m);
        if (_modelEntryFocused)
            ShowModelSuggestions = FilteredModels.Count > 0;
    }

    partial void OnProviderChanging(string value)
    {
        // Remember the outgoing provider's current base URL/model, even if settings were never saved,
        // so switching back to it later restores what was showing rather than another provider's leftovers.
        var oldProvider = Provider;
        if (string.IsNullOrWhiteSpace(oldProvider) || oldProvider == value)
            return;
        if (!string.IsNullOrWhiteSpace(BaseUrl))
            _service.Config.BaseUrlsByProvider[oldProvider] = BaseUrl;
        if (!string.IsNullOrWhiteSpace(Model))
            _service.Config.ModelsByProvider[oldProvider] = Model;
    }

    partial void OnProviderChanged(string value)
    {
        OnPropertyChanged(nameof(IsBaseUrlEditable));
        OnPropertyChanged(nameof(IsApiStyleEditable));
        OnPropertyChanged(nameof(IsEndpointEditable));
        OnPropertyChanged(nameof(ApiKeyFieldLabel));

        UpdateAvailableModels(value);

        _service.Config.BaseUrlsByProvider.TryGetValue(value, out var rememberedBaseUrl);

        if (value == "gemini")
        {
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";
            ApiStyle = "chat";
            if (string.IsNullOrWhiteSpace(Model) || OpenAiModels.Contains(Model))
                Model = "gemini-3.5-flash-lite";
        }
        else if (value == "openai")
        {
            BaseUrl = "https://api.openai.com/v1";
            ApiStyle = "responses";
            if (string.IsNullOrWhiteSpace(Model) || GeminiModels.Contains(Model))
                Model = "gpt-5.6-terra";
        }
        else if (value == "local")
        {
            BaseUrl = !string.IsNullOrWhiteSpace(rememberedBaseUrl) ? rememberedBaseUrl : "http://localhost:1234/v1";
            ApiStyle = "chat";
        }
        else
        {
            // "custom": restore the base URL last saved for it instead of leaving another provider's URL behind.
            if (!string.IsNullOrWhiteSpace(rememberedBaseUrl))
                BaseUrl = rememberedBaseUrl;
        }

        // Restore the model last used with this provider, overriding the defaults picked above.
        if (_service.Config.ModelsByProvider.TryGetValue(value, out var remembered) && !string.IsNullOrWhiteSpace(remembered))
            Model = remembered;

        _ = RefreshKeyStatusForProviderAsync(value);
    }

    // Best-effort preview of stored-key status for the provider selected in the UI, before Save is pressed.
    private async Task RefreshKeyStatusForProviderAsync(string provider)
    {
        try
        {
            if (ExtractorService.RequiresApiKey(provider, BaseUrl))
            {
                var key = await _service.GetApiKeyAsync(provider);
                ApiKeyOk = !string.IsNullOrEmpty(key);
                ApiKeyStatus = ApiKeyOk
                    ? $"API key stored securely for {provider}."
                    : $"No API key stored for {provider}. Enter your key above and save settings.";
            }
            else
            {
                ApiKeyOk = true;
                ApiKeyStatus = "This endpoint does not require an API key.";
            }
        }
        catch
        {
            // UI preview only; ignore failures here, actual state refreshes on save.
        }
    }

    private void UpdateAvailableModels(string provider)
    {
        AvailableModels.Clear();
        var list = provider switch
        {
            "gemini" => GeminiModels,
            "openai" => OpenAiModels,
            "local" => LocalModels,
            _ => GeneralModels
        };
        foreach (var m in list)
            AvailableModels.Add(m);

        RefreshFilteredModels(Model);
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            var incoming = new AppConfig
            {
                Provider = Provider,
                Model = Model,
                BaseUrl = BaseUrl,
                ApiStyle = ApiStyle,
                RequestsPerMinute = RequestsPerMinute,
                UseCache = UseCache,
                RosterSourceType = _service.Config.RosterSourceType,
                RosterXlsxPath = _service.Config.RosterXlsxPath,
                RosterGoogleSheetUrl = _service.Config.RosterGoogleSheetUrl,
                RosterSheetName = _service.Config.RosterSheetName,
            };
            _service.SetConfig(incoming);
            await _service.RefreshApiKeyStateAsync();
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                await _service.SetApiKeyAsync(ApiKey);
                ApiKey = "";
            }
            RefreshKeyState();
            SettingsIsError = false;
            SettingsMessage = "Settings saved. Changing model/endpoint uses a fresh extraction cache.";
        }
        catch (Exception ex)
        {
            SettingsIsError = true;
            SettingsMessage = ex.Message;
        }
    }

    private void RefreshKeyState()
    {
        var cfg = _service.Config;
        if (!cfg.ApiKeyRequired)
        {
            ApiKeyOk = true;
            ApiKeyStatus = "This endpoint does not require an API key.";
        }
        else if (cfg.ApiKeyPresent)
        {
            ApiKeyOk = true;
            ApiKeyStatus = $"API key stored securely ({cfg.ApiKeyHint}).";
        }
        else
        {
            ApiKeyOk = false;
            ApiKeyStatus = "No API key stored. Enter your key above and save settings.";
        }
    }

    // --- Roster ---
    [ObservableProperty] private string _rosterSourceType = "xlsx";
    [ObservableProperty] private string _rosterInput = "";
    [ObservableProperty] private string _sheetName = "Members";
    [ObservableProperty] private string _rosterMessage = "";
    [ObservableProperty] private bool _rosterIsError;
    [ObservableProperty] private bool _rosterBusy;

    public ObservableCollection<string> RosterWarnings { get; } = new();

    public bool IsXlsxSource => RosterSourceType == "xlsx";

    partial void OnRosterSourceTypeChanged(string value) => OnPropertyChanged(nameof(IsXlsxSource));

    [RelayCommand]
    private void SelectXlsxSource() => RosterSourceType = "xlsx";

    [RelayCommand]
    private void SelectGoogleSource() => RosterSourceType = "google_sheet";

    [RelayCommand]
    private async Task BrowseRosterAsync()
    {
        var picked = await FileDialogs.PickXlsxAsync();
        if (picked is not null)
            RosterInput = picked;
    }

    [RelayCommand]
    private async Task LoadRosterAsync()
    {
        RosterBusy = true;
        RosterWarnings.Clear();
        try
        {
            await _service.LoadMembersAsync(RosterSourceType, RosterInput, SheetName);
            RosterIsError = false;
            RosterMessage = $"Loaded {_service.Members.Count} members from {_service.MemberSource}";
            foreach (var w in _service.MemberWarnings)
                RosterWarnings.Add(w);
            RefreshObservations();
            RaiseSummary();
        }
        catch (Exception ex)
        {
            RosterIsError = true;
            RosterMessage = ex.Message;
        }
        finally
        {
            RosterBusy = false;
        }
    }

    // --- Screenshots ---
    public ObservableCollection<string> ScreenshotQueue { get; } = new();

    [ObservableProperty] private string _pastedPaths = "";
    [ObservableProperty] private string _extractionMessage = "";
    [ObservableProperty] private bool _isExtracting;
    [ObservableProperty] private double _extractionProgress;
    [ObservableProperty] private string _extractButtonText = "Extract";
    [ObservableProperty] private bool _showManualPaths;

    public string ManualPathsToggleLabel => ShowManualPaths ? "▾ Advanced / Manual file paths" : "▸ Advanced / Manual file paths";

    partial void OnShowManualPathsChanged(bool value) => OnPropertyChanged(nameof(ManualPathsToggleLabel));

    [RelayCommand]
    private void ToggleManualPaths() => ShowManualPaths = !ShowManualPaths;

    /// <summary>Entry point for surfaces outside the normal commands (e.g. native OS drag-and-drop).</summary>
    public void AddScreenshotPaths(IEnumerable<string> paths) => AddPaths(paths);

    [RelayCommand]
    private async Task ChooseScreenshotsAsync()
    {
        var files = await FileDialogs.PickImagesAsync();
        if (files.Count > 0)
            AddPaths(files);
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var folder = await FileDialogs.PickFolderAsync();
        if (folder is not null)
            AddPaths(new[] { folder });
    }

    [RelayCommand]
    private void AddScreenshots()
    {
        var paths = PastedPaths
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (paths.Count > 0)
            AddPaths(paths);
        PastedPaths = "";
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        try
        {
            _service.AddScreenshots(paths);
            RefreshQueue();
            ExtractionMessage = "";
        }
        catch (Exception ex)
        {
            ExtractionMessage = ex.Message;
        }
    }

    private void RefreshQueue()
    {
        ScreenshotQueue.Clear();
        foreach (var path in _service.ScreenshotPaths)
            ScreenshotQueue.Add(Path.GetFileName(path));
        ExtractButtonText = $"Extract {_service.ScreenshotPaths.Count}";
        RaiseSummary();
    }

    [RelayCommand]
    private async Task ExtractAsync()
    {
        if (IsExtracting)
            return;
        var key = await _service.GetApiKeyAsync() ?? "";
        var progress = new Progress<ExtractionProgressUpdate>(update =>
        {
            ExtractionProgress = update.Total == 0 ? 0 : (double)update.Completed / update.Total;
            var status = update.Cached ? "cached" : update.Error is not null ? $"error: {update.Error}" : $"{update.RowCount} rows";
            ExtractionMessage = $"{update.Completed}/{update.Total} · {Path.GetFileName(update.Path)} · {status}";
            ExtractButtonText = $"Extracting {update.Completed}/{update.Total}";
        });

        IsExtracting = true;
        try
        {
            await _service.StartExtractionAsync(progress, key);
            ExtractionMessage = "Extraction complete.";
            RefreshObservations();
            RaiseSummary();
            ActiveStep = 3;
        }
        catch (OperationCanceledException)
        {
            ExtractionMessage = "Extraction cancelled.";
            RefreshObservations();
            RaiseSummary();
        }
        catch (Exception ex)
        {
            ExtractionMessage = ex.Message;
        }
        finally
        {
            IsExtracting = false;
            ExtractButtonText = $"Extract {_service.ScreenshotPaths.Count}";
        }
    }

    [RelayCommand]
    private void CancelExtraction() => _service.CancelExtraction();

    // --- Review ---
    public ObservableCollection<ObservationItem> Observations { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();

    private void RefreshObservations()
    {
        Observations.Clear();
        foreach (var obs in _service.Observations
                     .OrderByDescending(o => o.MatchedMemberId is null)
                     .ThenBy(o => o.Day)
                     .ThenBy(o => o.Rank))
        {
            Observations.Add(new ObservationItem(obs));
        }
        Issues.Clear();
        foreach (var issue in _service.BaseIssues)
            Issues.Add(issue);
    }

    // --- Assign overlay ---
    [ObservableProperty] private bool _assignVisible;
    [ObservableProperty] private string _assignTitle = "";
    [ObservableProperty] private string _assignSubtitle = "";
    [ObservableProperty] private string _memberSearch = "";
    [ObservableProperty] private bool _rememberAlias = true;

    public ObservableCollection<AlternativeItem> Suggestions { get; } = new();
    public ObservableCollection<MemberRow> SearchResults { get; } = new();

    private ObservationItem? _assignTarget;

    [RelayCommand]
    private void OpenAssign(ObservationItem item)
    {
        _assignTarget = item;
        AssignTitle = $"Assign: {item.Name}";
        AssignSubtitle = $"{item.Model.Points:N0} points · {TitleCase(item.Model.Day)}";
        MemberSearch = "";
        RememberAlias = true;
        Suggestions.Clear();
        foreach (var alt in item.Model.Alternatives)
            Suggestions.Add(new AlternativeItem(alt));
        RefreshSearch();
        AssignVisible = true;
    }

    [RelayCommand]
    private void CloseAssign()
    {
        AssignVisible = false;
        _assignTarget = null;
    }

    partial void OnMemberSearchChanged(string value) => RefreshSearch();

    private void RefreshSearch()
    {
        SearchResults.Clear();
        var query = MemberSearch.Trim().ToLowerInvariant();
        var members = _service.Members
            .Where(m => query.Length == 0 || m.Name.ToLowerInvariant().Contains(query) || m.MemberId.ToString().Contains(query))
            .Take(10);
        foreach (var m in members)
            SearchResults.Add(new MemberRow(m.MemberId, $"{m.Name} · {m.MemberId}"));
    }

    [RelayCommand]
    private void AssignMember(int memberId)
    {
        if (_assignTarget is null)
            return;
        try
        {
            _service.AssignObservation(_assignTarget.Model, memberId, RememberAlias);
            AssignVisible = false;
            _assignTarget = null;
            RefreshObservations();
            RaiseSummary();
        }
        catch (Exception ex)
        {
            AssignSubtitle = ex.Message;
        }
    }

    // --- Export ---
    [ObservableProperty] private string _exportMessage = "";

    public int MemberCount => _service.Summary.MemberCount;
    public int ObservationCount => _service.Summary.ObservationCount;
    public int UnmatchedCount => _service.Summary.UnmatchedCount;
    public int ScreenshotCount => _service.Summary.ScreenshotCount;

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(MemberCount));
        OnPropertyChanged(nameof(ObservationCount));
        OnPropertyChanged(nameof(UnmatchedCount));
        OnPropertyChanged(nameof(ScreenshotCount));
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        try
        {
            if (_service.Members.Count == 0 || _service.Observations.Count == 0)
            {
                ExportMessage = "Load members and extract screenshots first.";
                return;
            }
            var suggested = $"weekly_scores_{DateTime.Now:yyyy-MM-dd}.xlsx";
            var target = await FileDialogs.SaveXlsxAsync(suggested);
            if (target is null)
                return;
            var path = await _service.ExportAsync(target);
            ExportMessage = $"Exported to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            ExportMessage = ex.Message;
        }
    }

    private void LoadFromConfig()
    {
        var cfg = _service.Config;
        Provider = cfg.Provider;
        UpdateAvailableModels(cfg.Provider);
        BaseUrl = cfg.BaseUrl;
        Model = cfg.Model;
        ApiStyle = cfg.ApiStyle;
        RequestsPerMinute = cfg.RequestsPerMinute;
        UseCache = cfg.UseCache;
        RosterSourceType = cfg.RosterSourceType;
        RosterInput = cfg.RosterSourceType == "xlsx" ? cfg.RosterXlsxPath : cfg.RosterGoogleSheetUrl;
        SheetName = cfg.RosterSheetName;
        RefreshKeyState();
    }

    private static string TitleCase(string day) =>
        day.Length == 0 ? day : char.ToUpperInvariant(day[0]) + day[1..];
}

public sealed class AlternativeItem
{
    public AlternativeItem(Alternative alt)
    {
        MemberId = alt.MemberId;
        Label = $"{alt.Name} ({alt.Score:P0})";
    }

    public int MemberId { get; }
    public string Label { get; }
}

public sealed record MemberRow(int Id, string Display);
