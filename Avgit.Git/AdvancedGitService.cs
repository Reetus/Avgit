using System.Globalization;
using Avgit.Core.Model;

namespace Avgit.Git;

public sealed record SubmoduleInfo(string Sha, string Path, string Description, char State);

/// <summary>
/// One entry of <c>git worktree list --porcelain</c>. <see cref="Branch"/> is the full ref
/// (<c>refs/heads/name</c>), empty when detached. The first entry is the main worktree.
/// </summary>
public sealed record WorktreeInfo(string Path, string Head, string Branch, bool IsBare, bool IsDetached,
    bool IsLocked = false, bool IsPrunable = false)
{
    public string BranchName => Branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? Branch["refs/heads/".Length..] : Branch;
}

public sealed record ReflogEntry(string ShortSha, string Selector, string Subject, string Date);

public sealed record BlameLine(int LineNumber, string Sha, string Author, string Date, string Text);

/// <summary>
/// Git features that sit outside the everyday log/stage/commit path. They all
/// use the same non-interactive git process runner, so missing optional tools
/// (git-lfs, git-flow, git-svn) surface as normal errors instead of hanging.
/// </summary>
public sealed class AdvancedGitService(GitRepoHandler git, string path)
{
    private readonly GitRepoHandler _git = git;

    public string Path { get; } = path;
    public string? LastError { get; private set; }

    private Task<ProcessResult> RunAsync(params string[] args)
        => _git.RunAsync(Path, args);

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> args, string? stdin = null)
        => _git.RunAsync(Path, args, stdin);

    // Submodules -----------------------------------------------------------
    public async Task<IReadOnlyList<SubmoduleInfo>> GetSubmodulesAsync()
    {
        LastError = null;
        ProcessResult result = await RunAsync("submodule", "status", "--recursive");
        var list = new List<SubmoduleInfo>();
        if (!result.Success)
        {
            LastError = result.Combined;
            return list;
        }

        foreach (var line in result.Output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length < 2)
            {
                continue;
            }

            var state = text[0];
            var body = text[1..];
            var space = body.IndexOf(' ');
            if (space <= 0)
            {
                continue;
            }

            var sha = body[..space];
            var rest = body[(space + 1)..].Trim();
            var description = string.Empty;
            var paren = rest.IndexOf(" (", StringComparison.Ordinal);
            if (paren >= 0)
            {
                description = rest[(paren + 2)..].TrimEnd(')');
                rest = rest[..paren];
            }
            list.Add(new SubmoduleInfo(sha, rest, description, state));
        }

        return list;
    }

    public Task<ProcessResult> SubmoduleInitAsync() => RunAsync("submodule", "init");
    public Task<ProcessResult> SubmoduleUpdateAsync() => RunAsync("submodule", "update", "--init", "--recursive");
    public Task<ProcessResult> SubmoduleSyncAsync() => RunAsync("submodule", "sync", "--recursive");
    public Task<ProcessResult> SubmoduleAddAsync(string url, string path, string? branch = null)
    {
        var args = new List<string> { "submodule", "add" };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            args.Add("--branch");
            args.Add(branch);
        }
        args.Add(url);
        args.Add(path);
        return RunAsync(args);
    }

    public Task<ProcessResult> SubmoduleDeinitAsync(string path)
        => RunAsync("submodule", "deinit", "-f", "--", path);

    public async Task<ProcessResult> SubmoduleRemoveAsync(string path)
    {
        ProcessResult deinit = await SubmoduleDeinitAsync(path).ConfigureAwait(false);
        if (!deinit.Success)
        {
            return deinit;
        }

        ProcessResult remove = await RunAsync("rm", "-f", "--", path).ConfigureAwait(false);
        if (!remove.Success)
        {
            return remove;
        }

        // `git rm` leaves the module's repository under .git/modules; removing
        // it allows the same path to be added again later.
        var modules = System.IO.Path.Combine(GitDir(Path), "modules",
            path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        try
        {
            if (Directory.Exists(modules))
            {
                Directory.Delete(modules, recursive: true);
            }
        }
        catch
        {
            // Best effort; a locked module can be removed manually.
        }

        return remove;
    }
    // Git LFS --------------------------------------------------------------
    public Task<ProcessResult> LfsInstallAsync() => RunAsync("lfs", "install", "--local");
    public Task<ProcessResult> LfsTrackAsync(string pattern) => RunAsync("lfs", "track", pattern);
    public Task<ProcessResult> LfsUntrackAsync(string pattern) => RunAsync("lfs", "untrack", pattern);
    public Task<ProcessResult> LfsFetchAsync() => RunAsync("lfs", "fetch", "--all");
    public Task<ProcessResult> LfsPullAsync() => RunAsync("lfs", "pull");
    public Task<ProcessResult> LfsLockAsync(string file) => RunAsync("lfs", "lock", file);
    public Task<ProcessResult> LfsUnlockAsync(string file) => RunAsync("lfs", "unlock", file);

    public async Task<IReadOnlyList<string>> GetLfsLocksAsync()
    {
        LastError = null;
        ProcessResult result = await RunAsync("lfs", "locks");
        if (!result.Success)
        {
            LastError = result.Combined;
            return [];
        }

        return [.. result.Output.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Trim().Length > 0
                           && !line.Contains("Listing", StringComparison.OrdinalIgnoreCase))];
    }

    public async Task<IReadOnlyList<string>> GetLfsPatternsAsync()
    {
        LastError = null;
        ProcessResult result = await RunAsync("lfs", "track");
        if (!result.Success)
        {
            LastError = result.Combined;
            return [];
        }

        var patterns = new List<string>();
        var excluded = false;
        foreach (var raw in result.Output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("Listing excluded patterns", StringComparison.OrdinalIgnoreCase))
            {
                excluded = true;
                continue;
            }
            if (line.Contains("Listing", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var suffix = text.LastIndexOf(" (.gitattributes)", StringComparison.OrdinalIgnoreCase);
            if (suffix > 0)
            {
                text = text[..suffix].Trim();
            }

            patterns.Add((excluded ? "! " : "") + text);
        }

        return patterns;
    }

    // Git Flow -------------------------------------------------------------
    public Task<ProcessResult> FlowInitAsync() => RunAsync("flow", "init", "-d");
    public Task<ProcessResult> FlowStartAsync(string kind, string name) => RunAsync("flow", kind, "start", name);

    public Task<ProcessResult> FlowFinishAsync(string kind, string name, string? tagMessage)
    {
        var args = new List<string> { "flow", kind, "finish", name };
        if (!string.IsNullOrWhiteSpace(tagMessage))
        {
            args.Add("-m");
            args.Add(tagMessage);
        }
        return RunAsync(args);
    }

    public Task<ProcessResult> FlowListAsync(string kind) => RunAsync("flow", kind, "list");

    // Subtree --------------------------------------------------------------
    public Task<ProcessResult> SubtreeAddAsync(string prefix, string url, string reference, bool squash)
    {
        var args = new List<string> { "subtree", "add", $"--prefix={prefix}", url, reference };
        if (squash)
        {
            args.Add("--squash");
        }

        return RunAsync(args);
    }

    public Task<ProcessResult> SubtreePullAsync(string prefix, string remote, string reference, bool squash)
    {
        var args = new List<string> { "subtree", "pull", $"--prefix={prefix}", remote, reference };
        if (squash)
        {
            args.Add("--squash");
        }

        return RunAsync(args);
    }

    public Task<ProcessResult> SubtreePushAsync(string prefix, string remote, string reference)
        => RunAsync("subtree", "push", $"--prefix={prefix}", remote, reference);

    // git-svn --------------------------------------------------------------
    public Task<ProcessResult> SvnFetchAsync() => RunAsync("svn", "fetch");
    public Task<ProcessResult> SvnRebaseAsync() => RunAsync("svn", "rebase");
    public Task<ProcessResult> SvnDcommitAsync() => RunAsync("svn", "dcommit");
    public Task<ProcessResult> SvnInfoAsync() => RunAsync("svn", "info");

    // Bisect ---------------------------------------------------------------
    public Task<ProcessResult> BisectStartAsync(string? bad, string? good)
    {
        var args = new List<string> { "bisect", "start" };
        if (!string.IsNullOrWhiteSpace(bad))
        {
            args.Add(bad);
        }

        if (!string.IsNullOrWhiteSpace(good))
        {
            args.Add(good);
        }

        return RunAsync(args);
    }

    public Task<ProcessResult> BisectGoodAsync(string? revision = null)
        => RunAsync(revision is null ? ["bisect", "good"] : ["bisect", "good", revision]);
    public Task<ProcessResult> BisectBadAsync(string? revision = null)
        => RunAsync(revision is null ? ["bisect", "bad"] : ["bisect", "bad", revision]);
    public Task<ProcessResult> BisectResetAsync() => RunAsync("bisect", "reset");
    public Task<ProcessResult> BisectLogAsync() => RunAsync("bisect", "log");

    // Worktrees ------------------------------------------------------------
    public async Task<IReadOnlyList<WorktreeInfo>> GetWorktreesAsync()
    {
        LastError = null;
        ProcessResult result = await RunAsync("worktree", "list", "--porcelain");
        if (!result.Success)
        {
            LastError = result.Combined;
            return [];
        }

        return GitRepoHandler.ParseWorktrees(result.Output);
    }

    /// <summary>With <paramref name="branch"/>, creates it at <paramref name="startPoint"/>; otherwise checks out the start point.</summary>
    public Task<ProcessResult> WorktreeAddAsync(string path, string? branch, string? startPoint)
        => string.IsNullOrWhiteSpace(branch)
            ? _git.AddWorktreeAsync(Path, path, startPoint, createBranch: false, startPoint: null)
            : _git.AddWorktreeAsync(Path, path, branch, createBranch: true, startPoint);

    public Task<ProcessResult> WorktreeRemoveAsync(string path, bool force)
        => _git.RemoveWorktreeAsync(Path, path, force);

    // Patch / archive ------------------------------------------------------
    public async Task<string> CreatePatchAsync(string revision)
    {
        // format-patch silently skips merge commits and, with -1, emits a
        // parent patch instead. Refuse rather than write the wrong commit.
        ProcessResult parents = await RunAsync("rev-list", "--parents", "-n", "1", revision);
        if (!parents.Success)
        {
            throw new InvalidOperationException(parents.Combined);
        }

        var ids = parents.Output.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length > 2)
        {
            throw new InvalidOperationException(
                "A merge commit cannot be exported as a single format-patch file. Select a non-merge commit.");
        }

        ProcessResult result = await RunAsync("format-patch", "-1", "--stdout", revision);
        return result.Success ? result.Output : throw new InvalidOperationException(result.Combined);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetConflictStagesAsync(string file)
    {
        var stages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((var name, var stage) in new (string, int)[] { ("base", 1), ("local", 2), ("remote", 3) })
        {
            ProcessResult result = await RunAsync("checkout-index", $"--stage={stage}", "--temp", "--", file)
                .ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            {
                continue;
            }

            var line = result.Output.Trim();
            var tab = line.IndexOf('\t');
            var path = tab >= 0 ? line[(tab + 1)..] : line;
            if (path.Length == 0)
            {
                continue;
            }

            stages[name] = System.IO.Path.IsPathRooted(path)
                ? path
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, path));
        }

        return stages;
    }

    // Notes ----------------------------------------------------------------
    public Task<ProcessResult> NotesShowAsync(string revision) => RunAsync("notes", "show", revision);
    public Task<ProcessResult> NotesAddAsync(string revision, string message)
        => RunAsync("notes", "add", "-m", message, revision);
    public Task<ProcessResult> NotesRemoveAsync(string revision) => RunAsync("notes", "remove", revision);

    // Sparse checkout ------------------------------------------------------
    public Task<ProcessResult> SparseCheckoutInitAsync() => RunAsync("sparse-checkout", "init", "--cone");
    public Task<ProcessResult> SparseCheckoutSetAsync(IEnumerable<string> patterns)
    {
        var args = new List<string> { "sparse-checkout", "set" };
        args.AddRange(patterns);
        return RunAsync(args);
    }
    public Task<ProcessResult> SparseCheckoutDisableAsync() => RunAsync("sparse-checkout", "disable");

    public Task<ProcessResult> ApplyPatchAsync(string file) => RunAsync("apply", file);

    public Task<ProcessResult> CreateArchiveAsync(string revision, string outputPath, string format = "zip")
        => RunAsync("archive", "--format=" + format, "--output=" + outputPath, revision);

    // Diagnostics ----------------------------------------------------------
    public async Task<IReadOnlyList<ReflogEntry>> GetReflogAsync(int limit = 100)
    {
        LastError = null;
        ProcessResult result = await RunAsync("reflog", $"--max-count={limit}", "--format=%h%x1f%gd%x1f%gs%x1f%ci");
        var list = new List<ReflogEntry>();
        if (!result.Success)
        {
            LastError = result.Combined;
            return list;
        }

        foreach (var line in result.Output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\x1f');
            if (parts.Length >= 4)
            {
                list.Add(new ReflogEntry(parts[0], parts[1], parts[2], parts[3]));
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<BlameLine>> GetBlameAsync(string file)
    {
        LastError = null;
        ProcessResult result = await RunAsync("blame", "--line-porcelain", "--", file);
        var list = new List<BlameLine>();
        if (!result.Success)
        {
            LastError = result.Combined;
            return list;
        }

        var sha = string.Empty;
        var author = string.Empty;
        var date = string.Empty;
        var lineNumber = 0;
        foreach (var line in result.Output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.StartsWith("author ", StringComparison.Ordinal))
            {
                author = text["author ".Length..];
            }
            else if (text.StartsWith("author-time ", StringComparison.Ordinal)
                     && long.TryParse(text["author-time ".Length..], out var seconds))
            {
                date = DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            }
            else if (text.StartsWith('\t'))
            {
                lineNumber++;
                list.Add(new BlameLine(lineNumber, sha, author, date, text[1..]));
            }
            else if (text.Length >= 40 && text[..40].All(Uri.IsHexDigit))
            {
                sha = text[..40];
                var fields = text.Split(' ');
                if (fields.Length >= 3 && int.TryParse(fields[2], out var originalLine))
                {
                    lineNumber = originalLine - 1;
                }
            }
        }

        return list;
    }

    public async Task<string> GetReflogTextAsync() => (await RunAsync("reflog", "-n", "100")).Output;

    public Task<ProcessResult> SearchLogAsync(string text, int limit = 200)
        => RunAsync("log", $"--max-count={limit}", "--all", "--fixed-strings", "--regexp-ignore-case",
            "--grep=" + text, "--format=%h %s");

    public async Task<string?> GetHooksPathAsync()
    {
        ProcessResult absolute = await RunAsync("rev-parse", "--path-format=absolute", "--git-path", "hooks");
        if (absolute.Success && !string.IsNullOrWhiteSpace(absolute.Output))
        {
            return absolute.Output.Trim();
        }

        ProcessResult relative = await RunAsync("rev-parse", "--git-path", "hooks");
        if (!relative.Success || string.IsNullOrWhiteSpace(relative.Output))
        {
            return null;
        }

        var hooks = relative.Output.Trim();
        return System.IO.Path.IsPathRooted(hooks)
            ? hooks
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, hooks));
    }

    public Task<ProcessResult> CherryPickAsync(string revision) => RunAsync("cherry-pick", revision);
    public Task<ProcessResult> RevertAsync(string revision) => RunAsync("revert", "--no-edit", revision);
    public Task<ProcessResult> RebasePreservingMergesAsync(string onto) => RunAsync("rebase", "--rebase-merges", onto);
    public Task<ProcessResult> RebaseRootPreservingMergesAsync() => RunAsync("rebase", "--root", "--rebase-merges");
    public Task<ProcessResult> TagPushAsync(string tag, string? remote = null)
        => RunAsync(remote is null ? ["push", "origin", tag] : ["push", remote, tag]);

    public Task<ProcessResult> StashShowAsync(string name)
        => RunAsync("stash", "show", "-p", "--include-untracked", name);
    public Task<ProcessResult> GrepAsync(string text) => RunAsync("grep", "-n", "--", text);
    public Task<ProcessResult> RenameBranchAsync(string oldName, string newName) => RunAsync("branch", "-m", oldName, newName);
    public Task<ProcessResult> DeleteRemoteBranchAsync(string remote, string branch)
        => RunAsync("push", remote, "--delete", branch);

    public static string HooksPath(string gitDir) => System.IO.Path.Combine(gitDir, "hooks");
    public static string GitDir(string repositoryPath) => GitRepoHandler.GetGitDir(repositoryPath);
}
