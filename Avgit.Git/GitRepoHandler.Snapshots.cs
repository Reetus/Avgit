using System.Collections.Concurrent;
using System.Globalization;
using Avgit.Core.Model;

namespace Avgit.Git;

/// <summary>Working-copy state from a single <c>git status</c> run.</summary>
/// <param name="Staged">Index vs HEAD, one record per path with a change in git's X column.</param>
/// <param name="Unstaged">Work tree vs index: Y-column changes, conflicts, untracked and (on request) ignored paths.</param>
/// <param name="Summary">Branch, upstream, in-progress operations and counts.</param>
/// <param name="Error">Why git status failed, or null.</param>
public sealed record StatusSnapshot(
    IReadOnlyList<ChangedFile> Staged,
    IReadOnlyList<ChangedFile> Unstaged,
    RepoSummary Summary,
    string? Error)
{
    /// <summary>The branch name, or <see cref="RepoSummary.DetachedHeadLabel"/>.</summary>
    public string Branch => Summary.BranchLabel;

    /// <summary>The HEAD commit id, or empty in a repository with no commits.</summary>
    public string Revision => Summary.HeadId ?? string.Empty;
}

/// <summary>A commit's details and changed files (against its first parent).</summary>
public sealed record CommitSnapshot(CommitRecord Details, IReadOnlyList<ChangedFile> Files);

public sealed record CommitResult(CommitSnapshot? Commit, string? Error);

/// <summary>Branches, tags, remotes, stashes and worktrees loaded together.</summary>
public sealed record RefsSnapshot(
    IReadOnlyList<BranchRef> Branches,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Remotes,
    IReadOnlyList<StashRecord> Stashes,
    IReadOnlyList<WorktreeInfo> Worktrees,
    string? Error);

public sealed record LogPage(IReadOnlyList<HistoryRow> Rows, string? Error);

/// <summary>
/// Batched reads used by the UI. Each returns its own error instead of using
/// the shared <see cref="LastError"/> slot, so loads can run concurrently, and
/// each issues as few git processes as possible: process start-up costs tens
/// of milliseconds on Windows, more than most of these commands take to run.
/// </summary>
public sealed partial class GitRepoHandler
{
    private const char GroupSeparator = '\x1d';

    // No %(refname:short): git checks every short name for ambiguity, which took
    // ~0.5 s for 1,000 tags on git.git; prefixes are stripped in ParseBranches.
    private const string BranchRefFormat = "%(refname)\t%(objectname)\t%(HEAD)\t%(upstream:short)\t%(upstream:track)";

    // Adds the tip commit's date, author and subject, for "Trim branches" only:
    // reading every tip object more than doubled the time of the refs query on
    // each refresh (200 ms to 460 ms with 2,000 annotated tags). The subject is
    // last because it is the only field that may contain a tab.
    private const string BranchTipFormat = BranchRefFormat + "\t%(committerdate:unix)\t%(authorname)\t%(contents:subject)";

    // Work-tree root -> absolute git dir, from `git rev-parse --absolute-git-dir`.
    // Resolved once per repository so later status runs stay one process.
    private readonly ConcurrentDictionary<string, string> _gitDirs = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>all</c> lists every file inside untracked folders; <c>normal</c> lists an
    /// untracked folder as one entry, which is much cheaper for large folders.
    /// </summary>
    public string UntrackedFilesMode { get; set; } = "all";

    public async Task<StatusSnapshot> GetStatusSnapshotAsync(string path, PathState mask = PathState.WorkingCopyMask,
        CancellationToken cancellationToken = default)
    {
        // --no-optional-locks: a background refresh must not take index.lock
        // (which can make the user's own git commands fail) or rewrite the index
        // (which would trigger the file watcher again).
        // --ignore-submodules=untracked still reports modified submodules but
        // skips scanning their untracked files.
        var args = new List<string>(8)
        {
            "--no-optional-locks", "status", "--porcelain=v2", "--branch", "-z",
            "--untracked-files=" + (UntrackedFilesMode == "normal" ? "normal" : "all"),
            "--ignore-submodules=untracked"
        };
        if ((mask & PathState.Excluded) != 0)
        {
            args.Add("--ignored=matching");
        }

        Task<string> gitDirTask = ResolveGitDirAsync(path, cancellationToken);
        ProcessResult result = await GitAsync(path, cancellationToken, args).ConfigureAwait(false);
        var gitDir = await gitDirTask.ConfigureAwait(false);

        if (!result.Success)
        {
            var failed = new RepoSummary { Operations = DetectOperations(gitDir), IsClean = false };
            return new StatusSnapshot([], [], failed,
                result.Cancelled ? null : DescribeFailure("git status", result));
        }

        return ParseStatus(path, result.Output, mask, DetectOperations(gitDir));
    }

    private async Task<string> ResolveGitDirAsync(string path, CancellationToken cancellationToken)
    {
        if (_gitDirs.TryGetValue(path, out var cached))
        {
            return cached;
        }

        ProcessResult result = await GitAsync(path, cancellationToken, "rev-parse", "--absolute-git-dir").ConfigureAwait(false);
        var resolved = result.Success ? result.Output.Trim() : string.Empty;
        if (resolved.Length == 0)
        {
            // Not cached: the repository may not exist yet, or git failed to start.
            return GetGitDir(path);
        }

        _gitDirs[path] = resolved;
        return resolved;
    }

    /// <summary>
    /// Parses <c>git status --porcelain=v2 --branch -z</c> output. Paths are
    /// taken verbatim (<c>-z</c> disables quoting). Entries whose state is not in
    /// <paramref name="mask"/> are left out of both lists and the counts.
    /// </summary>
    /// <param name="repoPath">Work-tree root, used to build each record's full path.</param>
    /// <param name="output">The raw NUL-separated output.</param>
    /// <param name="mask">Which states to return.</param>
    /// <param name="operations">In-progress operations, from <see cref="DetectOperations"/>.</param>
    public static StatusSnapshot ParseStatus(string repoPath, string output, PathState mask,
        RepoOperations operations = RepoOperations.None)
    {
        var parser = new StatusParser(repoPath, mask);
        var pos = 0;
        while (pos < output.Length)
        {
            var end = output.IndexOf('\0', pos);
            if (end < 0)
            {
                end = output.Length;
            }

            var start = pos;
            pos = end + 1;
            ReadOnlySpan<char> entry = output.AsSpan(start, end - start);
            if (entry.Length < 2)
            {
                continue;
            }

            switch (entry[0])
            {
                case '#':
                    parser.Header(entry);
                    break;

                case '1':
                    {
                        // 1 XY sub mH mI mW hH hI path
                        var offset = FieldOffset(entry, 8);
                        if (offset > 0 && entry.Length >= 9)
                        {
                            parser.Changed(entry[2], entry[3], entry.Slice(5, 4), output.Substring(start + offset, end - start - offset), null);
                        }

                        break;
                    }

                case '2':
                    {
                        // 2 XY sub mH mI mW hH hI Xscore path, then the original path as the next token.
                        string? original = null;
                        if (pos <= output.Length)
                        {
                            var originalEnd = output.IndexOf('\0', pos);
                            if (originalEnd < 0)
                            {
                                originalEnd = output.Length;
                            }

                            original = output[pos..originalEnd];
                            pos = originalEnd + 1;
                        }

                        var offset = FieldOffset(entry, 9);
                        if (offset > 0 && entry.Length >= 9)
                        {
                            parser.Changed(entry[2], entry[3], entry.Slice(5, 4), output.Substring(start + offset, end - start - offset), original);
                        }

                        break;
                    }

                case 'u':
                    {
                        // u XY sub m1 m2 m3 mW h1 h2 h3 path
                        var offset = FieldOffset(entry, 10);
                        if (offset > 0)
                        {
                            parser.Unmerged(output.Substring(start + offset, end - start - offset));
                        }

                        break;
                    }

                case '?':
                    if (entry.Length > 2)
                    {
                        parser.Other(PathState.Untracked, output.Substring(start + 2, end - start - 2));
                    }

                    break;

                case '!':
                    if (entry.Length > 2)
                    {
                        parser.Other(PathState.Excluded, output.Substring(start + 2, end - start - 2));
                    }

                    break;
            }
        }

        return parser.Finish(operations);
    }

    /// <summary>Index just past the <paramref name="count"/>-th space, or -1 when there are fewer.</summary>
    private static int FieldOffset(ReadOnlySpan<char> entry, int count)
    {
        var index = 0;
        for (var i = 0; i < count; i++)
        {
            var space = entry[index..].IndexOf(' ');
            if (space < 0)
            {
                return -1;
            }

            index += space + 1;
        }

        return index < entry.Length ? index : -1;
    }

    /// <summary>
    /// Operations left in progress, from marker files in the git dir (which for a
    /// linked worktree is that worktree's own directory under the main .git).
    /// </summary>
    public static RepoOperations DetectOperations(string gitDir)
    {
        var operations = RepoOperations.None;
        if (string.IsNullOrEmpty(gitDir) || !Directory.Exists(gitDir))
        {
            return operations;
        }

        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
        {
            operations |= RepoOperations.Merge;
        }

        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")))
        {
            operations |= RepoOperations.Rebase;
        }

        var applyDir = Path.Combine(gitDir, "rebase-apply");
        if (Directory.Exists(applyDir))
        {
            // rebase-apply/ is shared by `git am` and the apply backend of rebase;
            // am marks its runs with an "applying" file.
            operations |= File.Exists(Path.Combine(applyDir, "applying"))
                ? RepoOperations.ApplyMailbox
                : RepoOperations.Rebase;
        }

        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
        {
            operations |= RepoOperations.CherryPick;
        }

        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
        {
            operations |= RepoOperations.Revert;
        }

        if (File.Exists(Path.Combine(gitDir, "BISECT_LOG")))
        {
            operations |= RepoOperations.Bisect;
        }

        return operations;
    }

    /// <summary>Accumulates the two lists, the branch headers and the counts during one parse.</summary>
    private sealed class StatusParser(string repoPath, PathState mask)
    {
        private readonly List<ChangedFile> _staged = [];
        private readonly List<ChangedFile> _unstaged = [];
        private string? _branch;
        private string? _head;
        private string? _upstream;
        private int _ahead;
        private int _behind;
        private int _modified;
        private int _added;
        private int _removed;
        private int _renamed;
        private int _copied;
        private int _conflicted;
        private int _untracked;

        public void Header(ReadOnlySpan<char> entry)
        {
            if (TryValue(entry, "# branch.oid ", out ReadOnlySpan<char> oid))
            {
                _head = oid.SequenceEqual("(initial)") ? null : oid.ToString();
            }
            else if (TryValue(entry, "# branch.head ", out ReadOnlySpan<char> head))
            {
                _branch = head.SequenceEqual("(detached)") ? null : head.ToString();
            }
            else if (TryValue(entry, "# branch.upstream ", out ReadOnlySpan<char> upstream))
            {
                _upstream = upstream.ToString();
            }
            else if (TryValue(entry, "# branch.ab ", out ReadOnlySpan<char> ab))
            {
                // "+A -B"
                var space = ab.IndexOf(' ');
                if (space > 1 && ab[0] == '+' && space + 2 < ab.Length && ab[space + 1] == '-')
                {
                    _ = int.TryParse(ab[1..space], NumberStyles.None, CultureInfo.InvariantCulture, out _ahead);
                    _ = int.TryParse(ab[(space + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out _behind);
                }
            }
        }

        private static bool TryValue(ReadOnlySpan<char> entry, string prefix, out ReadOnlySpan<char> value)
        {
            if (entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                value = entry[prefix.Length..];
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>An ordinary or rename/copy entry.</summary>
        public void Changed(char x, char y, ReadOnlySpan<char> submodule, string path, string? originalPath)
        {
            if (IsUnmerged(x, y))
            {
                Unmerged(path);
                return;
            }

            // Submodule field: "N..." for a plain file, else "S<c><m><u>" where
            // c = 'C' when the checked-out commit differs from the index.
            var isSubmodule = submodule.Length == 4 && submodule[0] == 'S';
            PathState index = FromLetter(x);
            PathState workTree = FromLetter(y);

            // A gitlink can only change its commit in the index.
            if (isSubmodule && index == PathState.Edited)
            {
                index = PathState.SubmoduleCommitChanged;
            }

            if (isSubmodule && workTree == PathState.Edited && submodule[1] == 'C')
            {
                workTree = PathState.SubmoduleCommitChanged;
            }

            PathState counted = PathState.None;
            if (index is not (PathState.Unchanged or PathState.None) && (mask & index) != 0)
            {
                _staged.Add(CreateRecord(repoPath, path, OldPathFor(index, originalPath), ChangeArea.Index,
                    index, index, workTree));
                counted = index;
            }

            if (workTree is not (PathState.Unchanged or PathState.None) && (mask & workTree) != 0)
            {
                _unstaged.Add(CreateRecord(repoPath, path, OldPathFor(workTree, originalPath), ChangeArea.WorkTree,
                    workTree, index, workTree));
                if (counted == PathState.None)
                {
                    counted = workTree;
                }
            }

            Count(counted);
        }

        /// <summary>
        /// A conflicted path. It goes only in the unstaged list: "unstage" runs
        /// <c>git reset</c>, which would silently resolve it to one side.
        /// </summary>
        public void Unmerged(string path)
        {
            if ((mask & PathState.Conflicted) == 0)
            {
                return;
            }

            _unstaged.Add(CreateRecord(repoPath, path, null, ChangeArea.WorkTree,
                PathState.Conflicted, PathState.Conflicted, PathState.Conflicted));
            Count(PathState.Conflicted);
        }

        /// <summary>An untracked or ignored path.</summary>
        public void Other(PathState state, string path)
        {
            if ((mask & state) == 0)
            {
                return;
            }

            _unstaged.Add(CreateRecord(repoPath, path, null, ChangeArea.WorkTree, state, PathState.Unchanged, state));
            Count(state);
        }

        private void Count(PathState state)
        {
            switch (state)
            {
                case PathState.Edited or PathState.SubmoduleCommitChanged:
                    _modified++;
                    break;
                case PathState.New:
                    _added++;
                    break;
                case PathState.Removed:
                    _removed++;
                    break;
                case PathState.Moved:
                    _renamed++;
                    break;
                case PathState.Duplicated:
                    _copied++;
                    break;
                case PathState.Conflicted:
                    _conflicted++;
                    break;
                case PathState.Untracked:
                    _untracked++;
                    break;
            }
        }

        public StatusSnapshot Finish(RepoOperations operations)
        {
            var summary = new RepoSummary
            {
                Branch = _branch,
                HeadId = _head,
                Upstream = _upstream,
                Ahead = _ahead,
                Behind = _behind,
                Operations = operations,
                Modified = _modified,
                Added = _added,
                Removed = _removed,
                Renamed = _renamed,
                Copied = _copied,
                Conflicted = _conflicted,
                Untracked = _untracked,
                IsClean = _modified + _added + _removed + _renamed + _copied + _conflicted + _untracked == 0
            };
            return new StatusSnapshot(_staged, _unstaged, summary, null);
        }

        private static string? OldPathFor(PathState state, string? originalPath)
            => state is PathState.Moved or PathState.Duplicated ? originalPath : null;

        // The unmerged combinations listed in `git help status`: DD AU UD UA DU AA UU.
        private static bool IsUnmerged(char x, char y)
            => x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D');

        private static PathState FromLetter(char letter) => letter switch
        {
            '.' => PathState.Unchanged,
            'M' or 'T' => PathState.Edited,
            'A' => PathState.New,
            'D' => PathState.Removed,
            'R' => PathState.Moved,
            'C' => PathState.Duplicated,
            _ => PathState.None
        };
    }

    /// <summary>
    /// Details, refs and changed files of one commit in a single git process
    /// (previously show + for-each-ref + rev-list + diff). Merges are compared
    /// with their first parent.
    /// </summary>
    public async Task<CommitResult> GetCommitAsync(string path, string revision, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return new CommitResult(null, null);
        }

        const string format = "%H%x1e%h%x1e%P%x1e%an%x1e%ae%x1e%cn%x1e%ce%x1e%aI%x1e%cI%x1e%D%x1e%B%x1d";
        ProcessResult result = await GitAsync(path, cancellationToken,
            "log", "-1", "-z", "-m", "--first-parent", "--decorate=full", "--format=" + format,
            "--name-status", "-M", "-C", revision, "--").ConfigureAwait(false);
        if (!result.Success)
        {
            return new CommitResult(null, result.Cancelled ? null : DescribeFailure("git log", result));
        }

        var output = result.Output;
        var end = output.IndexOf(GroupSeparator);
        if (end < 0)
        {
            return new CommitResult(null, string.Format(GitStrings.Commit_UnexpectedOutput, revision));
        }

        var fields = output[..end].Split(UnitSeparator);
        if (fields.Length < 11)
        {
            return new CommitResult(null, string.Format(GitStrings.Commit_UnexpectedOutput, revision));
        }

        DateTimeOffset.TryParse(fields[7], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset authorDate);
        DateTimeOffset.TryParse(fields[8], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset commitDate);
        // The body could in theory contain the field separator; rejoin it.
        var message = string.Join(UnitSeparator, fields.Skip(10)).TrimEnd('\r', '\n');
        var tags = ParseTagDecorations(fields[9]);

        var details = new CommitRecord
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
            Tags = tags
        };

        return new CommitResult(new CommitSnapshot(details, ParseNameStatus(path, output[(end + 1)..])), null);
    }

    /// <summary>Branches and tags from one for-each-ref, with remotes and stashes in parallel.</summary>
    public async Task<RefsSnapshot> GetRefsSnapshotAsync(string path, CancellationToken cancellationToken = default)
    {
        Task<ProcessResult> refsTask = GitAsync(path, cancellationToken, "for-each-ref", "--format=" + BranchRefFormat,
            "refs/heads", "refs/remotes", "refs/tags");
        Task<ProcessResult> remotesTask = GitAsync(path, cancellationToken, "remote", "-v");
        Task<ProcessResult> stashesTask = GitAsync(path, cancellationToken, "stash", "list", StashListFormat);
        Task<ProcessResult> worktreesTask = GitAsync(path, cancellationToken, "worktree", "list", "--porcelain");
        await Task.WhenAll(refsTask, remotesTask, stashesTask, worktreesTask).ConfigureAwait(false);

        ProcessResult refs = refsTask.Result;
        ProcessResult remotes = remotesTask.Result;
        ProcessResult stashes = stashesTask.Result;
        ProcessResult worktreeList = worktreesTask.Result;
        var error = FirstFailure(("git for-each-ref", refs), ("git remote", remotes), ("git stash", stashes),
            ("git worktree list", worktreeList));

        var tags = new List<string>();
        if (refs.Success)
        {
            foreach (var line in refs.Output.Split('\n'))
            {
                var refName = line.Split('\t', 2)[0];
                if (refName.StartsWith("refs/tags/", StringComparison.Ordinal))
                {
                    tags.Add(refName["refs/tags/".Length..]);
                }
            }
        }

        List<BranchRef> branches = refs.Success ? ParseBranches(refs.Output) : [];
        IReadOnlyList<WorktreeInfo> worktrees = worktreeList.Success ? ParseWorktrees(worktreeList.Output) : [];
        foreach (WorktreeInfo worktree in worktrees)
        {
            if (worktree.BranchName.Length > 0
                && branches.FirstOrDefault(b => b.Kind == RefKind.Branch && b.Name == worktree.BranchName) is { } branch)
            {
                branch.WorktreePath = worktree.Path;
            }
        }

        return new RefsSnapshot(
            branches,
            tags,
            remotes.Success ? ParseRemotes(remotes.Output) : new Dictionary<string, string>(StringComparer.Ordinal),
            stashes.Success ? ParseStashes(stashes.Output) : [],
            worktrees,
            error);
    }

    /// <summary>
    /// Parses <c>git worktree list --porcelain</c>. Paths are returned in the platform's
    /// form (git prints forward slashes on Windows).
    /// </summary>
    public static IReadOnlyList<WorktreeInfo> ParseWorktrees(string output)
    {
        var list = new List<WorktreeInfo>();
        string? path = null;
        var head = string.Empty;
        var branch = string.Empty;
        bool bare = false, detached = false, locked = false, prunable = false;

        void Flush()
        {
            if (path is not null)
            {
                list.Add(new WorktreeInfo(path, head, branch, bare, detached, locked, prunable));
            }

            path = null;
            head = branch = string.Empty;
            bare = detached = locked = prunable = false;
        }

        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length == 0)
            {
                Flush();
            }
            else if (text.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = NormalizeWorktreePath(text["worktree ".Length..]);
            }
            else if (text.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = text["HEAD ".Length..];
            }
            else if (text.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = text["branch ".Length..];
            }
            else if (text == "bare")
            {
                bare = true;
            }
            else if (text == "detached")
            {
                detached = true;
            }
            // "locked" and "prunable" may be followed by a reason.
            else if (text == "locked" || text.StartsWith("locked ", StringComparison.Ordinal))
            {
                locked = true;
            }
            else if (text == "prunable" || text.StartsWith("prunable ", StringComparison.Ordinal))
            {
                prunable = true;
            }
        }

        Flush();
        return list;
    }

    private static string NormalizeWorktreePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>One page of history; the error (if any) is returned rather than stored.</summary>
    public async Task<LogPage> GetLogPageAsync(string path, HistoryFilter query, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await GitAsync(path, cancellationToken, BuildLogArguments(query)).ConfigureAwait(false);
        if (!result.Success)
        {
            // An empty (unborn) repository has no HEAD yet; that is not an error.
            var benign = result.Cancelled
                         || result.Error.Contains("does not have any commits yet", StringComparison.OrdinalIgnoreCase)
                         || result.Error.Contains("unknown revision", StringComparison.OrdinalIgnoreCase)
                         || result.Error.Contains("bad revision", StringComparison.OrdinalIgnoreCase);
            return new LogPage([], benign ? null : DescribeFailure("git log", result));
        }

        return new LogPage(ParseLogRows(result.Output), null);
    }

    /// <summary>
    /// Writes a commit-graph when the repository has none. Without one,
    /// <c>git log --topo-order</c> must walk the entire history before printing
    /// the first commit (about 0.8 s instead of 0.2 s on git.git). This is the
    /// same file <c>git gc</c> and <c>git maintenance</c> maintain.
    /// </summary>
    public async Task<bool> EnsureCommitGraphAsync(string path, CancellationToken cancellationToken = default)
    {
        var objects = Path.Combine(GetCommonGitDir(path), "objects");
        var info = Path.Combine(objects, "info");
        if (!Directory.Exists(objects)
            || File.Exists(Path.Combine(info, "commit-graph"))
            || File.Exists(Path.Combine(info, "commit-graphs", "commit-graph-chain"))
            || File.Exists(Path.Combine(GetCommonGitDir(path), "shallow")))
        {
            return false;
        }

        ProcessResult result = await GitAsync(path, cancellationToken, "commit-graph", "write", "--reachable").ConfigureAwait(false);
        return result.Success;
    }

    /// <summary>The repository's shared .git directory (differs from GetGitDir in a linked worktree).</summary>
    public static string GetCommonGitDir(string path)
    {
        var gitDir = GetGitDir(path);
        try
        {
            var commonDirFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonDirFile))
            {
                return Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDirFile).Trim()));
            }
        }
        catch (IOException)
        {
            // Fall back to the worktree's own git directory.
        }

        return gitDir;
    }

    /// <summary>Tag names from a <c>%D</c> decoration ("HEAD -> refs/heads/main, tag: refs/tags/v1").</summary>
    private static string[] ParseTagDecorations(string decorations)
        => [.. decorations.Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .Where(d => d.StartsWith("tag: ", StringComparison.Ordinal))
            .Select(d => d["tag: ".Length..])
            .Select(d => d.StartsWith("refs/tags/", StringComparison.Ordinal) ? d["refs/tags/".Length..] : d)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)];

    private static string? FirstFailure(params (string Operation, ProcessResult Result)[] results)
    {
        foreach ((var operation, ProcessResult? result) in results)
        {
            if (!result.Success && !result.Cancelled)
            {
                return DescribeFailure(operation, result);
            }
        }

        return null;
    }

    private static string DescribeFailure(string operation, ProcessResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        var firstLine = message.Split('\n').FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(firstLine)
            ? string.Format(GitStrings.Failure_ExitCode, operation, result.ExitCode)
            : string.Format(GitStrings.Failure_Message, operation, firstLine);
    }

    /// <summary>
    /// Parses <c>--name-status -z</c> output: <c>STATUS\0path\0</c>, or
    /// <c>R&lt;score&gt;\0old\0new\0</c> / <c>C&lt;score&gt;\0old\0new\0</c> for renames and
    /// copies. <c>X</c> and letters git may add in future are skipped.
    /// </summary>
    public static List<ChangedFile> ParseNameStatus(string repoPath, string output)
    {
        var list = new List<ChangedFile>();
        var pos = 0;
        while (pos < output.Length)
        {
            // `git log -z` puts a newline between the commit header and the first entry.
            ReadOnlySpan<char> code = NextToken(output, ref pos).Trim("\r\n");
            if (code.IsEmpty)
            {
                continue;
            }

            PathState status = code[0] switch
            {
                'A' => PathState.New,
                'M' or 'T' => PathState.Edited,
                'D' => PathState.Removed,
                'R' => PathState.Moved,
                'C' => PathState.Duplicated,
                'U' => PathState.Conflicted,
                _ => PathState.None
            };

            string? oldPath = null;
            if (code[0] is 'R' or 'C')
            {
                oldPath = NextToken(output, ref pos).ToString();
            }

            var newPath = NextToken(output, ref pos).ToString();
            if (status == PathState.None || newPath.Length == 0)
            {
                continue;
            }

            list.Add(CreateRecord(repoPath, newPath, oldPath, ChangeArea.Commit, status, status, PathState.Unchanged));
        }

        return list;
    }

    private static ReadOnlySpan<char> NextToken(string output, ref int pos)
    {
        if (pos >= output.Length)
        {
            return [];
        }

        var end = output.IndexOf('\0', pos);
        if (end < 0)
        {
            end = output.Length;
        }

        ReadOnlySpan<char> token = output.AsSpan(pos, end - pos);
        pos = end + 1;
        return token;
    }

    /// <summary>
    /// Parses <c>for-each-ref</c> output in <see cref="BranchRefFormat"/>. Tags and
    /// remote HEAD aliases are skipped; the tip commit fields are optional.
    /// </summary>
    public static List<BranchRef> ParseBranches(string output)
    {
        var list = new List<BranchRef>();
        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length == 0)
            {
                continue;
            }

            var parts = text.Split('\t', 8);
            if (parts.Length < 5)
            {
                continue;
            }

            var isRemote = parts[0].StartsWith("refs/remotes/", StringComparison.Ordinal);
            if (!isRemote && !parts[0].StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                continue;
            }

            if (isRemote && parts[0].EndsWith("/HEAD", StringComparison.Ordinal))
            {
                continue;
            }

            var info = new BranchRef
            {
                Name = isRemote ? parts[0]["refs/remotes/".Length..] : parts[0]["refs/heads/".Length..],
                Target = parts[1],
                IsCheckedOut = parts[2] == "*",
                Kind = isRemote ? RefKind.RemoteBranch : RefKind.Branch
            };

            if (parts.Length >= 8)
            {
                if (long.TryParse(parts[5], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds))
                {
                    info.LastCommitDate = DateTimeOffset.FromUnixTimeSeconds(seconds);
                }

                info.LastCommitAuthor = parts[6];
                info.LastCommitSubject = parts[7];
            }

            if (!isRemote && !string.IsNullOrWhiteSpace(parts[3]))
            {
                var slash = parts[3].IndexOf('/');
                var tracking = new UpstreamTrack
                {
                    RemoteBranchForPull = parts[3],
                    Remote = slash >= 0 ? parts[3][..slash] : parts[3],
                    Branch = slash >= 0 ? parts[3][(slash + 1)..] : parts[3]
                };
                ParseTrack(parts[4], out var ahead, out var behind, out var gone);
                tracking.Ahead = ahead;
                tracking.Behind = behind;
                tracking.Gone = gone;
                info.Upstream = tracking;
            }

            list.Add(info);
        }

        return list;
    }

    private static Dictionary<string, string> ParseRemotes(string output)
    {
        var remotes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            var tab = text.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var name = text[..tab];
            var rest = text[(tab + 1)..].Trim();
            const string fetchSuffix = "(fetch)";
            if (!rest.EndsWith(fetchSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var url = rest[..^fetchSuffix.Length].Trim();
            if (url.Length > 0)
            {
                remotes[name] = url;
            }
        }

        return remotes;
    }

    /// <summary>
    /// <c>git stash list</c> format read by <see cref="ParseStashes"/>: name, date, stash
    /// commit, abbreviated parents (the first is the commit stashed on) and the
    /// subject, last so a tab in it does not shift the other fields.
    /// </summary>
    private const string StashListFormat = "--format=%gd\t%ci\t%H\t%p\t%s";

    private static List<StashRecord> ParseStashes(string output)
    {
        var list = new List<StashRecord>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t', 5);
            if (parts.Length < 5)
            {
                continue;
            }

            var parents = parts[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list.Add(new StashRecord
            {
                Id = parts[0],
                Created = parts[1],
                Sha = parts[2],
                BaseShortSha = parents.Length > 0 ? parents[0] : string.Empty,
                Text = parts[4]
            });
        }

        return list;
    }

    private static List<string> BuildLogArguments(HistoryFilter query)
    {
        var args = new List<string>
        {
            "log",
            "--full-history",
            query.Flags.HasFlag(HistoryFlags.Topological) ? "--topo-order" : "--date-order",
            "--format=" + LogFormat,
            "--decorate=full",
            "--date=iso"
        };

        if (!query.Flags.HasFlag(HistoryFlags.Merges))
        {
            args.Add("--no-merges");
            args.Add("--first-parent");
        }
        else if (query.FirstParent)
        {
            args.Add("--first-parent");
        }

        if (query.Count > 0)
        {
            args.Add($"--max-count={query.Count}");
        }

        if (query.Offset > 0)
        {
            args.Add($"--skip={query.Offset}");
        }

        if (!string.IsNullOrWhiteSpace(query.Author))
        {
            args.Add($"--author={query.Author}");
        }

        if (!string.IsNullOrWhiteSpace(query.StartRef))
        {
            args.Add(query.StartRef);
        }
        else
        {
            args.Add("--branches");
            args.Add("--tags");
            if (query.Flags.HasFlag(HistoryFlags.Remotes))
            {
                args.Add("--remotes");
            }

            args.Add("HEAD");
        }

        if (!string.IsNullOrWhiteSpace(query.Path))
        {
            if (query.TrackRenames)
            {
                args.Add("--follow");
            }

            args.Add("--");
            args.Add(query.Path);
        }

        return args;
    }

    private static List<HistoryRow> ParseLogRows(string output)
    {
        var rows = new List<HistoryRow>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                continue;
            }

            var fields = trimmed.Split(UnitSeparator);
            if (fields.Length != 8)
            {
                continue;
            }

            DateTime.TryParse(fields[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date);
            var parents = fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            List<RefBadge> labels = ParseDecorations(fields[6]);
            var currentBranch = labels.FirstOrDefault(l => l.Kind == RefKind.Branch && l.IsCheckedOut)?.Name;

            rows.Add(new HistoryRow
            {
                Id = fields[0],
                CommitId = fields[0],
                ShortId = fields[1],
                ParentIds = parents,
                Timestamp = date,
                Committer = fields[4],
                Author = fields[5],
                BranchName = currentBranch ?? labels.FirstOrDefault(l => l.Kind == RefKind.Branch)?.Name ?? string.Empty,
                Title = fields[7],
                Refs = [.. labels],
                RowEnabled = true
            });
        }

        return rows;
    }
}
