namespace Avgit.Core.Model;

/// <summary>UI-independent colour used by the commit graph (Core has no Avalonia dependency).</summary>
public readonly struct GraphColor(byte r, byte g, byte b) : IEquatable<GraphColor>
{
    public byte R { get; } = r;
    public byte G { get; } = g;
    public byte B { get; } = b;

    public static GraphColor FromArgb(byte r, byte g, byte b) => new(r, g, b);

    public static GraphColor Default => new(0, 82, 204);
    public static GraphColor Grey => new(127, 127, 127);

    public bool Equals(GraphColor other) => R == other.R && G == other.G && B == other.B;
    public override bool Equals(object? obj) => obj is GraphColor c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(R, G, B);
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

    public static bool operator ==(GraphColor left, GraphColor right) => left.Equals(right);
    public static bool operator !=(GraphColor left, GraphColor right) => !left.Equals(right);
}

/// <summary>How a commit's node is drawn.</summary>
public enum GraphNodeKind : byte
{
    /// <summary>An ordinary commit with at most one parent.</summary>
    Commit,

    /// <summary>A commit with two or more parents.</summary>
    Merge,

    /// <summary>The synthetic "uncommitted changes" row above HEAD.</summary>
    WorkTree
}

/// <summary>
/// The shape of one edge within a single row. A row is split at its vertical
/// middle, where the node sits: edges either cross the whole row or connect the
/// top or bottom boundary with the node.
/// </summary>
public enum GraphEdgeKind : byte
{
    /// <summary>A lane passing the row: top of <see cref="GraphEdge.From"/> to bottom of the same column.</summary>
    Through,

    /// <summary>A lane ending at the node: top of <see cref="GraphEdge.From"/> to the node at <see cref="GraphEdge.To"/>.</summary>
    In,

    /// <summary>An edge from the node at <see cref="GraphEdge.From"/> to the bottom of <see cref="GraphEdge.To"/>.</summary>
    Out,

    /// <summary>
    /// A short edge from the node towards <see cref="GraphEdge.To"/> that stops before the
    /// bottom: the parent is not part of this history (for example hidden by a filter).
    /// </summary>
    Stub
}

/// <summary>One edge of the commit graph as drawn inside a single row.</summary>
/// <param name="Kind">Which boundaries or node the edge connects.</param>
/// <param name="From">Start column (top boundary, or the node for <see cref="GraphEdgeKind.Out"/> and <see cref="GraphEdgeKind.Stub"/>).</param>
/// <param name="To">End column (bottom boundary, or the node for <see cref="GraphEdgeKind.In"/>).</param>
/// <param name="Color">Colour of the lane the edge belongs to.</param>
public readonly record struct GraphEdge(GraphEdgeKind Kind, int From, int To, GraphColor Color)
{
    /// <summary>True when the edge touches the row's top boundary (at <see cref="From"/>).</summary>
    public bool EntersTop => Kind is GraphEdgeKind.Through or GraphEdgeKind.In;

    /// <summary>True when the edge touches the row's bottom boundary (at <see cref="To"/>).</summary>
    public bool LeavesBottom => Kind is GraphEdgeKind.Through or GraphEdgeKind.Out;
}

/// <summary>
/// Everything needed to draw one row of the commit graph, independent of any
/// other row: an edge leaving the bottom of a row at column c always enters the
/// top of the next row at column c with the same colour.
/// </summary>
public sealed class GraphRow(int nodeColumn, GraphColor nodeColor, GraphNodeKind nodeKind, GraphEdge[] edges, int columnCount)
{
    /// <summary>Placeholder for rows that have not been laid out.</summary>
    public static GraphRow Empty { get; } = new(0, GraphColor.Default, GraphNodeKind.Commit, [], 1);

    /// <summary>Column of this row's commit node.</summary>
    public int NodeColumn { get; } = nodeColumn;

    /// <summary>Colour of the node (and of the lane it sits on).</summary>
    public GraphColor NodeColor { get; } = nodeColor;

    public GraphNodeKind NodeKind { get; } = nodeKind;

    public bool IsWorkTree => NodeKind == GraphNodeKind.WorkTree;

    /// <summary>Lanes arriving from the row above in column order, then the node's edges to its parents.</summary>
    public GraphEdge[] Edges { get; } = edges;

    /// <summary>Number of columns this row touches (highest column + 1).</summary>
    public int ColumnCount { get; } = columnCount;
}
