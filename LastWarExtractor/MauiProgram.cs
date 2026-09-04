using LastWarExtractor.Services;
using LastWarExtractor.ViewModels;
using LastWarExtractor.Views;
using Microsoft.Extensions.Logging;

namespace LastWarExtractor;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		builder.Services.AddSingleton<ISecretStore, SecureStorageSecretStore>();
		builder.Services.AddSingleton(sp =>
		{
			var appDir = Path.Combine(FileSystem.AppDataDirectory, "LastWarWeeklyExtractor");
			return new WorkflowService(appDir, sp.GetRequiredService<ISecretStore>());
		});
		builder.Services.AddSingleton<MainViewModel>();
		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif
		// MAUI always probes "Assets/Fonts/{Family}.ttf|otf" for any FontFamily not registered
		// via ConfigureFonts (e.g. the system font "Consolas" used in Theme.xaml). That probe
		// throws on unpackaged Windows apps and is caught internally, but still logs as an
		// Error even though the font resolves correctly. Silence that specific noise.
		builder.Logging.AddFilter("Microsoft.Maui.FontManager", LogLevel.Critical);

		return builder.Build();
	}
}
