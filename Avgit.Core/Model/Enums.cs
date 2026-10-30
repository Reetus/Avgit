namespace Avgit.Core.Model;

/// <summary>
/// Bit flags describing how one path differs between two places (index,
/// working tree, or two commits). Each status letter git reports maps to
/// exactly one member; the masks select which states a query returns.
/// </summary>
[Flags]
public enum PathState
{
    None = 0,

    /// <summary>No difference on this side (git's <c>.</c>).</summary>
    Unchanged = 1 << 0,

    /// <summary>Content or file type changed (<c>M</c>, <c>T</c>).</summary>
    Edited = 1 << 1,

    /// <summary>Added (<c>A</c>), including intent-to-add paths.</summary>
    New = 1 << 2,

    /// <summary>Deleted (<c>D</c>).</summary>
    Removed = 1 << 3,

    /// <summary>Unmerged: one of git's conflict states (<c>u</c>, <c>UU</c>, <c>AA</c>, ...).</summary>
    Conflicted = 1 << 4,

    /// <summary>Not tracked by git (<c>?</c>).</summary>
    Untracked = 1 << 5,

    /// <summary>Ignored by a gitignore rule (<c>!</c>).</summary>
    Excluded = 1 << 6,

    /// <summary>Renamed (<c>R</c>); the record carries the original path.</summary>
    Moved = 1 << 7,

    /// <summary>Copied (<c>C</c>); the record carries the source path.</summary>
    Duplicated = 1 << 8,

    /// <summary>A submodule whose checked-out commit differs from the recorded one.</summary>
    SubmoduleCommitChanged = 1 << 9,

    /// <summary>Every tracked change: what a commit can contain.</summary>
    ChangedMask = Edited | New | Removed | Conflicted | Moved | Duplicated | SubmoduleCommitChanged,

    /// <summary>What a normal working-copy listing shows: changes plus untracked paths.</summary>
    WorkingCopyMask = ChangedMask | Untracked,

    /// <summary><see cref="WorkingCopyMask"/> plus ignored paths (asks git for <c>--ignored</c>).</summary>
    WorkingCopyWithIgnoredMask = WorkingCopyMask | Excluded
}

/// <summary>Operations git can leave half-finished in a repository, detected from the git dir.</summary>
[Flags]
public enum RepoOperations
{
    None = 0,

    /// <summary><c>MERGE_HEAD</c> exists.</summary>
    Merge = 1 << 0,

    /// <summary><c>rebase-merge/</c>, or <c>rebase-apply/</c> without an <c>applying</c> marker.</summary>
    Rebase = 1 << 1,

    /// <summary><c>git am</c>: <c>rebase-apply/applying</c> exists.</summary>
    ApplyMailbox = 1 << 2,

    /// <summary><c>CHERRY_PICK_HEAD</c> exists.</summary>
    CherryPick = 1 << 3,

    /// <summary><c>REVERT_HEAD</c> exists.</summary>
    Revert = 1 << 4,

    /// <summary><c>BISECT_LOG</c> exists.</summary>
    Bisect = 1 << 5
}

/// <summary>Which history entries to fetch and how to order them.</summary>
[Flags]
public enum HistoryFlags
{
    None = 0,

    /// <summary><c>--topo-order</c> instead of <c>--date-order</c>.</summary>
    Topological = 1 << 0,

    /// <summary>Include merge commits (otherwise follow first parents only).</summary>
    Merges = 1 << 1,

    /// <summary>Include remote-tracking branches.</summary>
    Remotes = 1 << 2
}

/// <summary>What a diff compares and how it treats whitespace.</summary>
[Flags]
public enum DiffScope
{
    WorkTreeVsHead = 0,
    WorkTreeVsIndex = 1,
    IndexVsHead = 2,
    TargetMask = 3,
    SkipWhitespace = 4,
    LoosePatch = 8
}

/// <summary>What a diff request is about.</summary>
public enum DiffSubject
{
    PendingFiles,
    SingleCommit,
    MergeCommit,
    Range,
    StashSnapshot
}

/// <summary>What a ref label points at.</summary>
public enum RefKind
{
    // Explicit values keep the on-disk history cache readable across releases;
    // HistoryCache.FormatVersion must change whenever these do.
    Branch = 0,
    Tag = 1,
    RemoteBranch = 2,
    Other = 3
}

public enum DiffRequestKind
{
    WorkingCopy,
    Staged,
    Commit,
    CommitRange,
    FileContent,

    /// <summary>A stash (<see cref="DiffSpec.Revision"/>) against the commit it was made on, untracked files included.</summary>
    Stash
}
