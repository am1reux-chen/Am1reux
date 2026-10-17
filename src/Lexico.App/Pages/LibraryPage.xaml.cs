using Lexio.Services;
using Lexio.ViewModels;
using Lexico.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Lexio.App.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public LibraryViewModel Vm { get; }

    public LibraryPage()
    {
        InitializeComponent();
        Vm = new LibraryViewModel(AppServices.Instance);
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = Vm.SearchAsync(SearchBox.Text);
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (AppServices.Instance.Ready)
            _ = Vm.LoadDecksAsync();
        else
            AppServices.Instance.ReadyChanged += OnReady;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        AppServices.Instance.ReadyChanged -= OnReady;
        _debounce.Stop();
    }

    private void OnReady()
    {
        AppServices.Instance.ReadyChanged -= OnReady;
        _ = Vm.LoadDecksAsync();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            FileTypeFilter = { ".csv", ".txt" },
        };
        // Unpackaged apps must interop-initialize the picker with the owning window.
        if (App.MainAppWindow is not null)
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;
        await Vm.ImportCommand.ExecuteAsync(file.Path);
    }

    private async void OnWordClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResultItem item)
            await AppServices.Instance.Speech.SpeakAsync(item.Word.Lemma);
    }
}
