namespace Avgit.Core.Model;

/// <summary>A branch, tag or other ref shown next to a commit.</summary>
public partial class RefBadge : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public RefKind Kind { get; set; }
    public string? Hint { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }

    public string Target { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCheckedOut { get; set; }

    public RefBadge() { }

    public RefBadge(string name, RefKind kind, string target, string targetId)
    {
        Name = name;
        Kind = kind;
        Target = target;
        TargetId = targetId;
    }

    public override string ToString() => Name;
}

/// <summary>One commit as listed in the history view.</summary>
public partial class HistoryRow : ObservableObject
{
    [ObservableProperty]
    public partial string Id { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CommitId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ShortId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TouchedFiles { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string[] ParentIds { get; set; } = [];

    [ObservableProperty]
    public partial DateTime Timestamp { get; set; }

    [ObservableProperty]
    public partial string BranchName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Author { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Committer { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RowEnabled { get; set; } = true;

    public bool IsCommit => !string.IsNullOrEmpty(Id);

    public RefBadge[] Refs
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasRefs));
            }
        }
    } = [];

    public bool IsCheckedOut => Refs.Any(r => r.IsCheckedOut);
    public bool HasRefs => Refs.Length != 0;

    /// <summary>This row's slice of the commit graph; set by <c>CommitGraphBuilder</c>.</summary>
    public GraphRow Graph { get; set; } = GraphRow.Empty;

    /// <summary>Colour of the row's graph node, used for its ref labels.</summary>
    public GraphColor Tint { get; set; } = GraphColor.Default;

    public string AuthorDisplay
    {
        get
        {
            var bracket = Author.IndexOf('<');
            return bracket > 0 ? Author[..bracket].Trim() : Author;
        }
    }

    public string TimestampText => Timestamp.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>A commit with its full metadata, as returned by a commit query.</summary>
public class CommitRecord
{
    public string? Author { get; set; }
    public DateTime AuthorDate { get; set; }
    public string? Branch { get; set; }
    public DateTime CommitDate { get; set; }
    public string? Committer { get; set; }
    public string[]? Refs { get; set; }
    public string[]? Tags { get; set; }
    public string? RefsText { get; set; }
    public string? Message { get; set; }
    public string? Title { get; set; }
    public string[]? ParentIds { get; set; }
    public string? Id { get; set; }
    public string? Sha { get; set; }
    public string? ShortSha { get; set; }
    public string? SvnRevision { get; set; }
}

/// <summary>A local or remote branch.</summary>
public class BranchRef
{
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public bool IsCheckedOut { get; set; }
    public RefKind Kind { get; set; }
    public UpstreamTrack? Upstream { get; set; }

    /// <summary>The worktree that has this branch checked out (this one or another), if any.</summary>
    public string? WorktreePath { get; set; }

    /// <summary>Checked out in a different worktree, so it cannot be checked out or deleted here.</summary>
    public bool IsInOtherWorktree => WorktreePath is not null && !IsCheckedOut;

    public string WorktreeToolTip => $"Checked out in worktree {WorktreePath}";

    public bool CanDelete => !IsCheckedOut && WorktreePath is null;

    /// <summary>The branch tracks a remote branch (which may since have been deleted).</summary>
    public bool HasUpstream => Kind == RefKind.Branch && Upstream is not null;

    /// <summary>A local branch without an upstream, which can be given one.</summary>
    public bool CanSetUpstream => Kind == RefKind.Branch && Upstream is null;

    /// <summary>A branch can be given its own worktree only while no worktree has it checked out.</summary>
    public bool CanAddWorktree => Kind == RefKind.Branch && !IsCheckedOut && WorktreePath is null;

    /// <summary>Committer date of the tip commit, or null when git did not report one.</summary>
    public DateTimeOffset? LastCommitDate { get; set; }

    public string LastCommitAuthor { get; set; } = string.Empty;
    public string LastCommitSubject { get; set; } = string.Empty;

    /// <summary>The upstream is configured but its remote-tracking branch no longer exists (deleted on the remote, then pruned).</summary>
    public bool UpstreamGone => Upstream is { Gone: true };

    public string UpstreamGoneToolTip => Upstream is { Gone: true } tracking
        ? string.Format(CoreStrings.Branch_UpstreamGoneToolTip, tracking.RemoteBranchForPull)
        : string.Empty;

    public string UpstreamText
    {
        get
        {
            if (Upstream is not { } tracking)
            {
                return string.Empty;
            }

            if (tracking.Ahead == 0 && tracking.Behind == 0)
            {
                return string.Empty;
            }

            return $"\u2191{tracking.Ahead} \u2193{tracking.Behind}";
        }
    }
}

public struct UpstreamTrack
{
    public string? Remote { get; set; }
    public string? Branch { get; set; }
    public string? RemoteBranchForPull { get; set; }
    public uint Ahead { get; set; }
    public uint Behind { get; set; }

    /// <summary>git reports the upstream as <c>[gone]</c>: its remote-tracking branch was removed.</summary>
    public bool Gone { get; set; }
}

public struct StashRecord
{
    /// <summary>The stash's reflog name (<c>stash@{n}</c>); it shifts as stashes are added or dropped.</summary>
    public string Id;
    public string Created;
    public string Text;

    /// <summary>The stash commit; unlike <see cref="Id"/> it stays the same while the stash exists.</summary>
    public string Sha;

    /// <summary>The abbreviated commit HEAD pointed at when the stash was made (its first parent).</summary>
    public string BaseShortSha;
}

/// <summary>Filter for a page of history.</summary>
public class HistoryFilter
{
    public uint Offset { get; init; }
    public uint Count { get; init; } = 300;
    public string? StartRef { get; init; }
    public string? Author { get; init; }
    public string? Path { get; init; }
    public bool TrackRenames { get; init; }
    public HistoryFlags Flags { get; init; } = HistoryFlags.Merges | HistoryFlags.Remotes;
    public bool FirstParent { get; init; }
}

/// <summary>What diff to produce for a path or commit range.</summary>
public class DiffSpec
{
    public DiffRequestKind Kind { get; init; }
    public string? Revision { get; init; }
    public string? BaseRevision { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    public int Context { get; init; } = 3;
    public bool IgnoreWhitespace { get; init; }
    public string? FilePath { get; init; }

    /// <summary>
    /// For <see cref="DiffRequestKind.Commit"/>: the commit's first parent when
    /// already known ("" for a root commit), saving a rev-list lookup. Null means
    /// look it up.
    /// </summary>
    public string? FirstParent { get; init; }
}

/// <summary>One line of a blame annotation.</summary>
public class BlameEntry
{
    public string CommitId { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime When { get; set; }
    public string Text { get; set; } = string.Empty;
    public int Number { get; set; }
}
