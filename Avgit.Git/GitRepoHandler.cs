using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avgit.Core.Diff;
using Avgit.Core.Model;

namespace Avgit.Git;

/// <summary>
/// Git engine. Every operation runs the git command line with explicit
/// argument lists and parses its machine-readable output, so no native Git
/// library is required.
/// </summary>
public sealed partial class GitRepoHandler(string executablePath = "git") : IGitEngine
{
    private const char UnitSeparator = '\x1e';
    private const string LogFormat = "%H%x1e%h%x1e%P%x1e%ci%x1e%cn <%ce>%x1e%an <%ae>%x1e%d%x1e%s";
    private readonly Lock _errorLock = new();

    public string ExecutablePath { get; set; } = executablePath;
    public string? SshCommand { get; set; }
    public string? CredentialHelper { get; set; }
    public string? LastError
    {
        get
        {
            lock (_errorLock)
            {
                return field;
            }
        }

        private set;
    }

    public void ClearLastError()
    {
        lock (_errorLock)
        {
            LastError = null;
        }
    }

    private void ReportFailure(string operation, ProcessResult result)
    {

        if (result.Success || result.Cancelled)
        {
            return;
        }

        var error = DescribeFailure(operation, result);
        lock (_errorLock)
        {
            LastError = error;
        }
    }

    public Task<ProcessResult> RunAsync(string path, IReadOnlyList<string> arguments, string? standardInput = null, CancellationToken cancellationToken = default)
        => RunCoreAsync(path, arguments, standardInput, null, cancellationToken);

    private Task<ProcessResult> RunCoreAsync(string path, IReadOnlyList<string> arguments, string? standardInput,
        IReadOnlyDictionary<string, string>? extraEnvironment, CancellationToken cancellationToken)
        => ProcessRunner.RunAsync(ExecutablePath, path, BuildArguments(arguments), standardInput,
            BuildEnvironment(extraEnvironment), cancellationToken);

    private List<string> BuildArguments(IReadOnlyList<string> arguments)
    {
        var args = new List<string>(arguments.Count + 7)
        {
            "-c", "core.quotepath=false",
            "-c", "color.ui=false",
            "--no-pager"
        };
        AddCredentialHelper(args);
        args.AddRange(arguments);
        return args;
    }

    private void AddCredentialHelper(List<string> args)
    {
        if (string.IsNullOrWhiteSpace(CredentialHelper))
        {
            return;
        }

        args.Add("-c");
        args.Add("credential.helper=" + CredentialHelper);
    }

    private Dictionary<string, string>? BuildEnvironment(IReadOnlyDictionary<string, string>? extra)
    {
        if (string.IsNullOrWhiteSpace(SshCommand) && extra is null)
        {
            return null;
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(SshCommand))
        {
            environment["GIT_SSH_COMMAND"] = SshCommand;
        }

        if (extra is not null)
        {
            foreach (KeyValuePair<string, string> pair in extra)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        return environment;
    }

    private async Task<ProcessResult> GitAsync(string path, CancellationToken ct, params string[] args)
        => await RunAsync(path, args, null, ct).ConfigureAwait(false);

    private async Task<ProcessResult> GitAsync(string path, CancellationToken ct, IReadOnlyList<string> args, string? stdin = null)
        => await RunAsync(path, args, stdin, ct).ConfigureAwait(false);

    public async Task<string> GetCurrentRevisionAsync(string path, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await GitAsync(path, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        return result.Success ? result.Output.Trim() : string.Empty;
    }

    public async Task<string> GetCurrentBranchAsync(string path, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await GitAsync(path, cancellationToken, "symbolic-ref", "--short", "-q", "HEAD").ConfigureAwait(false);
        return result.Success ? result.Output.Trim() : RepoSummary.DetachedHeadLabel;
    }

    public async Task<StatusSnapshot> GetStatusAsync(string path, PathState mask = PathState.WorkingCopyMask, CancellationToken cancellationToken = default)
    {
        StatusSnapshot snapshot = await GetStatusSnapshotAsync(path, mask, cancellationToken).ConfigureAwait(false);
        if (snapshot.Error is not null)
        {
            lock (_errorLock)
            {
                LastError = snapshot.Error;
            }
        }

        return snapshot;
    }

    private static ChangedFile CreateRecord(string repoPath, string filePath, string? oldPath, ChangeArea area,
        PathState status, PathState indexStatus, PathState workTreeStatus)
    {
        return new ChangedFile
        {
            Area = area,
            RelativePath = filePath,
            FileName = Path.GetFileName(filePath),
            FullPath = Path.Combine(repoPath, filePath.Replace('/', Path.DirectorySeparatorChar)),
            Status = status,
            IndexStatus = indexStatus,
            WorkTreeStatus = workTreeStatus,
            OldPath = oldPath
        };
    }

    public async Task<IReadOnlyList<HistoryRow>> GetLogAsync(string path, HistoryFilter query, CancellationToken cancellationToken = default)
    {

        LogPage page = await GetLogPageAsync(path, query, cancellationToken).ConfigureAwait(false);
        if (page.Error is not null)
        {
            lock (_errorLock)
            {
                LastError = page.Error;
            }
        }

        return page.Rows;
    }

    private static List<RefBadge> ParseDecorations(string decorations)
    {
        var labels = new List<RefBadge>();
        var text = decorations.Trim();
        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            text = text[1..^1];
        }

        if (text.Length == 0)
        {
            return labels;
        }

        foreach (var raw in text.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var item = raw.Trim();
            var current = false;
            if (item.StartsWith("HEAD -> ", StringComparison.Ordinal))
            {
                current = true;
                item = item["HEAD -> ".Length..];
            }
            else if (item == "HEAD")
            {
                labels.Add(new RefBadge("HEAD", RefKind.Branch, string.Empty, string.Empty) { IsCheckedOut = true });
                continue;
            }

            if (item.StartsWith("tag: ", StringComparison.Ordinal))
            {
                var name = item["tag: ".Length..];
                name = name.StartsWith("refs/tags/", StringComparison.Ordinal) ? name["refs/tags/".Length..] : name;
                labels.Add(new RefBadge(name, RefKind.Tag, item, item));
            }
            else if (item.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                var name = item["refs/heads/".Length..];
                labels.Add(new RefBadge(name, RefKind.Branch, item, item) { IsCheckedOut = current });
            }
            else if (item.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var name = item["refs/remotes/".Length..];
                labels.Add(new RefBadge(name, RefKind.RemoteBranch, item, item) { IsCheckedOut = current });
            }
            else if (item.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                var name = item["refs/tags/".Length..];
                labels.Add(new RefBadge(name, RefKind.Tag, item, item));
            }
            else if (item.Length > 0)
            {
                labels.Add(new RefBadge(item, RefKind.Other, item, item) { IsCheckedOut = current });
            }
        }

        return labels;
    }

    public async Task<CommitRecord?> GetCommitDetailsAsync(string path, string revision, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision == "0")
        {
            return null;
        }

        const string format = "%H%x1e%h%x1e%P%x1e%an%x1e%ae%x1e%cn%x1e%ce%x1e%aI%x1e%cI%x1e%D%x1e%B";
        ProcessResult result = await GitAsync(path, cancellationToken, "show", "-s", "--decorate=full", "--format=" + format, revision).ConfigureAwait(false);
        if (!result.Success)
        {
            ReportFailure("git show", result);
            return null;
        }

        var fields = result.Output.Split(UnitSeparator);
        if (fields.Length < 11)
        {
            return null;
        }

        DateTimeOffset.TryParse(fields[7], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset authorDate);
        DateTimeOffset.TryParse(fields[8], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset commitDate);
        var message = string.Join(UnitSeparator.ToString(), fields.Skip(10)).TrimEnd('\r', '\n');

        return new CommitRecord
        {
            Sha = fields[0],
            Id = fields[0],
            ShortSha = fields[1],
            ParentIds = fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries),
            Author = $"{fields[3]} <{fields[4]}>",
            Committer = $"{fields[5]} <{fields[6]}>",
            AuthorDate = authorDate.LocalDateTime,
            CommitDate = commitDate.LocalDateTime,
            Message = message,
            Title = message.Split('\n').FirstOrDefault() ?? string.Empty,
            Tags = ParseTagDecorations(fields[9])
        };
    }

    public async Task<IReadOnlyList<ChangedFile>> GetCommitChangesAsync(string path, string revision, CancellationToken cancellationToken = default)
    {
        var list = new List<ChangedFile>();
        ProcessResult parentsResult = await GitAsync(path, cancellationToken, "rev-list", "--parents", "-n", "1", revision).ConfigureAwait(false);
        if (!parentsResult.Success)
        {
            ReportFailure("git rev-list", parentsResult);
            return list;
        }

        var ids = parentsResult.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ProcessResult diff;
        if (ids.Length > 1)
        {
            diff = await GitAsync(path, cancellationToken, "diff", "--name-status", "-z", "-M", "-C", ids[1], revision).ConfigureAwait(false);
        }
        else
        {
            diff = await GitAsync(path, cancellationToken, "show", "--name-status", "-z", "--format=", "-M", "-C", revision).ConfigureAwait(false);
        }

        if (!diff.Success)
        {
            ReportFailure("git name-status", diff);
            return list;
        }

        return ParseNameStatus(path, diff.Output);
    }

    public async Task<IReadOnlyList<ChangedFile>> GetRangeChangesAsync(string path, string fromRevision, string toRevision, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await GitAsync(path, cancellationToken, "diff", "--name-status", "-z", "-M", "-C", fromRevision, toRevision).ConfigureAwait(false);
        if (!result.Success)
        {
            ReportFailure("git diff", result);
            return [];
        }

        return ParseNameStatus(path, result.Output);
    }

    public async Task<IReadOnlyList<ChangedFile>> GetStashChangesAsync(string path, string stash, CancellationToken cancellationToken = default)
    {
        // The untracked files are the tree of the stash's third parent, which exists
        // only for stashes made with --include-untracked.
        Task<ProcessResult> untrackedTask = GitAsync(path, cancellationToken, "ls-tree", "-r", "-z", "--name-only", stash + "^3");
        ProcessResult result = await StashShowAsync(path, cancellationToken, ["--name-status", "-z", "-M", "-C"], stash).ConfigureAwait(false);
        ProcessResult untracked = await untrackedTask.ConfigureAwait(false);
        if (!result.Success)
        {
            ReportFailure("git stash show", result);
            return [];
        }

        List<ChangedFile> files = ParseNameStatus(path, result.Output);
        if (untracked.Success)
        {
            var untrackedPaths = untracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            foreach (ChangedFile file in files)
            {
                if (file.Status == PathState.New && untrackedPaths.Contains(file.RelativePath))
                {
                    file.Status = PathState.Untracked;
                    file.IndexStatus = PathState.Untracked;
                }
            }
        }

        return files;
    }

    /// <summary>
    /// Runs <c>git stash show</c> with untracked files included. That option needs git
    /// 2.32; older versions reject it, so a failure retries with tracked changes only.
    /// </summary>
    private async Task<ProcessResult> StashShowAsync(string path, CancellationToken cancellationToken,
        IReadOnlyList<string> options, string stash)
    {
        ProcessResult result = await GitAsync(path, cancellationToken, BuildStashShow(true)).ConfigureAwait(false);
        if (result.Success || result.Cancelled)
        {
            return result;
        }

        return await GitAsync(path, cancellationToken, BuildStashShow(false)).ConfigureAwait(false);

        List<string> BuildStashShow(bool includeUntracked)
        {
            List<string> args = ["stash", "show", .. options];
            if (includeUntracked)
            {
                args.Add("--include-untracked");
            }

            args.Add(stash);
            return args;
        }
    }

    public async Task<BinaryProcessResult> GetFileBytesAsync(string path, string objectSpec, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        // --filters gives the checked-out form, so Git LFS pointers become their real
        // content. It fails when a required filter is missing; fall back to the raw blob.
        BinaryProcessResult result = await ProcessRunner.RunBytesAsync(ExecutablePath, path,
            BuildArguments(["cat-file", "--filters", objectSpec]), maxBytes, BuildEnvironment(null), cancellationToken).ConfigureAwait(false);
        if (result.Success || result.TooLarge || result.Cancelled)
        {
            return result;
        }

        return await ProcessRunner.RunBytesAsync(ExecutablePath, path,
            BuildArguments(["cat-file", "blob", objectSpec]), maxBytes, BuildEnvironment(null), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetDiffAsync(string path, DiffSpec request, CancellationToken cancellationToken = default)
    {
        var files = request.Files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        var args = new List<string>();
        var ignoreWhitespace = request.IgnoreWhitespace ? "-w" : null;

        switch (request.Kind)
        {
            case DiffRequestKind.WorkingCopy:
                {
                    if (files.Count == 1)
                    {
                        ProcessResult tracked = await GitAsync(path, cancellationToken, "ls-files", "--error-unmatch", "--", files[0]).ConfigureAwait(false);
                        if (!tracked.Success)
                        {
                            var abs = Path.Combine(path, files[0].Replace('/', Path.DirectorySeparatorChar));
                            return SyntheticDiff.ForUntrackedFile(files[0], abs);
                        }
                    }

                    args.Add("diff");
                    args.Add("--no-color");
                    args.Add("--no-ext-diff");
                    args.Add("--src-prefix=a/");
                    args.Add("--dst-prefix=b/");
                    args.Add($"--unified={request.Context}");
                    if (ignoreWhitespace is not null)
                    {
                        args.Add(ignoreWhitespace);
                    }

                    args.Add("-M");
                    args.Add("-C");
                    args.Add("--");
                    args.AddRange(files);
                    break;
                }
            case DiffRequestKind.Staged:
                {
                    args.Add("diff");
                    args.Add("--cached");
                    args.Add("--no-color");
                    args.Add("--no-ext-diff");
                    args.Add("--src-prefix=a/");
                    args.Add("--dst-prefix=b/");
                    args.Add($"--unified={request.Context}");
                    if (ignoreWhitespace is not null)
                    {
                        args.Add(ignoreWhitespace);
                    }

                    args.Add("-M");
                    args.Add("-C");
                    args.Add("--");
                    args.AddRange(files);
                    break;
                }
            case DiffRequestKind.Commit:
                {
                    var rev = request.Revision ?? "HEAD";
                    string[] ids;
                    if (request.FirstParent is { } knownParent)
                    {
                        ids = knownParent.Length == 0 ? [rev] : [rev, knownParent];
                    }
                    else
                    {
                        ProcessResult parentsResult = await GitAsync(path, cancellationToken, "rev-list", "--parents", "-n", "1", rev).ConfigureAwait(false);
                        if (!parentsResult.Success)
                        {
                            ReportFailure("git rev-list", parentsResult);
                        }

                        ids = parentsResult.Success ? parentsResult.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
                    }

                    if (ids.Length > 1)
                    {
                        args.Add("diff");
                        args.Add("--no-color");
                        args.Add("--no-ext-diff");
                        args.Add("--src-prefix=a/");
                        args.Add("--dst-prefix=b/");
                        args.Add($"--unified={request.Context}");
                        if (ignoreWhitespace is not null)
                        {
                            args.Add(ignoreWhitespace);
                        }

                        args.Add("-M");
                        args.Add("-C");
                        args.Add(ids[1]);
                        args.Add(rev);
                    }
                    else
                    {
                        args.Add("show");
                        args.Add("--format=");
                        args.Add("--no-color");
                        args.Add("--no-ext-diff");
                        args.Add("--src-prefix=a/");
                        args.Add("--dst-prefix=b/");
                        args.Add($"--unified={request.Context}");
                        if (ignoreWhitespace is not null)
                        {
                            args.Add(ignoreWhitespace);
                        }

                        args.Add("-M");
                        args.Add("-C");
                        args.Add(rev);
                    }

                    args.Add("--");
                    args.AddRange(files);
                    break;
                }
            case DiffRequestKind.CommitRange:
                {
                    args.Add("diff");
                    args.Add("--no-color");
                    args.Add("--no-ext-diff");
                    args.Add("--src-prefix=a/");
                    args.Add("--dst-prefix=b/");
                    args.Add($"--unified={request.Context}");
                    if (ignoreWhitespace is not null)
                    {
                        args.Add(ignoreWhitespace);
                    }

                    args.Add("-M");
                    args.Add("-C");
                    args.Add(request.BaseRevision ?? "HEAD^");
                    args.Add(request.Revision ?? "HEAD");
                    args.Add("--");
                    args.AddRange(files);
                    break;
                }
            case DiffRequestKind.FileContent:
                {
                    var rel = request.FilePath ?? files.FirstOrDefault() ?? string.Empty;
                    var abs = Path.Combine(path, rel.Replace('/', Path.DirectorySeparatorChar));
                    FileDiff file = SyntheticDiff.CreateContentFile(rel, abs, new DiffSetup { Subject = DiffSubject.PendingFiles });
                    return PatchBuilder.CreatePatchFromFile(file);
                }
            case DiffRequestKind.Stash:
                {
                    var stash = request.Revision ?? "stash@{0}";
                    List<string> options = ["--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", $"--unified={request.Context}"];
                    if (ignoreWhitespace is not null)
                    {
                        options.Add(ignoreWhitespace);
                    }

                    options.Add("-M");
                    options.Add("-C");
                    if (files.Count == 0)
                    {
                        ProcessResult all = await StashShowAsync(path, cancellationToken, ["-p", .. options], stash).ConfigureAwait(false);
                        if (!all.Success)
                        {
                            ReportFailure("git stash show", all);
                        }

                        return all.Output;
                    }

                    // git stash show takes no paths. A tracked file's change is the stash
                    // against its first parent; an untracked file is in the third parent,
                    // a root commit, so showing that commit gives it as an addition.
                    ProcessResult tracked = await GitAsync(path, cancellationToken,
                        ["diff", .. options, stash + "^1", stash, "--", .. files]).ConfigureAwait(false);
                    if (!tracked.Success)
                    {
                        ReportFailure("git diff", tracked);
                        return tracked.Output;
                    }

                    if (tracked.Output.Length > 0)
                    {
                        return tracked.Output;
                    }

                    // Fails when the stash has no untracked files; there is then no diff.
                    ProcessResult untracked = await GitAsync(path, cancellationToken,
                        ["show", "--format=", .. options, stash + "^3", "--", .. files]).ConfigureAwait(false);
                    return untracked.Success ? untracked.Output : string.Empty;
                }
        }

        ProcessResult result = await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
        if (!result.Success)
        {
            ReportFailure("git diff", result);
        }

        return result.Output;
    }

    public async Task<ProcessResult> StageAsync(string path, IReadOnlyList<ChangedFile> files, CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            return new ProcessResult(0, string.Empty, string.Empty);
        }

        var args = new List<string> { "add", "-A", "--" };
        args.AddRange(files.Select(f => f.RelativePath).Distinct());
        return await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
    }

    public async Task<ProcessResult> UnstageAsync(string path, IReadOnlyList<ChangedFile> files, CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            return new ProcessResult(0, string.Empty, string.Empty);
        }

        var paths = new List<string>();
        foreach (ChangedFile file in files)
        {
            paths.Add(file.RelativePath);
            if (file.Status is PathState.Moved or PathState.Duplicated
                && !string.IsNullOrEmpty(file.OldPath))
            {
                paths.Add(file.OldPath!);
            }
        }

        var args = new List<string> { "reset", "-q", "HEAD", "--" };
        args.AddRange(paths.Distinct(StringComparer.Ordinal));
        ProcessResult result = await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
        if (result.Success)
        {
            return result;
        }

        var rmArgs = new List<string> { "rm", "--cached", "-f", "--" };
        rmArgs.AddRange(files.Select(f => f.RelativePath).Distinct());
        return await GitAsync(path, cancellationToken, rmArgs).ConfigureAwait(false);
    }

    public async Task<ProcessResult> DiscardAsync(string path, IReadOnlyList<ChangedFile> files, CancellationToken cancellationToken = default)
    {
        ProcessResult? last = null;
        ProcessResult? firstFailure = null;
        foreach (ChangedFile file in files)
        {
            var rel = file.RelativePath;
            var abs = Path.Combine(path, rel.Replace('/', Path.DirectorySeparatorChar));

            if (file.Status == PathState.Conflicted)
            {
                return firstFailure ?? new ProcessResult(-1, string.Empty,
                    string.Format(GitStrings.Discard_Conflicted, rel));
            }

            if (file.Status == PathState.Untracked)
            {
                if (!TryDelete(abs))
                {
                    firstFailure ??= new ProcessResult(-1, string.Empty, string.Format(GitStrings.Discard_CouldNotDelete, rel));
                }

                continue;
            }

            if (file.Status == PathState.New)
            {
                ProcessResult remove = await GitAsync(path, cancellationToken, "rm", "--cached", "-f", "--", rel).ConfigureAwait(false);
                if (!remove.Success)
                {
                    firstFailure ??= remove;
                    continue;
                }
                if (!TryDelete(abs))
                {
                    firstFailure ??= new ProcessResult(-1, string.Empty, string.Format(GitStrings.Discard_CouldNotDelete, rel));
                }

                continue;
            }

            if (!file.IsStaged)
            {
                // Discarding an unstaged record must restore only the worktree from
                // the index; resetting --staged would destroy already-staged work.
                last = await GitAsync(path, cancellationToken, "restore", "--worktree", "--", rel).ConfigureAwait(false);
                if (!last.Success)
                {
                    last = await GitAsync(path, cancellationToken, "checkout", "--", rel).ConfigureAwait(false);
                    if (!last.Success)
                    {
                        firstFailure ??= last;
                    }
                }
                continue;
            }

            var paths = new List<string> { rel };
            if (file.Status is PathState.Moved or PathState.Duplicated
                && !string.IsNullOrEmpty(file.OldPath)
                && !string.Equals(file.OldPath, rel, StringComparison.Ordinal))
            {
                paths.Add(file.OldPath!);
            }

            var restoreArgs = new List<string> { "restore", "--source=HEAD", "--staged", "--worktree", "--" };
            restoreArgs.AddRange(paths);
            last = await GitAsync(path, cancellationToken, restoreArgs).ConfigureAwait(false);
            if (!last.Success)
            {
                var checkoutArgs = new List<string> { "checkout", "HEAD", "--" };
                checkoutArgs.AddRange(paths);
                last = await GitAsync(path, cancellationToken, checkoutArgs).ConfigureAwait(false);
            }
        }

        return firstFailure ?? last ?? new ProcessResult(0, string.Empty, string.Empty);
    }

    private static bool TryDelete(string absolutePath)
    {
        try
        {
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }
            else if (Directory.Exists(absolutePath))
            {
                Directory.Delete(absolutePath, recursive: true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ProcessResult> ApplyPatchAsync(string path, string patch, bool cached, bool reverse, bool allowFuzzy = true, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "apply" };
        if (cached)
        {
            args.Add("--cached");
        }
        else
        {
            args.Add("--reject");
        }

        if (reverse)
        {
            args.Add("-R");
        }

        if (allowFuzzy)
        {
            args.Add("-C1");
        }

        args.Add("--whitespace=nowarn");
        return await GitAsync(path, cancellationToken, args, patch).ConfigureAwait(false);
    }

    public async Task<ProcessResult> CommitAsync(string path, string message, bool amend = false, bool signOff = false, bool gpgSign = false, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "commit", "--cleanup=strip", "-m", message };
        if (amend)
        {
            args.Add("--amend");
        }

        if (signOff)
        {
            args.Add("--signoff");
        }

        if (gpgSign)
        {
            args.Add("-S");
        }

        return await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BranchRef>> GetBranchesAsync(string path, CancellationToken cancellationToken = default)
    {

        ProcessResult result = await GitAsync(path, cancellationToken, "for-each-ref", "--format=" + BranchRefFormat,
            "refs/heads", "refs/remotes").ConfigureAwait(false);
        if (!result.Success)
        {
            ReportFailure("git for-each-ref", result);
            return [];
        }

        return ParseBranches(result.Output);
    }

    private static void ParseTrack(string text, out uint ahead, out uint behind)
    {
        ahead = 0;
        behind = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Match match = System.Text.RegularExpressions.Regex.Match(text, @"ahead (\d+)");
        if (match.Success)
        {
            uint.TryParse(match.Groups[1].Value, out ahead);
        }

        match = System.Text.RegularExpressions.Regex.Match(text, @"behind (\d+)");
        if (match.Success)
        {
            uint.TryParse(match.Groups[1].Value, out behind);
        }
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(string path, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await GitAsync(path, cancellationToken, "tag", "--list").ConfigureAwait(false);
        if (!result.Success)
        {
            ReportFailure("git tag", result);
            return [];
        }

        return [.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())];
    }

    private Task<ProcessResult> GitWithEnvAsync(string path, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
        => RunCoreAsync(path, args, null, environment, cancellationToken);

    public async Task<ProcessResult> AddRemoteAsync(string path, string name, string url, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "remote", "add", name, url).ConfigureAwait(false);

    public async Task<ProcessResult> DeleteRemoteAsync(string path, string name, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "remote", "remove", name).ConfigureAwait(false);

    public async Task<ProcessResult> RenameRemoteAsync(string path, string oldName, string newName,
        CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "remote", "rename", oldName, newName).ConfigureAwait(false);

    public async Task<ProcessResult> SetRemoteUrlAsync(string path, string name, string url,
        CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "remote", "set-url", name, url).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string path, CancellationToken cancellationToken = default)
    {

        ProcessResult result = await GitAsync(path, cancellationToken, "remote", "-v").ConfigureAwait(false);
        if (!result.Success)
        {
            ReportFailure("git remote", result);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return ParseRemotes(result.Output);
    }

    public async Task<IReadOnlyList<StashRecord>> GetStashesAsync(string path, CancellationToken cancellationToken = default)
    {

        ProcessResult result = await GitAsync(path, cancellationToken, "stash", "list", StashListFormat).ConfigureAwait(false);
        if (!result.Success)
        {
            ReportFailure("git stash", result);
            return [];
        }

        return ParseStashes(result.Output);
    }

    public async Task<ProcessResult> CreateBranchAsync(string path, string name, string? startPoint, bool checkout, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { checkout ? "checkout" : "branch" };
        if (checkout)
        {
            args.Add("-b");
        }

        args.Add(name);
        if (!string.IsNullOrWhiteSpace(startPoint))
        {
            args.Add(startPoint);
        }

        return await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
    }

    public async Task<ProcessResult> CheckoutAsync(string path, string reference, CancellationToken cancellationToken = default)
    {
        ProcessResult local = await GitAsync(path, cancellationToken, "show-ref", "--verify", "--quiet", "refs/heads/" + reference).ConfigureAwait(false);
        if (local.Success)
        {
            return await GitAsync(path, cancellationToken, "checkout", reference).ConfigureAwait(false);
        }

        ProcessResult remote = await GitAsync(path, cancellationToken, "show-ref", "--verify", "--quiet", "refs/remotes/" + reference).ConfigureAwait(false);
        if (remote.Success)
        {
            var slash = reference.IndexOf('/');
            var localName = slash >= 0 && slash + 1 < reference.Length ? reference[(slash + 1)..] : reference;
            ProcessResult existingLocal = await GitAsync(path, cancellationToken, "show-ref", "--verify", "--quiet", "refs/heads/" + localName).ConfigureAwait(false);
            if (existingLocal.Success)
            {
                return await GitAsync(path, cancellationToken, "checkout", localName).ConfigureAwait(false);
            }

            ProcessResult tracked = await GitAsync(path, cancellationToken, "checkout", "--track", "-b", localName, reference).ConfigureAwait(false);
            if (tracked.Success)
            {
                return tracked;
            }
        }

        return await GitAsync(path, cancellationToken, "checkout", reference).ConfigureAwait(false);
    }

    public async Task<ProcessResult> DeleteBranchAsync(string path, string name, bool force, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "branch", force ? "-D" : "-d", name).ConfigureAwait(false);

    public async Task<ProcessResult> AddWorktreeAsync(string path, string worktreePath, string? branch, bool createBranch,
        string? startPoint, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "worktree", "add" };
        if (createBranch && !string.IsNullOrWhiteSpace(branch))
        {
            args.Add("-b");
            args.Add(branch);
        }

        args.Add("--");
        args.Add(worktreePath);
        var commitish = createBranch ? startPoint : branch;
        if (!string.IsNullOrWhiteSpace(commitish))
        {
            args.Add(commitish);
        }

        return await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
    }

    public async Task<ProcessResult> RemoveWorktreeAsync(string path, string worktreePath, bool force, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken,
            force ? ["worktree", "remove", "--force", "--", worktreePath] : ["worktree", "remove", "--", worktreePath]).ConfigureAwait(false);

    public async Task<ProcessResult> PruneWorktreesAsync(string path, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "worktree", "prune").ConfigureAwait(false);

    public async Task<ProcessResult> CreateTagAsync(string path, string name, string? revision, string? message, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "tag" };
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("-a");
            args.Add(name);
            args.Add("-m");
            args.Add(message);
        }
        else
        {
            args.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(revision))
        {
            args.Add(revision);
        }

        return await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
    }

    public async Task<ProcessResult> DeleteTagAsync(string path, string name, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "tag", "-d", name).ConfigureAwait(false);

    public async Task<ProcessResult> FetchAsync(string path, string? remote = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "fetch", "--prune" };
        if (!string.IsNullOrWhiteSpace(remote))
        {
            args.Add(remote);
        }

        return await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
    }

    /// <param name="rebase">
    /// true: rebase; false: merge; null: follow the user's pull.rebase setting. When
    /// that is unset (stock git outside Git for Windows), git >= 2.33 refuses to pull
    /// divergent branches at all, so the default is then an explicit merge.
    /// </param>
    public async Task<ProcessResult> PullAsync(string path, bool? rebase = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "pull" };
        if (rebase is null)
        {
            ProcessResult configured = await GitAsync(path, cancellationToken, "config", "--get", "pull.rebase").ConfigureAwait(false);
            if (!configured.Success || string.IsNullOrWhiteSpace(configured.Output))
            {
                args.Add("--no-rebase");
            }
        }
        else
        {
            args.Add(rebase.Value ? "--rebase" : "--no-rebase");
        }

        return await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
    }

    public async Task<ProcessResult> PushAsync(string path, string? remote = null, string? branch = null,
        bool setUpstream = false, bool force = false, string? detachedTarget = null,
        CancellationToken cancellationToken = default)
    {
        var remoteWasSpecified = !string.IsNullOrWhiteSpace(remote);
        var remoteName = remote;
        var hasUpstream = false;
        string? upstreamBranch = null;
        string? configuredRemote = null;
        string? pushRemote = null;

        if (string.IsNullOrWhiteSpace(branch) && string.IsNullOrWhiteSpace(detachedTarget))
        {
            ProcessResult current = await GitAsync(path, cancellationToken,
                "symbolic-ref", "--short", "-q", "HEAD").ConfigureAwait(false);
            if (current.Success && !string.IsNullOrWhiteSpace(current.Output))
            {
                branch = current.Output.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(branch))
        {
            ProcessResult configured = await GitAsync(path, cancellationToken,
                "config", "--get", $"branch.{branch}.remote").ConfigureAwait(false);
            if (configured.Success && !string.IsNullOrWhiteSpace(configured.Output))
            {
                hasUpstream = true;
                configuredRemote = configured.Output.Trim();
            }

            ProcessResult merge = await GitAsync(path, cancellationToken,
                "config", "--get", $"branch.{branch}.merge").ConfigureAwait(false);
            if (merge.Success && !string.IsNullOrWhiteSpace(merge.Output))
            {
                upstreamBranch = merge.Output.Trim();
                const string heads = "refs/heads/";
                if (upstreamBranch.StartsWith(heads, StringComparison.Ordinal))
                {
                    upstreamBranch = upstreamBranch[heads.Length..];
                }
            }

            ProcessResult configuredPushRemote = await GitAsync(path, cancellationToken,
                "config", "--get", $"branch.{branch}.pushRemote").ConfigureAwait(false);
            if (configuredPushRemote.Success && !string.IsNullOrWhiteSpace(configuredPushRemote.Output))
            {
                pushRemote = configuredPushRemote.Output.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(remoteName))
        {
            // Git precedence: branch.<name>.pushRemote, remote.pushDefault,
            // then branch.<name>.remote. pushRemote must win even when the
            // branch is already tracked.
            ProcessResult pushDefault = await GitAsync(path, cancellationToken,
                "config", "--get", "remote.pushDefault").ConfigureAwait(false);
            var defaultRemote = pushDefault.Success && !string.IsNullOrWhiteSpace(pushDefault.Output)
                ? pushDefault.Output.Trim()
                : null;

            remoteName = pushRemote ?? defaultRemote ?? configuredRemote;

            if (string.IsNullOrWhiteSpace(remoteName))
            {
                IReadOnlyDictionary<string, string> remotes = await GetRemotesAsync(path, cancellationToken).ConfigureAwait(false);
                if (remotes.Count == 0)
                {
                    return new ProcessResult(-1, string.Empty, GitStrings.Push_NoRemote);
                }

                remoteName = remotes.ContainsKey("origin") ? "origin" : remotes.Keys.First();
            }
        }

        if (string.IsNullOrWhiteSpace(branch) && string.IsNullOrWhiteSpace(detachedTarget))
        {
            return new ProcessResult(-1, string.Empty,
                GitStrings.Push_DetachedWithoutTarget);
        }

        if (!hasUpstream && !remoteWasSpecified && !string.IsNullOrWhiteSpace(branch))
        {
            setUpstream = true;
        }

        var refspec = string.IsNullOrWhiteSpace(branch)
            ? $"HEAD:refs/heads/{detachedTarget}"
            : (!string.IsNullOrWhiteSpace(upstreamBranch) && upstreamBranch != branch
                ? $"{branch}:{upstreamBranch}"
                : branch);

        // --porcelain: per-ref results on stdout, which PushOutcome parses to offer the right recovery.
        var args = new List<string> { "push", "--porcelain" };
        if (setUpstream && !string.IsNullOrWhiteSpace(branch))
        {
            args.Add("--set-upstream");
        }

        if (force)
        {
            args.Add("--force-with-lease");
        }

        args.Add(remoteName);
        args.Add(refspec);
        return await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
    }

    public async Task<ProcessResult> StashSaveAsync(string path, string message, bool includeUntracked, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "stash", "push" };
        if (includeUntracked)
        {
            args.Add("--include-untracked");
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("-m");
            args.Add(message);
        }

        return await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
    }

    public async Task<ProcessResult> StashApplyAsync(string path, string name, bool pop, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "stash", pop ? "pop" : "apply", name).ConfigureAwait(false);

    public async Task<ProcessResult> StashDropAsync(string path, string name, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "stash", "drop", name).ConfigureAwait(false);

    public async Task<ProcessResult> MergeAsync(string path, string revision, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "merge", revision).ConfigureAwait(false);

    public async Task<ProcessResult> RebaseAsync(string path, string onto, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "rebase", onto).ConfigureAwait(false);

    public async Task<ProcessResult> CherryPickAsync(string path, string revision, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "cherry-pick", revision).ConfigureAwait(false);

    public async Task<ProcessResult> RevertAsync(string path, string revision, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "revert", "--no-edit", revision).ConfigureAwait(false);

    public async Task<string?> GetCommitTemplateAsync(string path, CancellationToken cancellationToken = default)
    {
        ProcessResult config = await GitAsync(path, cancellationToken, "config", "--get", "commit.template").ConfigureAwait(false);
        if (!config.Success || string.IsNullOrWhiteSpace(config.Output))
        {
            return null;
        }

        var template = config.Output.Trim();
        if (template.StartsWith("~/", StringComparison.Ordinal))
        {
            template = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                template[2..]);
        }
        else if (!System.IO.Path.IsPathRooted(template))
        {
            template = System.IO.Path.Combine(path, template);
        }

        try
        {
            return File.Exists(template) ? await File.ReadAllTextAsync(template, cancellationToken).ConfigureAwait(false) : null;
        }
        catch
        {
            return null;
        }
    }

    public Task<ProcessResult> CherryPickContinueAsync(string path, CancellationToken cancellationToken = default)
        => GitAsync(path, cancellationToken, "cherry-pick", "--continue");
    public Task<ProcessResult> CherryPickAbortAsync(string path, CancellationToken cancellationToken = default)
        => GitAsync(path, cancellationToken, "cherry-pick", "--abort");
    public Task<ProcessResult> CherryPickSkipAsync(string path, CancellationToken cancellationToken = default)
        => GitAsync(path, cancellationToken, "cherry-pick", "--skip");
    public Task<ProcessResult> RevertContinueAsync(string path, CancellationToken cancellationToken = default)
        => GitAsync(path, cancellationToken, "revert", "--continue");
    public Task<ProcessResult> RevertAbortAsync(string path, CancellationToken cancellationToken = default)
        => GitAsync(path, cancellationToken, "revert", "--abort");

    public async Task<ProcessResult> ResetAsync(string path, string revision, bool hard, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "reset", hard ? "--hard" : "--mixed", revision).ConfigureAwait(false);

    public async Task<ProcessResult> AbortMergeAsync(string path, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "merge", "--abort").ConfigureAwait(false);

    public async Task<ProcessResult> AbortRebaseAsync(string path, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await GitAsync(path, cancellationToken, "rebase", "--abort").ConfigureAwait(false);
        ClearRebaseData(path);
        return result;
    }

    public async Task<ProcessResult> ContinueRebaseAsync(string path, CancellationToken cancellationToken = default)
    {
        var dataDir = RebaseDataDir(path);
        var messageDir = Path.Combine(dataDir, "messages");
        var messageEditor = Path.Combine(dataDir, OperatingSystem.IsWindows() ? "message-editor.cmd" : "message-editor.sh");
        Dictionary<string, string>? environment = null;
        if (File.Exists(messageEditor))
        {
            environment = new Dictionary<string, string>
            {
                ["AVGIT_MSG_DIR"] = messageDir,
                ["AVGIT_GIT_DIR"] = GetGitDir(path),
                ["GIT_EDITOR"] = '"' + messageEditor + '"'
            };
        }

        ProcessResult result = environment is null
            ? await GitAsync(path, cancellationToken, "rebase", "--continue").ConfigureAwait(false)
            : await GitWithEnvAsync(path, ["rebase", "--continue"], environment, cancellationToken).ConfigureAwait(false);

        if (!IsRebaseInProgressAt(path))
        {
            ClearRebaseData(path);
        }

        return result;
    }

    public async Task<ProcessResult> SkipRebaseCommitAsync(string path, CancellationToken cancellationToken = default)
    {
        var dataDir = RebaseDataDir(path);
        var messageDir = Path.Combine(dataDir, "messages");
        var messageEditor = Path.Combine(dataDir, OperatingSystem.IsWindows() ? "message-editor.cmd" : "message-editor.sh");
        Dictionary<string, string>? environment = null;
        if (File.Exists(messageEditor))
        {
            environment = new Dictionary<string, string>
            {
                ["AVGIT_MSG_DIR"] = messageDir,
                ["AVGIT_GIT_DIR"] = GetGitDir(path),
                ["GIT_EDITOR"] = '"' + messageEditor + '"'
            };
        }

        ProcessResult result = environment is null
            ? await GitAsync(path, cancellationToken, "rebase", "--skip").ConfigureAwait(false)
            : await GitWithEnvAsync(path, ["rebase", "--skip"], environment, cancellationToken).ConfigureAwait(false);

        if (!IsRebaseInProgressAt(path))
        {
            ClearRebaseData(path);
        }

        return result;
    }

    public async Task<string> GetInteractiveRebaseTodoAsync(string path, string baseRevision,
        bool fromRoot = false, CancellationToken cancellationToken = default)
    {
        var range = fromRoot ? "HEAD" : baseRevision + "..HEAD";
        ProcessResult merges = await GitAsync(path, cancellationToken,
            "rev-list", "--merges", range).ConfigureAwait(false);
        if (!merges.Success)
        {
            ReportFailure("git rev-list", merges);
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(merges.Output))
        {
            throw new InvalidOperationException(
                GitStrings.Rebase_MergesNotSupported);
        }

        // Record format: full-sha US short-sha US full-message RS. Full messages
        // preserve bodies; full SHAs key reword overrides across replay.
        ProcessResult result = await GitAsync(path, cancellationToken,
            "log", "--reverse", "--format=%H%x1f%h%x1f%B%x1e", range).ConfigureAwait(false);
        if (!result.Success)
        {
            ReportFailure("git log", result);
            return string.Empty;
        }

        return result.Output.TrimEnd('\r', '\n');
    }

    public async Task<ProcessResult> InteractiveRebaseAsync(string path, string baseRevision, string todo,
        IReadOnlyDictionary<string, string>? rewordMessages = null, bool fromRoot = false,
        CancellationToken cancellationToken = default)
    {
        ClearRebaseData(path);
        var dataDir = RebaseDataDir(path);
        Directory.CreateDirectory(dataDir);
        var todoPath = Path.Combine(dataDir, "todo.txt");
        var isWindows = OperatingSystem.IsWindows();
        var editorPath = Path.Combine(dataDir, isWindows ? "sequence-editor.cmd" : "sequence-editor.sh");
        var messageEditorPath = Path.Combine(dataDir, isWindows ? "message-editor.cmd" : "message-editor.sh");
        var messageDir = Path.Combine(dataDir, "messages");
        Directory.CreateDirectory(messageDir);
        var keepForContinuation = false;

        try
        {
            await File.WriteAllTextAsync(todoPath, todo, cancellationToken).ConfigureAwait(false);
            if (rewordMessages is not null)
            {
                foreach (KeyValuePair<string, string> pair in rewordMessages)
                {
                    if (!string.IsNullOrEmpty(pair.Value))
                    {
                        await File.WriteAllTextAsync(Path.Combine(messageDir, "msg-" + pair.Key + ".txt"),
                            pair.Value, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (isWindows)
            {
                await File.WriteAllTextAsync(editorPath,
                    "@echo off\r\ncopy /y \"%AVGIT_TODO%\" \"%~1\" >nul\r\n", cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(messageEditorPath,
                    "@echo off\r\n" +
                    "set \"LAST=\"\r\n" +
                    "for /f \"usebackq delims=\" %%l in (\"%AVGIT_GIT_DIR%\\rebase-merge\\done\") do set \"LAST=%%l\"\r\n" +
                    "for /f \"tokens=1,2\" %%a in (\"%LAST%\") do if /i \"%%a\"==\"reword\" if exist \"%AVGIT_MSG_DIR%\\msg-%%b.txt\" copy /y \"%AVGIT_MSG_DIR%\\msg-%%b.txt\" \"%~1\" >nul\r\n" +
                    "exit /b 0\r\n", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(editorPath,
                    "#!/bin/sh\ncp \"$AVGIT_TODO\" \"$1\"\n", cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(messageEditorPath,
                    "#!/bin/sh\nfile=\"$1\"\n" +
                    "last=$(tail -n 1 \"$AVGIT_GIT_DIR/rebase-merge/done\" 2>/dev/null)\n" +
                    "set -- $last\n" +
                    "[ \"$1\" = \"reword\" ] || exit 0\n" +
                    "sha=$2\n" +
                    "if [ -f \"$AVGIT_MSG_DIR/msg-$sha.txt\" ]; then cp \"$AVGIT_MSG_DIR/msg-$sha.txt\" \"$file\"; fi\n" +
                    "exit 0\n", cancellationToken).ConfigureAwait(false);
                UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                           | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                           | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(editorPath, mode);
                File.SetUnixFileMode(messageEditorPath, mode);
            }

            var environment = new Dictionary<string, string>
            {
                ["AVGIT_TODO"] = todoPath,
                ["AVGIT_MSG_DIR"] = messageDir,
                ["AVGIT_GIT_DIR"] = GetGitDir(path),
                ["GIT_SEQUENCE_EDITOR"] = '"' + editorPath + '"',
                ["GIT_EDITOR"] = '"' + messageEditorPath + '"'
            };

            IReadOnlyList<string> rebaseArgs = fromRoot
                ? ["rebase", "-i", "--root"]
                : ["rebase", "-i", baseRevision];
            ProcessResult result = await GitWithEnvAsync(path, rebaseArgs, environment, cancellationToken)
                .ConfigureAwait(false);

            if (IsRebaseInProgressAt(path))
            {
                keepForContinuation = true;
            }

            return result;
        }
        finally
        {
            if (!keepForContinuation)
            {
                ClearRebaseData(path);
            }
        }
    }

    private static bool IsRebaseInProgressAt(string path)
        => Directory.Exists(Path.Combine(GetGitDir(path), "rebase-merge"))
           || Directory.Exists(Path.Combine(GetGitDir(path), "rebase-apply"));

    private static string RebaseDataDir(string path) => Path.Combine(GetGitDir(path), "avgit-rebase");

    private static void ClearRebaseData(string path)
    {
        try
        {
            var dir = RebaseDataDir(path);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch
        {
            // Best effort; a later rebase recreates the directory.
        }
    }
    public async Task<ProcessResult> InitAsync(string path, CancellationToken cancellationToken = default)
        => await GitAsync(path, cancellationToken, "init").ConfigureAwait(false);

    public async Task<ProcessResult> CloneAsync(string url, string destination, bool partial = false,
        CancellationToken cancellationToken = default)
        => await CloneAsync(url, destination, partial, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Clones with additional <c>git clone</c> options (e.g. <c>--depth 1</c>, <c>--branch dev</c>).</summary>
    public async Task<ProcessResult> CloneAsync(string url, string destination, bool partial,
        IReadOnlyList<string>? extraOptions, CancellationToken cancellationToken = default)
    {
        // A trailing separator would make GetFileName return "" and clone into the parent.
        var fullDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var parent = Path.GetDirectoryName(fullDestination);
        var folder = Path.GetFileName(fullDestination);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            return new ProcessResult(-1, string.Empty, GitStrings.Clone_ParentMissing);
        }

        if (string.IsNullOrEmpty(folder))
        {
            return new ProcessResult(-1, string.Empty, GitStrings.Clone_FolderNameEmpty);
        }

        var args = new List<string> { "-c", "core.quotepath=false" };
        AddCredentialHelper(args);
        args.Add("clone");
        if (partial)
        {
            args.Add("--filter=blob:none");
        }

        if (extraOptions is not null)
        {
            args.AddRange(extraOptions.Where(o => !string.IsNullOrWhiteSpace(o)));
        }

        args.Add("--");
        args.Add(url);
        args.Add(folder);

        return await ProcessRunner.RunAsync(ExecutablePath, parent, args, null, BuildEnvironment(null), cancellationToken)
            .ConfigureAwait(false);
    }

    public static string GetGitDir(string path)
    {
        var dotGit = Path.Combine(path, ".git");
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (File.Exists(dotGit))
        {
            try
            {
                var text = File.ReadAllText(dotGit).Trim();
                const string prefix = "gitdir:";
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(Path.Combine(path, text[prefix.Length..].Trim()));
                }
            }
            catch
            {
                // Fall through to the conventional location.
            }
        }

        return dotGit;
    }
}




