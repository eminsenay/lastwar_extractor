namespace LastWarExtractor.Services;

/// <summary>File/folder dialogs. Uses MAUI FilePicker for open, WinUI pickers for folder/save.</summary>
public static class FileDialogs
{
    public static async Task<IReadOnlyList<string>> PickImagesAsync()
    {
        var imageType = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.WinUI] = new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" },
        });
        var result = await FilePicker.Default.PickMultipleAsync(new PickOptions
        {
            PickerTitle = "Choose screenshots",
            FileTypes = imageType,
        });
        return result?.Select(f => f.FullPath).ToList() ?? new List<string>();
    }

    public static async Task<string?> PickXlsxAsync()
    {
        var xlsxType = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.WinUI] = new[] { ".xlsx" },
        });
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Choose roster workbook",
            FileTypes = xlsxType,
        });
        return result?.FullPath;
    }

#if WINDOWS
    public static async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow(picker);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<string?> SaveXlsxAsync(string suggestedName)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = suggestedName,
        };
        picker.FileTypeChoices.Add("Excel workbook", new List<string> { ".xlsx" });
        InitializeWithWindow(picker);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private static void InitializeWithWindow(object target)
    {
        var mauiWindow = Application.Current?.Windows.FirstOrDefault();
        var nativeWindow = mauiWindow?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(target, hwnd);
    }
#else
    public static Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    public static Task<string?> SaveXlsxAsync(string suggestedName) => Task.FromResult<string?>(null);
#endif
}
