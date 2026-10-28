namespace Avgit.Core.Model;

/// <summary>Which list a <see cref="ChangedFile"/> belongs to.</summary>
public enum ChangeArea
{
    /// <summary>Work tree compared with the index (git's Y column).</summary>
    WorkTree,

    /// <summary>Index compared with HEAD (git's X column): the staged list.</summary>
    Index,

    /// <summary>A path changed by a commit or between two commits.</summary>
    Commit
}

/// <summary>
/// One path shown in the working-copy lists (or in a commit's file list),
/// with the git-reported state on each side plus UI selection state.
/// </summary>
public sealed partial class ChangedFile : ObservableObject
{
    /// <summary>The list this record belongs to; part of its identity.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStaged))]
    public partial ChangeArea Area { get; set; } = ChangeArea.WorkTree;

    [ObservableProperty]
    public partial string FileName { get; set; } = string.Empty;

    /// <summary>Repository-relative path with forward slashes, exactly as git reports it.</summary>
    [ObservableProperty]
    public partial string RelativePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FullPath { get; set; } = string.Empty;

    /// <summary>The original path of a rename or the source of a copy.</summary>
    public string? OldPath
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasOldPath));
            }
        }
    }

    /// <summary>The state shown for this row: the index side when staged, otherwise the work-tree side.</summary>
    public PathState Status
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>Index vs HEAD (git's X column).</summary>
    [ObservableProperty]
    public partial PathState IndexStatus { get; set; }

    /// <summary>Work tree vs index (git's Y column).</summary>
    [ObservableProperty]
    public partial PathState WorkTreeStatus { get; set; }

    /// <summary>Whether the row's checkbox is ticked.</summary>
    [ObservableProperty]
    public partial bool Included { get; set; }

    public bool IsStaged => Area == ChangeArea.Index;
    public bool HasOldPath => !string.IsNullOrEmpty(OldPath);

    public string StatusText => Status switch
    {
        PathState.New => "A",
        PathState.Removed => "D",
        PathState.Edited => "M",
        PathState.Moved => "R",
        PathState.Duplicated => "C",
        PathState.Conflicted => "!",
        PathState.Untracked => "?",
        PathState.Excluded => "I",
        PathState.SubmoduleCommitChanged => "S",
        PathState.Unchanged => " ",
        _ => "M"
    };

    /// <summary>
    /// Copies the git-reported state of <paramref name="source"/> (same path and
    /// list) onto this record, keeping UI state such as <see cref="Included"/>.
    /// Refreshing in place keeps list selection and scroll position stable.
    /// </summary>
    public void CopyStatusFrom(ChangedFile source)
    {
        FileName = source.FileName;
        FullPath = source.FullPath;
        OldPath = source.OldPath;
        Status = source.Status;
        IndexStatus = source.IndexStatus;
        WorkTreeStatus = source.WorkTreeStatus;
    }

    public override bool Equals(object? obj)
        => obj is ChangedFile that && Area == that.Area && string.Equals(RelativePath, that.RelativePath, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Area, RelativePath);

    public override string ToString() => RelativePath;
}

/// <summary>
/// Repository-level facts from one status run: the branch, its upstream, any
/// half-finished operation, and counts for the UI badges.
/// </summary>
public sealed record RepoSummary
{
    /// <summary>What the UI shows in place of a branch name when HEAD is detached.</summary>
    public const string DetachedHeadLabel = "HEAD (detached)";

    /// <summary>The checked-out branch, or null when HEAD is detached.</summary>
    public string? Branch { get; init; }

    /// <summary>The commit HEAD points at, or null in a repository with no commits yet.</summary>
    public string? HeadId { get; init; }

    /// <summary>The branch's upstream (e.g. <c>origin/main</c>), or null when none is configured.</summary>
    public string? Upstream { get; init; }

    /// <summary>Commits on the branch that are not on its upstream (0 without an upstream).</summary>
    public int Ahead { get; init; }

    /// <summary>Commits on the upstream that are not on the branch (0 without an upstream).</summary>
    public int Behind { get; init; }

    public RepoOperations Operations { get; init; }

    public bool IsClean { get; init; }

    // Each path counts once, by the state of its staged entry when it has one.
    public int Modified { get; init; }
    public int Added { get; init; }
    public int Removed { get; init; }
    public int Renamed { get; init; }
    public int Copied { get; init; }
    public int Conflicted { get; init; }
    public int Untracked { get; init; }

    public bool IsDetached => Branch is null;
    public bool IsUnborn => HeadId is null;

    /// <summary>The branch name, or <see cref="DetachedHeadLabel"/>.</summary>
    public string BranchLabel => Branch ?? DetachedHeadLabel;

    public bool MergeInProgress => (Operations & RepoOperations.Merge) != 0;
    public bool RebaseInProgress => (Operations & RepoOperations.Rebase) != 0;
    public bool ApplyMailboxInProgress => (Operations & RepoOperations.ApplyMailbox) != 0;
    public bool CherryPickInProgress => (Operations & RepoOperations.CherryPick) != 0;
    public bool RevertInProgress => (Operations & RepoOperations.Revert) != 0;
    public bool BisectInProgress => (Operations & RepoOperations.Bisect) != 0;
}
