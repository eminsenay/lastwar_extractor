using lawar4.ViewModels;

namespace lawar4.Views;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private bool _initialized;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_initialized)
            return;
        _initialized = true;
        await _viewModel.InitializeAsync();
    }

    private void ModelEntry_Focused(object? sender, FocusEventArgs e) => _viewModel.OnModelEntryFocusChanged(true);

    private async void ModelEntry_Unfocused(object? sender, FocusEventArgs e)
    {
        // Delay so a tap on the suggestion list registers before it's hidden.
        await Task.Delay(150);
        _viewModel.OnModelEntryFocusChanged(false);
    }

    // Native Windows file drop for the Recon drop zone. No cross-platform MAUI abstraction exposes
    // OS file drag-and-drop, so this wires the WinUI element directly (mirrors the existing
    // platform-specific pickers in Services/FileDialogs.cs).
    private void DropZone_Loaded(object? sender, EventArgs e)
    {
#if WINDOWS
        if (DropZone.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement native)
        {
            native.AllowDrop = true;
            native.DragOver -= DropZone_NativeDragOver;
            native.DragOver += DropZone_NativeDragOver;
            native.Drop -= DropZone_NativeDrop;
            native.Drop += DropZone_NativeDrop;
        }
#endif
    }

#if WINDOWS
    private void DropZone_NativeDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void DropZone_NativeDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0)
                _viewModel.AddScreenshotPaths(paths);
        }
        finally
        {
            deferral.Complete();
        }
    }
#endif
}
