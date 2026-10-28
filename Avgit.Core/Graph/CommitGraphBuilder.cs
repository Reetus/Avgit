using Avgit.Core.Model;

namespace Avgit.Core.Graph;

/// <summary>
/// Lays out the commit graph for history rows given newest first in topological
/// order. Each row gets a <see cref="GraphRow"/> describing its node and every
/// edge that crosses it, so rows can be drawn independently.
/// </summary>
/// <remarks>
/// <para>
/// The builder keeps the open lanes between calls to <see cref="Append"/>, so
/// the next page of history continues exactly where the previous one stopped.
/// An instance is not thread-safe; callers that keep a layout around for paging
/// should <see cref="Clone"/> it before appending on a background thread and
/// only keep the clone once its rows are accepted.
/// </para>
/// <para>
/// Lanes never move sideways: a lane keeps its column from the row where it
/// starts to the row of the commit it leads to, and a freed column is reused by
/// the next lane that needs one. Edges therefore only bend where they join or
/// leave a commit node.
/// </para>
/// </remarks>
public sealed class CommitGraphBuilder
{
    /// <summary>
    /// Lane colours. Mid-tone, saturated hues that stay readable on both light and
    /// dark backgrounds and are far enough apart to tell neighbouring lanes apart.
    /// </summary>
    public static IReadOnlyList<GraphColor> Palette { get; } =
    [
        new(59, 130, 246),  // blue
        new(229, 72, 77),   // red
        new(48, 164, 108),  // green
        new(245, 158, 11),  // amber
        new(142, 78, 198),  // purple
        new(18, 165, 184),  // teal
        new(229, 77, 158),  // pink
        new(249, 115, 22),  // orange
        new(110, 159, 24),  // olive
        new(141, 110, 99)   // brown
    ];

    private readonly List<Lane> _lanes;
    private readonly List<GraphEdge> _scratch = [];
    private int _nextColor;

    /// <summary>Creates an empty layout (first page of a fresh load).</summary>
    /// <param name="stubMissingParents">
    /// When true, an edge to a parent that is not among the rows passed to the same
    /// <see cref="Append"/> call is drawn as a short stub instead of an open lane. Use
    /// this for filtered histories (e.g. by author) where most parents never appear;
    /// otherwise each hidden parent would keep a lane open to the end of the list.
    /// </param>
    public CommitGraphBuilder(bool stubMissingParents = false)
    {
        StubMissingParents = stubMissingParents;
        _lanes = [];
    }

    private CommitGraphBuilder(CommitGraphBuilder source)
    {
        StubMissingParents = source.StubMissingParents;
        _lanes = [.. source._lanes];
        _nextColor = source._nextColor;
    }

    /// <summary>See the constructor.</summary>
    public bool StubMissingParents { get; }

    /// <summary>Number of lanes currently open below the last appended row.</summary>
    public int OpenLaneCount => _lanes.Count(l => l.Target is not null);

    /// <summary>Copies the layout state so a further page can be laid out without changing this one.</summary>
    public CommitGraphBuilder Clone() => new(this);

    /// <summary>Lays out a whole list with a fresh layout.</summary>
    public static CommitGraphBuilder Layout(IReadOnlyList<HistoryRow> rows, bool stubMissingParents = false)
    {
        var builder = new CommitGraphBuilder(stubMissingParents);
        builder.Append(rows);
        return builder;
    }

    /// <summary>
    /// Lays out <paramref name="rows"/> below the rows appended earlier, setting
    /// <see cref="HistoryRow.Graph"/> and <see cref="HistoryRow.Tint"/> on each.
    /// </summary>
    /// <remarks>
    /// A row whose <see cref="HistoryRow.CommitId"/> is empty is the synthetic
    /// work-tree row: it is drawn as a distinct node whose lane leads to its parent (HEAD).
    /// </remarks>
    public void Append(IReadOnlyList<HistoryRow> rows)
    {
        HashSet<string>? present = null;
        if (StubMissingParents)
        {
            present = new HashSet<string>(rows.Count, StringComparer.Ordinal);
            foreach (HistoryRow row in rows)
            {
                if (!string.IsNullOrEmpty(row.CommitId))
                {
                    present.Add(row.CommitId);
                }
            }
        }

        foreach (HistoryRow row in rows)
        {
            LayoutRow(row, present);
        }
    }

    private void LayoutRow(HistoryRow row, HashSet<string>? present)
    {
        var id = row.CommitId;
        var isWorkTree = string.IsNullOrEmpty(id);
        List<GraphEdge> edges = _scratch;
        edges.Clear();

        // Lanes arriving from above: those leading to this commit end at its node
        // (the leftmost becomes the node's column), the rest pass straight through.
        var node = -1;
        var nodeColor = 0;
        for (var i = 0; i < _lanes.Count; i++)
        {
            Lane lane = _lanes[i];
            if (lane.Target is null)
            {
                continue;
            }

            if (!isWorkTree && string.Equals(lane.Target, id, StringComparison.Ordinal))
            {
                if (node < 0)
                {
                    node = i;
                    nodeColor = lane.Color;
                }

                edges.Add(new GraphEdge(GraphEdgeKind.In, i, node, Palette[lane.Color]));
                _lanes[i] = default;
            }
            else
            {
                edges.Add(new GraphEdge(GraphEdgeKind.Through, i, i, Palette[lane.Color]));
            }
        }

        if (node < 0)
        {
            // Branch tip (or work-tree row): nothing above the node.
            node = FirstFree(0);
            nodeColor = PickColor(node, avoid: 0);
        }

        var maxColumn = node;
        string[] parents = row.ParentIds;
        var usedColors = 1 << nodeColor;
        for (var k = 0; k < parents.Length; k++)
        {
            var parent = parents[k];
            if (string.IsNullOrEmpty(parent) || IndexOf(parents, parent, k) >= 0)
            {
                continue;
            }

            if (present is not null && !present.Contains(parent))
            {
                var towards = node + k;
                edges.Add(new GraphEdge(GraphEdgeKind.Stub, node, towards, Palette[k == 0 ? nodeColor : PickColor(towards, usedColors)]));
                maxColumn = Math.Max(maxColumn, towards);
                continue;
            }

            if (k == 0)
            {
                // The first parent continues the node's own lane, keeping its column and colour.
                SetLane(node, new Lane(parent, nodeColor));
                edges.Add(new GraphEdge(GraphEdgeKind.Out, node, node, Palette[nodeColor]));
                continue;
            }

            // Another parent joins the lane already heading for it, or opens a new one.
            var column = FindLane(parent);
            if (column < 0)
            {
                column = FirstFree(node + 1);
                if (column >= _lanes.Count || _lanes[column].Target is not null)
                {
                    column = FirstFree(0);
                }

                SetLane(column, new Lane(parent, PickColor(column, usedColors)));
            }

            var color = _lanes[column].Color;
            usedColors |= 1 << color;
            edges.Add(new GraphEdge(GraphEdgeKind.Out, node, column, Palette[color]));
            maxColumn = Math.Max(maxColumn, column);
        }

        while (_lanes.Count > 0 && _lanes[^1].Target is null)
        {
            _lanes.RemoveAt(_lanes.Count - 1);
        }

        foreach (GraphEdge edge in edges)
        {
            maxColumn = Math.Max(maxColumn, Math.Max(edge.From, edge.To));
        }

        GraphNodeKind kind = isWorkTree ? GraphNodeKind.WorkTree
            : parents.Length > 1 ? GraphNodeKind.Merge
            : GraphNodeKind.Commit;
        GraphColor tint = Palette[nodeColor];
        row.Graph = new GraphRow(node, tint, kind, [.. edges], maxColumn + 1);
        row.Tint = tint;
    }

    private static int IndexOf(string[] values, string value, int before)
    {
        for (var i = 0; i < before; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindLane(string target)
    {
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (string.Equals(_lanes[i].Target, target, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>First free column at or after <paramref name="start"/>; may be one past the open lanes.</summary>
    private int FirstFree(int start)
    {
        for (var i = start; i < _lanes.Count; i++)
        {
            if (_lanes[i].Target is null)
            {
                return i;
            }
        }

        return Math.Max(start, _lanes.Count);
    }

    private void SetLane(int column, Lane lane)
    {
        while (_lanes.Count <= column)
        {
            _lanes.Add(default);
        }

        _lanes[column] = lane;
    }

    /// <summary>
    /// Next palette colour in rotation, skipping colours in <paramref name="avoid"/>
    /// (a bit mask of palette indices) and those of the lanes beside <paramref name="column"/>.
    /// </summary>
    private int PickColor(int column, int avoid)
    {
        avoid |= NeighbourMask(column - 1) | NeighbourMask(column + 1);
        var count = Palette.Count;
        for (var step = 0; step < count; step++)
        {
            var candidate = (_nextColor + step) % count;
            if ((avoid & (1 << candidate)) == 0)
            {
                _nextColor = (candidate + 1) % count;
                return candidate;
            }
        }

        var fallback = _nextColor;
        _nextColor = (fallback + 1) % count;
        return fallback;
    }

    private int NeighbourMask(int column)
        => column >= 0 && column < _lanes.Count && _lanes[column].Target is not null ? 1 << _lanes[column].Color : 0;

    /// <summary>An open lane: the commit it leads to (null when the column is free) and its palette index.</summary>
    private readonly record struct Lane(string? Target, int Color);
}
