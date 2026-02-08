using System.Windows;
using JFStorageTester.Services;

namespace JFStorageTester;

public partial class App : Application
{
    private ThemeService? _themeService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // Initialize theme service and apply Windows theme
        _themeService = ThemeService.Instance;
        _themeService.ApplyWindowsTheme();
        _themeService.StartMonitoringThemeChanges();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _themeService?.StopMonitoringThemeChanges();
        base.OnExit(e);
    }
}
