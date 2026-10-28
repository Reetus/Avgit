using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.App.Views;

namespace Avgit.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new AppSettingsService();
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

            var dialogs = new DialogService();
            var viewModel = new MainWindowViewModel(settings, dialogs);
            var window = new MainWindow
            {
                DataContext = viewModel
            };

            dialogs.Owner = window;
            AsyncRelayCommand.UnhandledError = ex =>
                _ = dialogs.ShowMessageAsync("Unexpected error", ex.Message);
            viewModel.ExitAction = () => window.Close();
            viewModel.CopyText = text =>
            {
                IClipboard? clipboard = window.Clipboard;
                if (clipboard is not null)
                {
                    _ = clipboard.SetTextAsync(text);
                }
            };
            viewModel.ShowSettingsAction = () => window.ShowSettingsAsync();

            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // Opening a path from the command line is handy for shell integration and
            // for smoke-testing against a fixture repository.
            var startupPath = desktop.Args?.FirstOrDefault(a => !a.StartsWith('-') && (Directory.Exists(a) || File.Exists(a)));
            window.Opened += async (_, _) =>
            {
                if (startupPath is not null)
                {
                    await viewModel.RestoreOpenTabsAsync();
                    await viewModel.OpenPathAsync(startupPath);
                }
                else
                {
                    await viewModel.RestoreOpenTabsAsync();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

