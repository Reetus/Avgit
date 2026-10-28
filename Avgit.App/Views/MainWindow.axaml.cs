using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Avgit.App.ViewModels;

namespace Avgit.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        this.FindControl<ListBox>("RepositoryTabs")?.AddHandler(
            InputElement.PointerPressedEvent,
            OnRepositoryTabsPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public async Task ShowSettingsAsync()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            var window = new SettingsWindow(vm.SettingsService, vm);
            await window.ShowDialog(this);
        }
    }

    private void OnRepositoryTabsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is not Visual source)
        {
            return;
        }

        if (source.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        if (source.FindAncestorOfType<ListBoxItem>() is not null)
        {
            vm.HideHome();
        }
    }

    private static RepositoryViewModel? TabFrom(object? sender)
        => (sender as MenuItem)?.DataContext as RepositoryViewModel;

    private void OnTabCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && TabFrom(sender) is { } repository)
        {
            vm.CloseRepositoryCommand.Execute(repository);
        }
    }

    private void OnTabCloseOthersClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && TabFrom(sender) is { } repository)
        {
            vm.CloseOtherRepositoriesCommand.Execute(repository);
        }
    }

    private void OnTabCloseAllClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.CloseAllRepositoriesCommand.Execute(null);
        }
    }

    private void OnTabCopyPathClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && TabFrom(sender) is { } repository)
        {
            vm.CopyRepositoryPathCommand.Execute(repository);
        }
    }

    private async void OnAdvancedGitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { Repository: { } repository })
        {
            var window = new AdvancedGitWindow(repository);
            await window.ShowDialog(this);
            await repository.RefreshAsync();
        }
    }
}
