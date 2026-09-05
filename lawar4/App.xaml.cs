using lawar4.Views;

namespace lawar4;

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
			Title = "lawar4",
			Width = 1240,
			Height = 900,
		};
	}
}