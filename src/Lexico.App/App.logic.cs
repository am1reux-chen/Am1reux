using Lexio.Services;
using Microsoft.UI.Xaml;

namespace Lexio.App;

/// <summary>Non-XAML partial of App: initialization and global exception handling.</summary>
public partial class App
{
    public static Window? MainAppWindow { get; private set; }

    private void AttachHandlers()
    {
        UnhandledException += OnUnhandled;
    }

    private static void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = StartupFailureReporter.LogUnhandledException(e.Exception, AppServices.StartupLogPath);
    }

    private async void RunStartup()
    {
        try
        {
            await AppServices.InitializeAsync();
            MainAppWindow = new MainWindow();
            MainAppWindow.Activate();
        }
        catch (Exception error)
        {
            var info = StartupFailureReporter.Capture(error, AppServices.DatabasePath,
                AppServices.AutomaticBackupPath, AppServices.StartupLogPath);
            var failureWindow = new StartupErrorWindow(info, TryRestoreFromUserBackupAsync);
            MainAppWindow = failureWindow;
            failureWindow.Activate();
        }
    }

    private static async Task<bool> TryRestoreFromUserBackupAsync(string backupPath)
    {
        try
        {
            await AppServices.InitializeAsync(backupPath);
            var mainWindow = new MainWindow();
            MainAppWindow = mainWindow;
            mainWindow.Activate();
            return true;
        }
        catch (Exception error)
        {
            StartupFailureReporter.WriteTechnicalError(error, AppServices.StartupLogPath, "user-selected backup recovery failed");
            return false;
        }
    }
}
