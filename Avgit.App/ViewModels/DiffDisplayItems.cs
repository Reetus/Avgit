using System.Windows.Input;
using Avalonia.Media;
using Avgit.Core.Model;

namespace Avgit.App.ViewModels;

/// <summary>One commit in the history list.</summary>
public sealed partial class CommitRowViewModel(HistoryRow row) : ObservableObject
{

    // Matches the CommitGraphRow in HistoryView: 8px left margin, 11px per lane.
    private const double GraphMargin = 8;
    private const double GraphLaneWidth = 11;
    private const double GraphMaxWidth = 160;

    public HistoryRow Row { get; } = row;

    /// <summary>Graph column width shared by every loaded row; widens when a later page needs more lanes.</summary>
    [ObservableProperty]
    public partial double GraphWidth { get; set; } = GraphMaxWidth;

    /// <summary>
    /// Sizes the graph column to the widest lane in <paramref name="rows"/> so a narrow graph
    /// leaves room for the message and ref pills. Wider graphs are clipped at the old fixed width.
    /// </summary>
    public static double GraphWidthFor(IEnumerable<HistoryRow> rows)
    {
        var maxColumn = 0;
        foreach (HistoryRow row in rows)
        {
            maxColumn = Math.Max(maxColumn, row.Graph.ColumnCount - 1);
        }

        return Math.Min(GraphMaxWidth, GraphMargin * 2 + maxColumn * GraphLaneWidth);
    }

    /// <summary>This row's slice of the commit graph, drawn by <c>CommitGraphRow</c>.</summary>
    public GraphRow Graph => Row.Graph;
    public string Message => Row.Title;
    public string Author => Row.AuthorDisplay;
    public string TimestampText => Row.Timestamp.ToString("yyyy-MM-dd HH:mm");
    public string Sha => Row.ShortId;
    public string ParentsText => Row.ParentIds.Length > 1 ? $"merge {Row.ParentIds.Length}" : string.Empty;
    public bool IsMerge => Row.ParentIds.Length > 1;
    public bool HasLabels => Row.Refs.Length > 0;
    public IReadOnlyList<RefPill> Pills => _pills ??= RefPill.FromLabels(Row.Refs, _worktreeBranches);
    public string LabelsToolTip => string.Join("\n", Row.Refs.Select(l => l.Kind switch
    {
        RefKind.Tag => $"tag: {l.Name}",
        RefKind.RemoteBranch => l.Name,
        RefKind.Branch when _worktreeBranches.Contains(l.Name) => $"{l.Name} (in another worktree)",
        _ => l.Name
    }));

    private IReadOnlyList<RefPill>? _pills;
    private IReadOnlySet<string> _worktreeBranches = EmptyNames;
    private static readonly IReadOnlySet<string> EmptyNames = new HashSet<string>();

    /// <summary>
    /// Sets the local branches checked out in other worktrees; their pills get a worktree icon.
    /// Rows whose labels are unaffected are not redrawn.
    /// </summary>
    public void SetWorktreeBranches(IReadOnlySet<string> branches)
    {
        if (ReferenceEquals(branches, _worktreeBranches))
        {
            return;
        }

        var changed = Row.Refs.Any(l => l.Kind == RefKind.Branch
                                        && _worktreeBranches.Contains(l.Name) != branches.Contains(l.Name));
        _worktreeBranches = branches;
        if (changed)
        {
            _pills = null;
            OnPropertyChanged(nameof(Pills));
            OnPropertyChanged(nameof(LabelsToolTip));
        }
    }
    public string LabelColor => $"#{Row.Tint.R:X2}{Row.Tint.G:X2}{Row.Tint.B:X2}";
}

public enum RefPillKind
{
    CurrentBranch,
    Branch,
    RemoteBranch,
    Tag,
    Other
}

/// <summary>
/// A ref label drawn as a pill in the history list. <paramref name="InWorktree"/> marks a
/// local branch checked out in another worktree.
/// </summary>
public sealed record RefPill(string Text, RefPillKind Kind, string? Remotes = null, bool InWorktree = false)
{
    /// <summary>
    /// Orders labels current branch, local branches, remote-only branches, tags, then
    /// anything else. A remote branch whose local branch of the same name is on the same
    /// commit folds into that pill (shown as its remote names), and remote/HEAD is dropped
    /// because it always sits beside the remote's default branch.
    /// </summary>
    public static IReadOnlyList<RefPill> FromLabels(IReadOnlyList<RefBadge> labels, IReadOnlySet<string>? worktreeBranches = null)
    {
        var locals = labels.Where(l => l.Kind == RefKind.Branch).Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        var folded = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var remoteOnly = new List<string>();
        foreach (RefBadge? label in labels.Where(l => l.Kind == RefKind.RemoteBranch))
        {
            var slash = label.Name.IndexOf('/');
            var remote = slash > 0 ? label.Name[..slash] : string.Empty;
            var branch = slash > 0 ? label.Name[(slash + 1)..] : label.Name;
            if (branch == "HEAD")
            {
                continue;
            }

            if (locals.Contains(branch))
            {
                if (!folded.TryGetValue(branch, out List<string>? remotes))
                {
                    folded[branch] = remotes = [];
                }

                remotes.Add(remote);
            }
            else
            {
                remoteOnly.Add(label.Name);
            }
        }

        var pills = new List<RefPill>(labels.Count);
        foreach (RefBadge? label in labels.Where(l => l.Kind == RefKind.Branch).OrderByDescending(l => l.IsCheckedOut))
        {
            var remotes = folded.TryGetValue(label.Name, out List<string>? names) ? string.Join(", ", names) : null;
            pills.Add(new RefPill(label.Name, label.IsCheckedOut ? RefPillKind.CurrentBranch : RefPillKind.Branch, remotes,
                worktreeBranches?.Contains(label.Name) == true));
        }
        pills.AddRange(remoteOnly.Select(name => new RefPill(name, RefPillKind.RemoteBranch)));
        pills.AddRange(labels.Where(l => l.Kind == RefKind.Tag).Select(l => new RefPill(l.Name, RefPillKind.Tag)));
        pills.AddRange(labels.Where(l => l.Kind == RefKind.Other).Select(l => new RefPill(l.Name, RefPillKind.Other)));
        return pills;
    }
}

public sealed class RemoteEntry
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Display => string.IsNullOrEmpty(Name) ? Url : Name;
}

public abstract class DiffDisplayItem
{
}

public sealed class DiffFileHeaderItem(FileDiff file, bool canStage, bool canUnstage, bool canDiscard) : DiffDisplayItem
{
    public FileDiff File { get; } = file;
    public string Title => File.FileName;
    public string Description => File.Caption;
    public bool CanStage { get; } = canStage;
    public bool CanUnstage { get; } = canUnstage;
    public bool CanDiscard { get; } = canDiscard;
    public ICommand? StageCommand { get; init; }
    public ICommand? UnstageCommand { get; init; }
    public ICommand? DiscardCommand { get; init; }
}

public sealed class DiffHunkHeaderItem : DiffDisplayItem
{
    public DiffHunkHeaderItem(IHunk hunk, FileDiff file, bool canStage, bool canUnstage, bool canDiscard)
    {
        Hunk = hunk;
        File = file;
        CanStage = canStage && !hunk.Truncated;
        CanUnstage = canUnstage && !hunk.Truncated;
        CanDiscard = canDiscard && !hunk.Truncated;
        // Line-level discard is not offered for new files (see ApplyNewFileSelectionAsync).
        CanDiscardLines = CanDiscard && file.State is not (PathState.New or PathState.Untracked);
    }

    public IHunk Hunk { get; }
    public FileDiff File { get; }
    public string Description => Hunk.Caption;
    public bool CanStage { get; }
    public bool CanUnstage { get; }
    public bool CanDiscard { get; }
    public bool CanDiscardLines { get; }
    public ICommand? StageCommand { get; init; }
    public ICommand? UnstageCommand { get; init; }
    public ICommand? DiscardCommand { get; init; }
    public ICommand? StageSelectedLinesCommand { get; init; }
    public ICommand? UnstageSelectedLinesCommand { get; init; }
    public ICommand? DiscardSelectedLinesCommand { get; init; }
}

public sealed class DiffLineItem(HunkLine line, IHunk hunk, FileDiff file, bool showCheckBox,
    DiffGutter? gutter = null) : DiffDisplayItem
{
    private static readonly IBrush AddedBackground = new SolidColorBrush(Color.Parse("#17351F"));
    private static readonly IBrush RemovedBackground = new SolidColorBrush(Color.Parse("#3A1C1C"));
    private static readonly IBrush AddedPrefix = new SolidColorBrush(Color.Parse("#7EE787"));
    private static readonly IBrush RemovedPrefix = new SolidColorBrush(Color.Parse("#FF7B72"));
    private static readonly IBrush ContextPrefix = new SolidColorBrush(Color.Parse("#858585"));
    private static readonly IBrush DefaultText = new SolidColorBrush(Color.Parse("#D4D4D4"));
    private static readonly IBrush LineNumber = new SolidColorBrush(Color.Parse("#858585"));

    public DiffGutter Gutter { get; } = gutter ?? DiffGutter.Default;
    public bool ShowBeforeLineNo => Gutter.ShowBefore;
    public bool ShowAfterLineNo => Gutter.ShowAfter;
    public double LineNoWidth => Gutter.NumberWidth;

    public HunkLine Line { get; } = line;
    public IHunk Hunk { get; } = hunk;
    public FileDiff File { get; } = file;
    public string Text => Line.Text;
    public string PrefixText => Line.Marker == ' ' ? " " : Line.Marker.ToString();
    public string OldLineNo => Line.OldLineNo;
    public string NewLineNo => Line.NewLineNo;
    public bool ShowCheckBox { get; } = showCheckBox && !hunk.Truncated && line.Marker is '+' or '-';
    public IBrush Background => Line.Marker switch
    {
        '+' => AddedBackground,
        '-' => RemovedBackground,
        _ => Brushes.Transparent
    };
    public IBrush PrefixBrush => Line.Marker switch
    {
        '+' => AddedPrefix,
        '-' => RemovedPrefix,
        _ => ContextPrefix
    };
    public IBrush TextBrush => DefaultText;
    public IBrush LineNumberBrush => LineNumber;
    public string RawText => Line.RawText;
}

/// <summary>
/// Line-number gutter layout shared by every line of one file. A side with no
/// numbers (the old side of a new file, the new side of a deleted file, either
/// side of a plain content view) is hidden instead of leaving an empty column,
/// and the width fits the largest number.
/// </summary>
public sealed record DiffGutter(bool ShowBefore, bool ShowAfter, double NumberWidth)
{
    public static DiffGutter Default { get; } = new(true, true, 34);

    public static DiffGutter For(FileDiff file)
    {
        var maxDigits = 1;
        var anyBefore = false;
        var anyAfter = false;
        foreach (IHunk hunk in file.Hunks)
        {
            foreach (HunkLine line in hunk.LineItems)
            {
                if (int.TryParse(line.OldLineNo, out var before))
                {
                    anyBefore = true;
                    maxDigits = Math.Max(maxDigits, before.ToString().Length);
                }
                if (int.TryParse(line.NewLineNo, out var after))
                {
                    anyAfter = true;
                    maxDigits = Math.Max(maxDigits, after.ToString().Length);
                }
            }
        }

        // Unchanged content shows identical numbers on both sides; one is enough.
        if (file.State == PathState.Unchanged)
        {
            anyBefore = false;
        }

        // ~7px per digit at the 11px monospace size; the cell adds its own margin.
        var width = Math.Max(2, maxDigits) * 7 + 4;
        return new DiffGutter(anyBefore, anyAfter || !anyBefore, width);
    }
}

public sealed class DiffNoteItem(string text) : DiffDisplayItem
{
    public string Text { get; } = text;
}
