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

		return builder.Build();
	}
}
