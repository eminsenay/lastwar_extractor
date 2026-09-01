using LastWarExtractor.Views;

namespace LastWarExtractor;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var services = IPlatformApplication.Current!.Services;
		var page = services.GetRequiredService<MainPage>();
		return new Window(page)
		{
			Title = "Last War Weekly Extractor",
			Width = 1240,
			Height = 900,
		};
	}
}