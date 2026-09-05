using lawar4.Services;
using lawar4.ViewModels;
using lawar4.Views;
using Microsoft.Extensions.Logging;
#if WINDOWS
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
using WinColors = Microsoft.UI.Colors;
using WinThickness = Microsoft.UI.Xaml.Thickness;
using WinSolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
#endif

namespace lawar4;

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

#if WINDOWS
		ConfigureWindowsFieldChrome();
#endif

		return builder.Build();
	}

#if WINDOWS
	// Entry/Picker render as a native WinUI TextBox/ComboBox that draws its own background,
	// border and focus outline on top of our InputShell Border, producing a "double box".
	// Strip that native chrome so the InputShell Border is the only visible rectangle.
	private static void ConfigureWindowsFieldChrome()
	{
		var transparent = new WinSolidColorBrush(WinColors.Transparent);

		EntryHandler.Mapper.AppendToMapping("InputShell.FlattenChrome", (handler, _) =>
		{
			if (handler.PlatformView is not TextBox textBox)
				return;

			textBox.BorderThickness = new WinThickness(0);
			textBox.UseSystemFocusVisuals = false;

			foreach (var key in NativeTextBoxChromeResourceKeys)
				textBox.Resources[key] = transparent;
		});

		PickerHandler.Mapper.AppendToMapping("InputShell.FlattenChrome", (handler, _) =>
		{
			if (handler.PlatformView is not ComboBox comboBox)
				return;

			comboBox.BorderThickness = new WinThickness(0);
			comboBox.UseSystemFocusVisuals = false;

			foreach (var key in NativeComboBoxChromeResourceKeys)
				comboBox.Resources[key] = transparent;
		});
	}

	private static readonly string[] NativeTextBoxChromeResourceKeys =
	[
		"TextControlBackground", "TextControlBackgroundPointerOver",
		"TextControlBackgroundFocused", "TextControlBackgroundDisabled",
		"TextControlBorderBrush", "TextControlBorderBrushPointerOver",
		"TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
	];

	private static readonly string[] NativeComboBoxChromeResourceKeys =
	[
		"ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed",
		"ComboBoxBackgroundFocused", "ComboBoxBackgroundDisabled",
		"ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed",
		"ComboBoxBorderBrushFocused", "ComboBoxBorderBrushDisabled",
	];
#endif
}
