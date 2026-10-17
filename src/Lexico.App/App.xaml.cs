namespace Lexio.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    public App()
    {
        InitializeComponent();
        AttachHandlers();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        RunStartup();
    }
}
