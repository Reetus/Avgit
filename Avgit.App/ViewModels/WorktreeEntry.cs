using Avgit.Git;

namespace Avgit.App.ViewModels;

/// <summary>One worktree in the sidebar's WORKTREES list.</summary>
public sealed class WorktreeEntry(WorktreeInfo info, bool isCurrent, bool isMain)
{
    public WorktreeInfo Info { get; } = info;
    public string Path => Info.Path;
    public bool IsCurrent { get; } = isCurrent;
    public bool IsMain { get; } = isMain;
    public bool IsLocked => Info.IsLocked;
    public bool IsPrunable => Info.IsPrunable;

    public string Name
    {
        get
        {
            var name = System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }

    public string BranchText
    {
        get
        {
            if (Info.IsBare)
            {
                return "bare";
            }

            if (Info.IsDetached || Info.BranchName.Length == 0)
            {
                return Info.Head.Length >= 7 ? $"detached at {Info.Head[..7]}" : "detached";
            }

            return Info.BranchName;
        }
    }

    /// <summary>Short labels after the branch: main, locked, missing.</summary>
    public string Badges => string.Join(" · ", new[]
    {
        IsMain ? "main" : null,
        IsLocked ? "locked" : null,
        IsPrunable ? "missing" : null
    }.Where(b => b is not null));

    public bool HasBadges => IsMain || IsLocked || IsPrunable;

    public string ToolTip
    {
        get
        {
            var lines = new List<string> { Path };
            if (IsCurrent)
            {
                lines.Add("This tab's worktree.");
            }

            if (IsMain)
            {
                lines.Add("Main worktree.");
            }

            if (IsLocked)
            {
                lines.Add("Locked: git will not remove or prune it.");
            }

            if (IsPrunable)
            {
                lines.Add("The folder is missing. Prune to forget it.");
            }

            return string.Join("\n", lines);
        }
    }

    public bool CanOpen => !IsCurrent && !Info.IsBare && !IsPrunable;
    public bool CanRemove => !IsCurrent && !IsMain;
}
