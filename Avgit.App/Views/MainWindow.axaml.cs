using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Avgit.App.ViewModels;

namespace Avgit.App.Views;

public partial class MainWindow : Window
{
    /// <summary>Pixels the tab strip moves per mouse wheel notch.</summary>
    private const double TabWheelStep = 90;

    private ScrollViewer? _tabScroller;

    public MainWindow()
    {
        InitializeComponent();
        if (this.FindControl<ListBox>("RepositoryTabs") is { } tabs)
        {
            tabs.AddHandler(
                InputElement.PointerPressedEvent,
                OnRepositoryTabsPointerPressed,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            tabs.AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnRepositoryTabsWheel,
                RoutingStrategies.Tunnel);
            tabs.TemplateApplied += OnRepositoryTabsTemplateApplied;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnRepositoryTabsTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (_tabScroller is not null)
        {
            _tabScroller.PropertyChanged -= OnTabScrollerPropertyChanged;
        }

        _tabScroller = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        if (_tabScroller is not null)
        {
            _tabScroller.PropertyChanged += OnTabScrollerPropertyChanged;
            UpdateTabOverflow();
        }
    }

    private void OnTabScrollerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)
        {
            UpdateTabOverflow();
        }
    }

    /// <summary>
    /// Shows the "all repositories" button only while some tabs are scrolled out of view.
    /// Showing it narrows the strip, which can only keep the tabs overflowing, so this settles.
    /// </summary>
    private void UpdateTabOverflow()
    {
        if (_tabScroller is null || this.FindControl<Button>("RepositoryTabsOverflow") is not { } overflow)
        {
            return;
        }

        // Half a pixel of slack so layout rounding does not count as overflow.
        overflow.IsVisible = _tabScroller.Extent.Width > _tabScroller.Viewport.Width + 0.5;
    }

    /// <summary>The strip only scrolls sideways, so a plain vertical wheel moves it too.</summary>
    private void OnRepositoryTabsWheel(object? sender, PointerWheelEventArgs e)
    {
        // Sideways wheels and touchpads already scroll horizontally on their own.
        if (_tabScroller is null || e.Delta.Y == 0 || e.Delta.X != 0)
        {
            return;
        }

        var max = _tabScroller.Extent.Width - _tabScroller.Viewport.Width;
        if (max <= 0)
        {
            return;
        }

        var x = Math.Clamp(_tabScroller.Offset.X - (e.Delta.Y * TabWheelStep), 0, max);
        _tabScroller.Offset = _tabScroller.Offset.WithX(x);
        e.Handled = true;
    }

    private void OnRepositoryOverflowOpened(object? sender, EventArgs e)
    {
        if (sender is Flyout { Content: ListBox list } && DataContext is MainWindowViewModel vm)
        {
            list.SelectedItem = vm.Repository;
            if (vm.Repository is not null)
            {
                list.ScrollIntoView(vm.Repository);
            }

            list.Focus();
        }
    }

    private void OnRepositoryOverflowTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext
            is RepositoryViewModel repository)
        {
            ShowRepositoryFromOverflow(repository);
        }
    }

    private void OnRepositoryOverflowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox { SelectedItem: RepositoryViewModel repository })
        {
            ShowRepositoryFromOverflow(repository);
            e.Handled = true;
        }
    }

    private void ShowRepositoryFromOverflow(RepositoryViewModel repository)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // Selecting the tab also scrolls it into view in the strip.
        vm.Repository = repository;
        vm.HideHome();
        this.FindControl<Button>("RepositoryTabsOverflow")?.Flyout?.Hide();
    }

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
