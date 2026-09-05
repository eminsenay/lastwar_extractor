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
}
