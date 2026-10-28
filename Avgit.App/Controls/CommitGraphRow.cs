using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avgit.Core.Model;

namespace Avgit.App.Controls;

/// <summary>
/// Draws one row of the commit graph from its <see cref="GraphRow"/>, produced by
/// Avgit.Core.Graph.CommitGraphBuilder. Each row is self-contained: lanes cross the
/// top and bottom boundaries vertically at their column, so adjacent rows join
/// without gaps however the list is virtualised.
/// </summary>
public sealed class CommitGraphRow : Control
{
    public static readonly StyledProperty<GraphRow?> EntryProperty =
        AvaloniaProperty.Register<CommitGraphRow, GraphRow?>(nameof(Entry));

    public static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<CommitGraphRow, double>(nameof(ColumnWidth), 12.0);

    /// <summary>Left margin before the first column's centre.</summary>
    public const double LeftMargin = 8;

    private const double LineThickness = 2;
    private const double NodeRadius = 4.5;

    private static readonly ConcurrentDictionary<GraphColor, (IBrush Brush, IPen Pen, IPen FadedPen)> Paints = new();
    private static readonly IPen NodeBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(160, 18, 18, 18)), 1);

    static CommitGraphRow()
    {
        AffectsRender<CommitGraphRow>(EntryProperty, ColumnWidthProperty);
    }

    public GraphRow? Entry
    {
        get => GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    /// <summary>Horizontal distance between lane centres.</summary>
    public double ColumnWidth
    {
        get => GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        GraphRow? row = Entry;
        if (row is null)
        {
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var spacing = Math.Max(4, ColumnWidth);
        var mid = height / 2;
        // Anything centred beyond this is fully outside the control.
        var limit = width + spacing;

        double X(int column) => LeftMargin + column * spacing;

        var nodeX = X(row.NodeColumn);
        foreach (GraphEdge edge in row.Edges)
        {
            double fromX = X(edge.From), toX = X(edge.To);
            if (fromX > limit && toX > limit)
            {
                continue;
            }

            (IBrush _, IPen pen, IPen faded) = PaintFor(edge.Color);
            switch (edge.Kind)
            {
                case GraphEdgeKind.Through:
                    context.DrawLine(pen, new Point(fromX, 0), new Point(fromX, height));
                    break;
                case GraphEdgeKind.In:
                    DrawLink(context, pen, new Point(fromX, 0), new Point(nodeX, mid));
                    break;
                case GraphEdgeKind.Out:
                    DrawLink(context, pen, new Point(nodeX, mid), new Point(toX, height));
                    break;
                case GraphEdgeKind.Stub:
                    // Heads towards where the parent would be, then fades out before the boundary.
                    var end = new Point(nodeX + (toX - nodeX) * 0.5, mid + (height - mid) * 0.7);
                    context.DrawLine(faded, new Point(nodeX, mid), end);
                    break;
            }
        }

        if (nodeX - NodeRadius > width)
        {
            return;
        }

        var center = new Point(nodeX, mid);
        (IBrush brush, IPen nodePen, IPen _) = PaintFor(row.NodeColor);
        switch (row.NodeKind)
        {
            case GraphNodeKind.WorkTree:
                context.DrawEllipse(null, new Pen(brush, 1.5, new DashStyle([2, 1.5], 0)), center, NodeRadius, NodeRadius);
                break;
            case GraphNodeKind.Merge:
                // Ring with a dot: same footprint as a commit, but readable as a join.
                context.DrawEllipse(null, nodePen, center, NodeRadius - 0.5, NodeRadius - 0.5);
                context.DrawEllipse(brush, null, center, 2, 2);
                break;
            default:
                context.DrawEllipse(brush, NodeBorder, center, NodeRadius, NodeRadius);
                break;
        }
    }

    /// <summary>
    /// Vertical at both ends so it meets straight lanes cleanly; a straight line
    /// when the columns match.
    /// </summary>
    private static void DrawLink(DrawingContext context, IPen pen, Point from, Point to)
    {
        if (Math.Abs(from.X - to.X) < 0.5)
        {
            context.DrawLine(pen, from, to);
            return;
        }

        var bend = (to.Y - from.Y) / 2;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(from, isFilled: false);
            g.CubicBezierTo(new Point(from.X, from.Y + bend), new Point(to.X, to.Y - bend), to);
            g.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static (IBrush Brush, IPen Pen, IPen FadedPen) PaintFor(GraphColor color)
        => Paints.GetOrAdd(color, static c =>
        {
            var brush = new ImmutableSolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            var faded = new ImmutableSolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B));
            return (brush, new ImmutablePen(brush, LineThickness), new ImmutablePen(faded, LineThickness, new ImmutableDashStyle([1.5, 1.5], 0)));
        });
}
