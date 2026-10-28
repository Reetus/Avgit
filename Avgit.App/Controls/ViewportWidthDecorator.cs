using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Avgit.App.Controls;

/// <summary>
/// Sizes its child to the width of the enclosing <see cref="ScrollViewer"/>'s viewport.
/// Inside a horizontally scrolling list the child would otherwise be measured with
/// infinite width, so content meant to scale down (such as images) would not.
/// A binding to the ancestor's viewport does not work there: virtualized items are
/// built before they are attached, and the ancestor lookup is not retried.
/// </summary>
public sealed class ViewportWidthDecorator : Decorator
{
    private ScrollViewer? _scrollViewer;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollViewer is not null)
        {
            _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        }

        InvalidateMeasure();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scrollViewer is not null)
        {
            _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
            _scrollViewer = null;
        }
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ViewportProperty)
        {
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = _scrollViewer?.Viewport.Width ?? 0;
        if (width > 0)
        {
            availableSize = availableSize.WithWidth(Math.Min(availableSize.Width, width));
        }

        Size desired = base.MeasureOverride(availableSize);
        // Take the whole width so the panels inside share it evenly.
        return width > 0 ? desired.WithWidth(availableSize.Width) : desired;
    }
}
