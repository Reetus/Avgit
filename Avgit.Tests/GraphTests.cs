using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avgit.App.Controls;
using Avgit.Core.Graph;
using Avgit.Core.Model;

namespace Avgit.Tests;

public class GraphTests
{
    [Fact]
    public void Linear_history_stays_in_one_column_and_colour()
    {
        List<HistoryRow> rows = Rows(("4", ["3"]), ("3", ["2"]), ("2", ["1"]), ("1", []));

        CommitGraphBuilder.Layout(rows);

        Assert.All(rows, r =>
        {
            Assert.Equal(0, r.Graph.NodeColumn);
            Assert.Equal(1, r.Graph.ColumnCount);
            Assert.Equal(GraphNodeKind.Commit, r.Graph.NodeKind);
            Assert.Equal(rows[0].Graph.NodeColor, r.Graph.NodeColor);
            Assert.Equal(r.Graph.NodeColor, r.Tint);
        });
        AssertContinuous(rows);
    }

    [Fact]
    public void Lanes_start_at_the_tip_and_end_at_the_root()
    {
        List<HistoryRow> rows = Rows(("tip", ["mid"]), ("mid", ["root"]), ("root", []));

        CommitGraphBuilder.Layout(rows);

        // The branch tip has nothing above its node.
        GraphEdge tip = Assert.Single(rows[0].Graph.Edges);
        Assert.Equal(new GraphEdge(GraphEdgeKind.Out, 0, 0, rows[0].Graph.NodeColor), tip);
        Assert.Empty(Top(rows[0].Graph));

        // A middle commit's lane arrives from above and continues below.
        Assert.Equal([GraphEdgeKind.In, GraphEdgeKind.Out], rows[1].Graph.Edges.Select(e => e.Kind));

        // The root's lane ends at its node.
        GraphEdge root = Assert.Single(rows[2].Graph.Edges);
        Assert.Equal(GraphEdgeKind.In, root.Kind);
        Assert.Empty(Bottom(rows[2].Graph));
    }

    [Fact]
    public void Merge_commit_opens_a_second_lane_in_another_colour()
    {
        List<HistoryRow> rows = Rows(("m", ["a", "b"]), ("a", ["root"]), ("b", ["root"]), ("root", []));

        CommitGraphBuilder.Layout(rows);

        GraphRow merge = rows[0].Graph;
        Assert.Equal(GraphNodeKind.Merge, merge.NodeKind);
        Assert.Equal(0, merge.NodeColumn);
        GraphEdge[] outs = [.. merge.Edges.Where(e => e.Kind == GraphEdgeKind.Out)];
        Assert.Equal([0, 1], outs.Select(e => e.To));
        Assert.All(outs, e => Assert.Equal(0, e.From));
        Assert.NotEqual(outs[0].Color, outs[1].Color);
        Assert.Equal(merge.NodeColor, outs[0].Color);

        Assert.Equal(0, rows[1].Graph.NodeColumn);
        Assert.Equal(1, rows[2].Graph.NodeColumn);
        Assert.Equal(outs[1].Color, rows[2].Graph.NodeColor);

        // Both lanes meet again at the fork point.
        GraphRow root = rows[3].Graph;
        Assert.Equal(0, root.NodeColumn);
        Assert.Equal([0, 1], root.Edges.Where(e => e.Kind == GraphEdgeKind.In).Select(e => e.From));
        AssertContinuous(rows);
    }

    [Fact]
    public void Octopus_merge_gets_distinct_lane_colours()
    {
        List<HistoryRow> rows = Rows(("o", ["a", "b", "c"]), ("a", ["root"]), ("b", ["root"]), ("c", ["root"]), ("root", []));

        CommitGraphBuilder.Layout(rows);

        GraphEdge[] outs = [.. rows[0].Graph.Edges.Where(e => e.Kind == GraphEdgeKind.Out)];
        Assert.Equal(3, outs.Length);
        Assert.Equal(3, outs.Select(e => e.Color).Distinct().Count());
        Assert.Equal(3, outs.Select(e => e.To).Distinct().Count());
        Assert.Equal([0, 0, 1, 2], rows.Take(4).Select(r => r.Graph.NodeColumn));
        AssertContinuous(rows);
    }

    [Fact]
    public void Colours_are_stable_along_a_branch()
    {
        List<HistoryRow> rows = Rows(("m", ["a", "x"]), ("x", ["y"]), ("a", ["b"]), ("y", ["b"]), ("b", []));

        CommitGraphBuilder.Layout(rows);

        GraphColor side = rows[1].Graph.NodeColor;
        Assert.Equal(side, rows[3].Graph.NodeColor);
        Assert.Equal(rows[0].Graph.NodeColor, rows[2].Graph.NodeColor);
        Assert.NotEqual(rows[0].Graph.NodeColor, side);
        // The side lane keeps its colour while it passes a's row.
        Assert.Contains(rows[2].Graph.Edges, e => e is { Kind: GraphEdgeKind.Through, From: 1 } && e.Color == side);
    }

    [Fact]
    public void Missing_parents_run_off_the_bottom_without_extra_nodes()
    {
        List<HistoryRow> rows = Rows(("m", ["a", "gone1"]), ("a", ["gone2"]));

        CommitGraphBuilder builder = CommitGraphBuilder.Layout(rows);

        Assert.Equal([0, 1], Bottom(rows[1].Graph).Select(b => b.Column));
        Assert.Equal(2, builder.OpenLaneCount);
        Assert.All(rows, r => Assert.Equal(0, r.Graph.NodeColumn));
        AssertContinuous(rows);
    }

    [Fact]
    public void Filtered_history_draws_stubs_for_hidden_parents()
    {
        List<HistoryRow> rows = Rows(("c3", ["hidden3"]), ("c2", ["c1", "hidden2"]), ("c1", ["hidden1"]));

        CommitGraphBuilder builder = CommitGraphBuilder.Layout(rows, stubMissingParents: true);

        Assert.Equal(0, builder.OpenLaneCount);
        Assert.All(rows, r => Assert.Equal(0, r.Graph.NodeColumn));
        Assert.Equal(GraphEdgeKind.Stub, Assert.Single(rows[0].Graph.Edges).Kind);
        Assert.Contains(rows[1].Graph.Edges, e => e.Kind == GraphEdgeKind.Stub);
        Assert.Contains(rows[1].Graph.Edges, e => e is { Kind: GraphEdgeKind.Out, To: 0 });
        Assert.Empty(Bottom(rows[2].Graph));
        AssertContinuous(rows);
    }

    [Fact]
    public void Column_freed_by_an_ended_lane_is_reused()
    {
        // s merges into a at a's row, freeing column 1 before the later tip t.
        List<HistoryRow> rows = Rows(("m", ["a", "s"]), ("s", ["a"]), ("a", ["b"]), ("t", ["b"]), ("b", []));

        CommitGraphBuilder.Layout(rows);

        Assert.Equal(1, rows[1].Graph.NodeColumn);
        Assert.DoesNotContain(Bottom(rows[2].Graph), b => b.Column == 1);
        Assert.Equal(1, rows[3].Graph.NodeColumn);
        Assert.DoesNotContain(Top(rows[3].Graph), t => t.Column == 1);
        Assert.All(rows, r => Assert.True(r.Graph.ColumnCount <= 2));
        AssertContinuous(rows);
    }

    [Fact]
    public void Work_tree_row_sits_above_head()
    {
        var workTree = new HistoryRow { Id = string.Empty, CommitId = string.Empty, ParentIds = ["head"], Title = "Uncommitted changes" };
        List<HistoryRow> rows = [workTree, .. Rows(("other", ["base"]), ("head", ["base"]), ("base", []))];

        CommitGraphBuilder.Layout(rows);

        GraphRow wt = rows[0].Graph;
        Assert.True(wt.IsWorkTree);
        Assert.Empty(Top(wt));
        Assert.Equal(new GraphEdge(GraphEdgeKind.Out, wt.NodeColumn, wt.NodeColumn, wt.NodeColor), Assert.Single(wt.Edges));
        GraphRow head = rows[2].Graph;
        Assert.Equal(GraphNodeKind.Commit, head.NodeKind);
        Assert.Equal(wt.NodeColumn, head.NodeColumn);
        Assert.Contains(head.Edges, e => e is { Kind: GraphEdgeKind.In } && e.From == wt.NodeColumn);
        AssertContinuous(rows);
    }

    [Fact]
    public void Paged_layout_matches_laying_out_everything_at_once()
    {
        List<HistoryRow> whole = RandomHistory(seed: 7, count: 1000);
        List<HistoryRow> paged = [.. whole.Select(CopyRow)];

        CommitGraphBuilder.Layout(whole);
        var firstPage = new CommitGraphBuilder();
        firstPage.Append(paged[..500]);
        CommitGraphBuilder secondPage = firstPage.Clone();
        secondPage.Append(paged[500..]);

        for (var i = 0; i < whole.Count; i++)
        {
            AssertSameLayout(whole[i].Graph, paged[i].Graph);
            Assert.Equal(whole[i].Tint, paged[i].Tint);
        }

        // Cloning left the first page's state untouched: appending again gives the same result.
        List<HistoryRow> again = [.. whole.Skip(500).Select(CopyRow)];
        firstPage.Append(again);
        for (var i = 0; i < again.Count; i++)
        {
            AssertSameLayout(whole[500 + i].Graph, again[i].Graph);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Random_history_edges_join_across_every_row(int seed)
    {
        List<HistoryRow> rows = RandomHistory(seed, count: 2000);

        CommitGraphBuilder.Layout(rows);

        Assert.Empty(Top(rows[0].Graph));
        AssertContinuous(rows);
        foreach (HistoryRow row in rows)
        {
            GraphRow g = row.Graph;
            Assert.All(g.Edges.Where(e => e.Kind == GraphEdgeKind.In), e => Assert.Equal(g.NodeColumn, e.To));
            Assert.All(g.Edges.Where(e => e.Kind is GraphEdgeKind.Out or GraphEdgeKind.Stub), e => Assert.Equal(g.NodeColumn, e.From));
            Assert.Equal(row.ParentIds.Distinct().Count(), g.Edges.Count(e => e.Kind is GraphEdgeKind.Out));
            Assert.Equal(g.NodeColor, row.Tint);
            Assert.True(g.Edges.All(e => e.From < g.ColumnCount && e.To < g.ColumnCount));
            // Each parent is reached through its own lane.
            GraphEdge[] outs = [.. g.Edges.Where(e => e.Kind == GraphEdgeKind.Out)];
            Assert.Equal(outs.Length, outs.Select(e => e.To).Distinct().Count());
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Ten_thousand_rows_lay_out_quickly()
    {
        List<HistoryRow> rows = RandomHistory(seed: 11, count: 10_000);

        CommitGraphBuilder.Layout(rows[..500]); // warm up
        var stopwatch = Stopwatch.StartNew();
        var builder = new CommitGraphBuilder();
        for (var start = 0; start < rows.Count; start += 500)
        {
            builder = builder.Clone();
            builder.Append(rows[start..Math.Min(rows.Count, start + 500)]);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"10k rows took {stopwatch.ElapsedMilliseconds} ms");
    }

    [AvaloniaFact]
    public void Graph_rows_render_continuous_lanes_headlessly()
    {
        List<HistoryRow> rows = Rows(("m", ["a", "b"]), ("a", ["c"]), ("b", ["c"]), ("c", []));
        rows.Insert(0, new HistoryRow { ParentIds = ["m"] });
        CommitGraphBuilder.Layout(rows);

        const int rowHeight = 26;
        var panel = new StackPanel { Background = Brushes.Transparent };
        foreach (HistoryRow row in rows)
        {
            panel.Children.Add(new CommitGraphRow
            {
                Entry = row.Graph,
                Width = 60,
                Height = rowHeight,
                ColumnWidth = 11,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left
            });
        }

        // Rows without data and clipped lanes beyond the width must not throw either.
        panel.Children.Add(new CommitGraphRow { Width = 60, Height = rowHeight });
        panel.Children.Add(new CommitGraphRow
        {
            Entry = new GraphRow(40, GraphColor.Grey, GraphNodeKind.Merge,
                [new GraphEdge(GraphEdgeKind.Through, 30, 30, GraphColor.Grey), new GraphEdge(GraphEdgeKind.Out, 40, 2, GraphColor.Grey)], 41),
            Width = 60,
            Height = rowHeight
        });

        var window = new Window { Width = 200, Height = rowHeight * (rows.Count + 2), Content = panel, Background = Brushes.White };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        // The lane in column 0 is drawn across every boundary from the work-tree node down to c.
        var x = (int)CommitGraphRow.LeftMargin;
        for (var boundary = 1; boundary < rows.Count; boundary++)
        {
            Assert.NotEqual(Colors.White, PixelAt(frame!, x, boundary * rowHeight));
        }

        // Column 1 (b's lane) is drawn at the boundary between a and b, but not left of the graph.
        Assert.NotEqual(Colors.White, PixelAt(frame!, x + 11, 3 * rowHeight));
        Assert.Equal(Colors.White, PixelAt(frame!, 1, 2 * rowHeight));
        window.Close();
    }

    /// <summary>The edges leaving the bottom of <paramref name="upper"/> enter the top of <paramref name="lower"/>.</summary>
    internal static void AssertRowsJoin(GraphRow upper, GraphRow lower)
        => Assert.Equal(Bottom(upper), Top(lower));

    internal static void AssertSameLayout(GraphRow expected, GraphRow actual)
    {
        Assert.Equal(expected.NodeColumn, actual.NodeColumn);
        Assert.Equal(expected.NodeColor, actual.NodeColor);
        Assert.Equal(expected.NodeKind, actual.NodeKind);
        Assert.Equal(expected.ColumnCount, actual.ColumnCount);
        Assert.Equal(expected.Edges, actual.Edges);
    }

    internal static HistoryRow CopyRow(HistoryRow row)
        => new() { Id = row.Id, CommitId = row.CommitId, ParentIds = row.ParentIds, Title = row.Title };

    private static void AssertContinuous(IReadOnlyList<HistoryRow> rows)
    {
        for (var i = 1; i < rows.Count; i++)
        {
            List<(int Column, GraphColor Color)> bottom = Bottom(rows[i - 1].Graph), top = Top(rows[i].Graph);
            Assert.True(bottom.SequenceEqual(top),
                $"row {i - 1} leaves at [{string.Join(", ", bottom)}] but row {i} enters at [{string.Join(", ", top)}]");
        }
    }

    private static List<(int Column, GraphColor Color)> Top(GraphRow row)
        => [.. row.Edges.Where(e => e.EntersTop).Select(e => (e.From, e.Color)).Distinct().OrderBy(e => e.From)];

    private static List<(int Column, GraphColor Color)> Bottom(GraphRow row)
        => [.. row.Edges.Where(e => e.LeavesBottom).Select(e => (e.To, e.Color)).Distinct().OrderBy(e => e.To)];

    private static List<HistoryRow> Rows(params (string Id, string[] Parents)[] commits)
        => [.. commits.Select(c => new HistoryRow { Id = c.Id, CommitId = c.Id, ParentIds = c.Parents, Title = c.Id })];

    /// <summary>
    /// A branchy history, newest first: branches fork from recent commits, merge
    /// (sometimes octopus), end, and occasionally point at parents outside the list.
    /// </summary>
    private static List<HistoryRow> RandomHistory(int seed, int count)
    {
        var random = new Random(seed);
        var commits = new List<(string Id, string[] Parents)>(count);
        var heads = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var id = $"c{i}";
            string[] parents;
            if (heads.Count == 0)
            {
                parents = random.Next(4) == 0 ? [$"outside{i}"] : [];
                heads.Add(id);
            }
            else
            {
                var roll = random.Next(100);
                var h = random.Next(heads.Count);
                if (roll < 10 && commits.Count > 0)
                {
                    // New branch from a recent commit.
                    parents = [commits[Math.Max(0, commits.Count - 1 - random.Next(30))].Id];
                    heads.Add(id);
                }
                else if (roll < 22 && heads.Count > 1)
                {
                    var others = heads.Where((_, k) => k != h).OrderBy(_ => random.Next()).Take(roll < 14 ? 2 : 1).ToList();
                    parents = [heads[h], .. others];
                    heads.RemoveAll(others.Contains);
                    heads[heads.IndexOf(parents[0])] = id;
                }
                else if (roll == 22 && random.Next(4) == 0)
                {
                    parents = [heads[h], $"outside{i}"];
                    heads[h] = id;
                }
                else
                {
                    parents = [heads[h]];
                    heads[h] = id;
                }

                if (heads.Count > 12)
                {
                    heads.RemoveAt(random.Next(heads.Count));
                }
            }

            commits.Add((id, parents));
        }

        commits.Reverse();
        return Rows([.. commits]);
    }

    private static Color PixelAt(WriteableBitmap bitmap, int x, int y)
    {
        using ILockedFramebuffer buffer = bitmap.Lock();
        var bytes = new byte[4];
        Marshal.Copy(buffer.Address + y * buffer.RowBytes + x * 4, bytes, 0, 4);
        return buffer.Format == PixelFormat.Bgra8888
            ? Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0])
            : Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2]);
    }
}
