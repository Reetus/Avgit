using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avgit.App.ViewModels;

namespace Avgit.App.Views;

public partial class DiffView : UserControl
{
    private const double DragThreshold = 3;
    // While a drag is held above or below the list, scroll this often, by up to this
    // many pixels per tick (further from the edge scrolls faster).
    private static readonly TimeSpan AutoScrollInterval = TimeSpan.FromMilliseconds(40);
    private const double AutoScrollMaxStep = 60;

    private DiffLineItem? _contextLine;
    private DiffLineItem? _dragAnchor;
    private Point _pressPosition;
    private bool _pressed;
    private bool _dragging;
    private Point _dragPosition;
    private DispatcherTimer? _autoScroll;

    public DiffView()
    {
        InitializeComponent();
        ItemsControl? items = this.FindControl<ItemsControl>("DiffItems");
        items?.AddHandler(InputElement.PointerPressedEvent, OnDiffPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        items?.AddHandler(InputElement.PointerMovedEvent, OnDiffPointerMoved,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        items?.AddHandler(InputElement.PointerReleasedEvent, OnDiffPointerReleased,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // Losing capture without a release (e.g. the window deactivates) must end the drag,
        // or auto-scroll would keep running.
        items?.PointerCaptureLost += (_, _) => EndDrag();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDiffPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not DiffViewModel viewModel || sender is not ItemsControl items
            || e.Source is not Visual source)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(items);
        var left = point.Properties.IsLeftButtonPressed;
        var right = point.Properties.IsRightButtonPressed;
        if (!left && !right)
        {
            return;
        }

        // The staging checkbox owns its click; clicking it must not change row selection.
        if (left && IsInsideCheckBox(source))
        {
            return;
        }

        DiffLineItem? line = FindLine(source);
        if (right)
        {
            _contextLine = line;
            if (line is not null && !line.IsSelected)
            {
                viewModel.SelectLine(line, extend: false, toggle: false);
            }

            return;
        }

        if (line is null)
        {
            return;
        }

        items.Focus();
        viewModel.SelectLine(line, e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            e.KeyModifiers.HasFlag(KeyModifiers.Control));
        _dragAnchor = viewModel.SelectionAnchor ?? line;
        _pressPosition = point.Position;
        _pressed = true;
        _dragging = false;
        e.Pointer.Capture(items);
        e.Handled = true;
    }

    private void OnDiffPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed || _dragAnchor is null || sender is not ItemsControl items
            || DataContext is not DiffViewModel viewModel
            || !e.GetCurrentPoint(items).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point position = e.GetPosition(items);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _pressPosition.X) < DragThreshold
                && Math.Abs(position.Y - _pressPosition.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
        }

        _dragPosition = position;
        ExtendDragSelection(items, viewModel);
        var outside = position.Y < 0 || position.Y > items.Bounds.Height;
        if (outside && _autoScroll is null)
        {
            _autoScroll = new DispatcherTimer(AutoScrollInterval, DispatcherPriority.Input, (_, _) => AutoScrollTick(items));
            _autoScroll.Start();
        }
        else if (!outside)
        {
            StopAutoScroll();
        }
    }

    /// <summary>
    /// Selects from the drag anchor to the row under the pointer. A pointer above or below
    /// the list (it is captured) counts as being over the first or last visible row.
    /// </summary>
    private void ExtendDragSelection(ItemsControl items, DiffViewModel viewModel)
    {
        if (_dragAnchor is null || items.Bounds.Height < 2 || items.Bounds.Width < 2)
        {
            return;
        }

        var probe = new Point(
            Math.Clamp(_dragPosition.X, 1, items.Bounds.Width - 1),
            Math.Clamp(_dragPosition.Y, 1, items.Bounds.Height - 1));
        if (FindLine(items.InputHitTest(probe) as Visual) is { } line)
        {
            viewModel.SelectRange(_dragAnchor, line);
        }
    }

    private void AutoScrollTick(ItemsControl items)
    {
        if (!_dragging || DataContext is not DiffViewModel viewModel
            || items.FindDescendantOfType<ScrollViewer>() is not { } scroller)
        {
            StopAutoScroll();
            return;
        }

        var beyond = _dragPosition.Y < 0 ? _dragPosition.Y : _dragPosition.Y - items.Bounds.Height;
        var step = Math.Clamp(beyond, -AutoScrollMaxStep, AutoScrollMaxStep);
        var maxY = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var y = Math.Clamp(scroller.Offset.Y + step, 0, maxY);
        if (y == scroller.Offset.Y)
        {
            return;
        }

        scroller.Offset = scroller.Offset.WithY(y);
        // Realise the rows scrolled into view before hit-testing the edge row.
        items.UpdateLayout();
        ExtendDragSelection(items, viewModel);
    }

    private void StopAutoScroll()
    {
        _autoScroll?.Stop();
        _autoScroll = null;
    }

    private void OnDiffPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed)
        {
            e.Pointer.Capture(null);
        }

        EndDrag();
    }

    private void EndDrag()
    {
        _pressed = false;
        _dragging = false;
        _dragAnchor = null;
        StopAutoScroll();
    }

    private void OnDiffKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not DiffViewModel viewModel)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.A)
        {
            viewModel.SelectAllLines();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C)
        {
            viewModel.CopySelectedLines();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            viewModel.ClearSelection();
            e.Handled = true;
        }
    }

    private void OnDiffMenuOpening(object? sender, CancelEventArgs e)
    {
        if (DataContext is not DiffViewModel viewModel || sender is not ContextMenu menu
            || _contextLine is null)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = false;
        var canStage = viewModel.CanStageSelection;
        var canUnstage = viewModel.CanUnstageSelection;
        var canDiscard = viewModel.CanDiscardSelection;
        SetMenuVisible(menu, "stage", canStage);
        SetMenuVisible(menu, "unstage", canUnstage);
        SetMenuVisible(menu, "discard", canDiscard);
        SetMenuVisible(menu, "workseparator", canStage || canUnstage || canDiscard);
    }

    private void OnDiffMenuClosing(object? sender, CancelEventArgs e) => _contextLine = null;

    private void OnCopySelectedLines(object? sender, RoutedEventArgs e)
        => (DataContext as DiffViewModel)?.CopySelectedLines();

    private void OnCopySelectedLinesWithMarkers(object? sender, RoutedEventArgs e)
        => (DataContext as DiffViewModel)?.CopySelectedLinesWithMarkers();

    private void OnCopyNewVersion(object? sender, RoutedEventArgs e)
        => (DataContext as DiffViewModel)?.CopyNewVersion();

    private void OnCopyOldVersion(object? sender, RoutedEventArgs e)
        => (DataContext as DiffViewModel)?.CopyOldVersion();

    private void OnCopyFilePath(object? sender, RoutedEventArgs e)
        => (DataContext as DiffViewModel)?.CopyFilePath(_contextLine);

    private async void OnStageSelectedLines(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiffViewModel viewModel)
        {
            await viewModel.StageSelectedLinesAsync();
        }
    }

    private async void OnUnstageSelectedLines(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiffViewModel viewModel)
        {
            await viewModel.UnstageSelectedLinesAsync();
        }
    }

    private async void OnDiscardSelectedLines(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiffViewModel viewModel)
        {
            await viewModel.DiscardSelectedLinesAsync();
        }
    }

    private static void SetMenuVisible(ContextMenu menu, string tag, bool visible)
    {
        foreach (Control item in menu.Items.OfType<Control>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                item.IsVisible = visible;
            }
        }
    }

    private static bool IsInsideCheckBox(Visual source)
        => source is CheckBox || source.FindAncestorOfType<CheckBox>() is not null;

    private static DiffLineItem? FindLine(Visual? source)
    {
        for (Visual? visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: DiffLineItem line })
            {
                return line;
            }
        }

        return null;
    }
}
