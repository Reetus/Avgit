using Avgit.Core.Model;

namespace Avgit.Git;

/// <summary>
/// Runs git for a repository. Every member takes the repository's work-tree
/// root explicitly; <see cref="GitManager.Open"/> pairs a root with an engine.
/// Members that return <see cref="ProcessResult"/> report failures there; the
/// convenience readers record theirs in <see cref="LastError"/>; the batched
/// snapshot readers return theirs in the result so they can run concurrently.
/// </summary>
public interface IGitEngine
{
    string ExecutablePath { get; }
    string? LastError { get; }
    void ClearLastError();

    Task<ProcessResult> RunAsync(string path, IReadOnlyList<string> arguments, string? standardInput = null, CancellationToken cancellationToken = default);

    Task<string> GetCurrentRevisionAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>The checked-out branch, or <see cref="RepoSummary.DetachedHeadLabel"/>.</summary>
    Task<string> GetCurrentBranchAsync(string path, CancellationToken cancellationToken = default);

    /// <summary><see cref="GetStatusSnapshotAsync"/>, recording a failure in <see cref="LastError"/>.</summary>
    Task<StatusSnapshot> GetStatusAsync(string path, PathState mask = PathState.WorkingCopyMask, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoryRow>> GetLogAsync(string path, HistoryFilter query, CancellationToken cancellationToken = default);
    Task<CommitRecord?> GetCommitDetailsAsync(string path, string revision, CancellationToken cancellationToken = default);

    /// <summary>Paths a commit changed, compared with its first parent.</summary>
    Task<IReadOnlyList<ChangedFile>> GetCommitChangesAsync(string path, string revision, CancellationToken cancellationToken = default);

    /// <summary>Paths that differ between two revisions.</summary>
    Task<IReadOnlyList<ChangedFile>> GetRangeChangesAsync(string path, string fromRevision, string toRevision, CancellationToken cancellationToken = default);

    // Batched reads for the UI: one or few git processes each, errors returned
    // in the result (not LastError) so they can run concurrently.
    Task<StatusSnapshot> GetStatusSnapshotAsync(string path, PathState mask = PathState.WorkingCopyMask, CancellationToken cancellationToken = default);
    Task<CommitResult> GetCommitAsync(string path, string revision, CancellationToken cancellationToken = default);
    Task<RefsSnapshot> GetRefsSnapshotAsync(string path, CancellationToken cancellationToken = default);
    Task<LogPage> GetLogPageAsync(string path, HistoryFilter query, CancellationToken cancellationToken = default);
    Task<bool> EnsureCommitGraphAsync(string path, CancellationToken cancellationToken = default);

    Task<string> GetDiffAsync(string path, DiffSpec request, CancellationToken cancellationToken = default);

    /// <summary>
    /// A file's content at <paramref name="objectSpec"/> ("rev:path", or ":path" for the
    /// index), as checked out (Git LFS content rather than its pointer). Output beyond
    /// <paramref name="maxBytes"/> is reported as <see cref="BinaryProcessResult.TooLarge"/>.
    /// </summary>
    Task<BinaryProcessResult> GetFileBytesAsync(string path, string objectSpec, long maxBytes, CancellationToken cancellationToken = default);

    Task<ProcessResult> StageAsync(string path, IReadOnlyList<ChangedFile> files, CancellationToken cancellationToken = default);
    Task<ProcessResult> UnstageAsync(string path, IReadOnlyList<ChangedFile> files, CancellationToken cancellationToken = default);
    Task<ProcessResult> DiscardAsync(string path, IReadOnlyList<ChangedFile> files, CancellationToken cancellationToken = default);
    Task<ProcessResult> ApplyPatchAsync(string path, string patch, bool cached, bool reverse, bool allowFuzzy = true, CancellationToken cancellationToken = default);

    Task<ProcessResult> CommitAsync(string path, string message, bool amend = false, bool signOff = false, bool gpgSign = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BranchRef>> GetBranchesAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetTagsAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> AddRemoteAsync(string path, string name, string url, CancellationToken cancellationToken = default);
    Task<ProcessResult> DeleteRemoteAsync(string path, string name, CancellationToken cancellationToken = default);
    Task<ProcessResult> RenameRemoteAsync(string path, string oldName, string newName, CancellationToken cancellationToken = default);
    Task<ProcessResult> SetRemoteUrlAsync(string path, string name, string url, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StashRecord>> GetStashesAsync(string path, CancellationToken cancellationToken = default);

    Task<ProcessResult> CreateBranchAsync(string path, string name, string? startPoint, bool checkout, CancellationToken cancellationToken = default);
    Task<ProcessResult> CheckoutAsync(string path, string reference, CancellationToken cancellationToken = default);
    Task<ProcessResult> DeleteBranchAsync(string path, string name, bool force, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a worktree at <paramref name="worktreePath"/>. With <paramref name="createBranch"/>, creates
    /// <paramref name="branch"/> at <paramref name="startPoint"/> (HEAD when null); otherwise checks out the
    /// existing <paramref name="branch"/>.
    /// </summary>
    Task<ProcessResult> AddWorktreeAsync(string path, string worktreePath, string? branch, bool createBranch, string? startPoint, CancellationToken cancellationToken = default);
    Task<ProcessResult> RemoveWorktreeAsync(string path, string worktreePath, bool force, CancellationToken cancellationToken = default);

    /// <summary>Forgets worktrees whose folders were deleted outside git.</summary>
    Task<ProcessResult> PruneWorktreesAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> CreateTagAsync(string path, string name, string? revision, string? message, CancellationToken cancellationToken = default);
    Task<ProcessResult> DeleteTagAsync(string path, string name, CancellationToken cancellationToken = default);

    Task<ProcessResult> FetchAsync(string path, string? remote = null, CancellationToken cancellationToken = default);
    Task<ProcessResult> PullAsync(string path, bool? rebase = null, CancellationToken cancellationToken = default);
    Task<ProcessResult> PushAsync(string path, string? remote = null, string? branch = null, bool setUpstream = false, bool force = false, string? detachedTarget = null, CancellationToken cancellationToken = default);

    Task<ProcessResult> StashSaveAsync(string path, string message, bool includeUntracked, CancellationToken cancellationToken = default);
    Task<ProcessResult> StashApplyAsync(string path, string name, bool pop, CancellationToken cancellationToken = default);
    Task<ProcessResult> StashDropAsync(string path, string name, CancellationToken cancellationToken = default);

    Task<ProcessResult> MergeAsync(string path, string revision, CancellationToken cancellationToken = default);
    Task<ProcessResult> CherryPickAsync(string path, string revision, CancellationToken cancellationToken = default);
    Task<ProcessResult> RevertAsync(string path, string revision, CancellationToken cancellationToken = default);
    Task<string?> GetCommitTemplateAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> CherryPickContinueAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> CherryPickAbortAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> CherryPickSkipAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> RevertContinueAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> RevertAbortAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> RebaseAsync(string path, string onto, CancellationToken cancellationToken = default);
    Task<ProcessResult> ResetAsync(string path, string revision, bool hard, CancellationToken cancellationToken = default);
    Task<ProcessResult> AbortMergeAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> AbortRebaseAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> ContinueRebaseAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> SkipRebaseCommitAsync(string path, CancellationToken cancellationToken = default);
    Task<string> GetInteractiveRebaseTodoAsync(string path, string baseRevision, bool fromRoot = false,
        CancellationToken cancellationToken = default);
    Task<ProcessResult> InteractiveRebaseAsync(string path, string baseRevision, string todo,
        IReadOnlyDictionary<string, string>? rewordMessages = null, bool fromRoot = false,
        CancellationToken cancellationToken = default);

    Task<ProcessResult> InitAsync(string path, CancellationToken cancellationToken = default);
    Task<ProcessResult> CloneAsync(string url, string destination, bool partial = false, CancellationToken cancellationToken = default);
}

/// <summary>A repository found on disk: its work-tree root and an engine of its own.</summary>
public sealed record GitRepository(string Root, GitRepoHandler Engine);

/// <summary>Finds repositories and creates engines that use one git executable.</summary>
public sealed class GitManager(string gitExecutable = "git")
{
    public string ExecutablePath { get; } = gitExecutable;

    /// <summary>An engine for work outside any open repository, such as clone and init.</summary>
    public GitRepoHandler Git { get; } = new(gitExecutable);

    /// <summary>Creates an isolated engine so each open repository has its own error state.</summary>
    public GitRepoHandler CreateEngine() => new(ExecutablePath);

    /// <summary>
    /// Opens the repository containing <paramref name="path"/> (the path itself or
    /// any folder inside it), or returns null when there is none.
    /// </summary>
    public GitRepository? Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        var root = FindRepositoryRoot(path);
        return root is null ? null : new GitRepository(root, CreateEngine());
    }

    /// <summary>
    /// True when <paramref name="path"/> is a work-tree root: it holds a
    /// <c>.git</c> directory, or a <c>.git</c> file (linked worktree or
    /// submodule) that points at one with <c>gitdir:</c>.
    /// </summary>
    public static bool IsRepositoryRoot(string path)
    {
        var dotGit = Path.Combine(path, ".git");
        if (Directory.Exists(dotGit))
        {
            return true;
        }

        if (!File.Exists(dotGit))
        {
            return false;
        }

        try
        {
            using var reader = new StreamReader(dotGit);
            return reader.ReadLine()?.TrimStart().StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Walks up from a path to the nearest enclosing work-tree root.</summary>
    public static string? FindRepositoryRoot(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (IsRepositoryRoot(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
