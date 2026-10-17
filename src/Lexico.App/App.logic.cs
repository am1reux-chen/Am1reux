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
        System.Diagnostics.Debug.WriteLine($"[Lexio] Unhandled: {e.Message}");
        e.Handled = true;
    }

    private void RunStartup()
    {
        AppServices.Initialize();
        MainAppWindow = new MainWindow();
        MainAppWindow.Activate();
    }
}
