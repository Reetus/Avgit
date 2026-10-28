using Avgit.Core.Diff;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

public class GitHandlerTests
{
    [Fact]
    public async Task End_to_end_status_log_diff_and_hunk_staging()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "one\ntwo\nthree\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "b.txt"), "bee\n");

        StatusSnapshot status = await repo.Git.GetStatusAsync(repo.Path);
        Assert.Equal(2, status.Unstaged.Count);
        Assert.All(status.Unstaged, f => Assert.Equal(PathState.Untracked, f.Status));

        await repo.Git.StageAsync(repo.Path, status.Unstaged);
        StatusSnapshot staged = await repo.Git.GetStatusAsync(repo.Path);
        Assert.Equal(2, staged.Staged.Count);
        Assert.Empty(staged.Unstaged);

        ProcessResult commit = await repo.Git.CommitAsync(repo.Path, "initial commit");
        Assert.True(commit.Success, commit.Combined);

        var revision = await repo.Git.GetCurrentRevisionAsync(repo.Path);
        Assert.Equal(40, revision.Length);

        IReadOnlyList<HistoryRow> log = await repo.Git.GetLogAsync(repo.Path, new HistoryFilter { Count = 20 });
        HistoryRow row = Assert.Single(log);
        Assert.Equal("initial commit", row.Title);
        Assert.Empty(row.ParentIds);

        CommitRecord? details = await repo.Git.GetCommitDetailsAsync(repo.Path, revision);
        Assert.NotNull(details);
        Assert.Equal("initial commit", details!.Title);

        IReadOnlyList<ChangedFile> changes = await repo.Git.GetCommitChangesAsync(repo.Path, revision);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(PathState.New, c.Status));

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "one\nTWO\nthree\nfour\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "new.txt"), "new file\n");

        StatusSnapshot modified = await repo.Git.GetStatusAsync(repo.Path);
        Assert.Contains(modified.Unstaged, f => f.RelativePath == "a.txt" && f.Status == PathState.Edited);
        Assert.Contains(modified.Unstaged, f => f.RelativePath == "new.txt" && f.Status == PathState.Untracked);

        var raw = await repo.Git.GetDiffAsync(repo.Path, new DiffSpec
        {
            Kind = DiffRequestKind.WorkingCopy,
            Files = ["a.txt"],
            Context = 3
        });
        List<FileDiff> files = UnifiedDiffParser.Parse(raw, new DiffParseOptions
        {
            Setup = new DiffSetup { Subject = DiffSubject.PendingFiles, Scope = DiffScope.WorkTreeVsIndex }
        });
        FileDiff file = Assert.Single(files);
        Assert.Equal("a.txt", file.FileName);
        IHunk hunk = Assert.Single(file.Hunks);

        var patch = PatchBuilder.CreatePatchFromHunk(hunk, 0, hunk.LineItems.Count, isStaging: true);
        Assert.NotNull(patch);
        ProcessResult applied = await repo.Git.ApplyPatchAsync(repo.Path, patch!, cached: true, reverse: false);
        Assert.True(applied.Success, applied.Combined);

        StatusSnapshot afterPatch = await repo.Git.GetStatusAsync(repo.Path);
        Assert.Contains(afterPatch.Staged, f => f.RelativePath == "a.txt");

        var untrackedRaw = await repo.Git.GetDiffAsync(repo.Path, new DiffSpec
        {
            Kind = DiffRequestKind.WorkingCopy,
            Files = ["new.txt"]
        });
        Assert.Contains("new file mode", untrackedRaw);
        List<FileDiff> untrackedFiles = UnifiedDiffParser.Parse(untrackedRaw, new DiffParseOptions
        {
            Setup = new DiffSetup { Subject = DiffSubject.PendingFiles }
        });
        Assert.Equal(PathState.New, Assert.Single(untrackedFiles).State);
    }

    [Fact]
    public async Task Branches_tags_and_stashes()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var initialBranch = await repo.Git.GetCurrentBranchAsync(repo.Path);
        Assert.False(string.IsNullOrWhiteSpace(initialBranch));

        ProcessResult branch = await repo.Git.CreateBranchAsync(repo.Path, "feature/test", null, checkout: true);
        Assert.True(branch.Success, branch.Combined);
        Assert.Equal("feature/test", await repo.Git.GetCurrentBranchAsync(repo.Path));

        await repo.CreateCommitAsync("feature work", "feature.txt", "feature\n");
        IReadOnlyList<BranchRef> branches = await repo.Git.GetBranchesAsync(repo.Path);
        Assert.Contains(branches, b => b.Name == "feature/test" && b.IsCheckedOut);
        Assert.Contains(branches, b => b.Name == initialBranch && !b.IsCheckedOut);

        ProcessResult checkout = await repo.Git.CheckoutAsync(repo.Path, initialBranch);
        Assert.True(checkout.Success, checkout.Combined);

        ProcessResult tag = await repo.Git.CreateTagAsync(repo.Path, "v-test", null, "test tag");
        Assert.True(tag.Success, tag.Combined);
        Assert.Contains("v-test", await repo.Git.GetTagsAsync(repo.Path));

        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "stash me\n");
        ProcessResult stash = await repo.Git.StashSaveAsync(repo.Path, "wip", includeUntracked: true);
        Assert.True(stash.Success, stash.Combined);
        IReadOnlyList<StashRecord> stashes = await repo.Git.GetStashesAsync(repo.Path);
        Assert.Single(stashes);

        ProcessResult pop = await repo.Git.StashApplyAsync(repo.Path, stashes[0].Id, pop: true);
        Assert.True(pop.Success, pop.Combined);
        Assert.Empty(await repo.Git.GetStashesAsync(repo.Path));

        StatusSnapshot status = await repo.Git.GetStatusAsync(repo.Path);
        Assert.Contains(status.Unstaged, f => f.RelativePath == "readme.txt");
    }

    [Fact]
    public async Task Merge_commit_has_two_parents_and_graph_lanes()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var main = await repo.Git.GetCurrentBranchAsync(repo.Path);

        Assert.True((await repo.Git.CreateBranchAsync(repo.Path, "topic", null, checkout: true)).Success);
        await repo.CreateCommitAsync("topic work", "topic.txt", "topic\n");
        Assert.True((await repo.Git.CheckoutAsync(repo.Path, main)).Success);
        // Move main so the merge cannot fast-forward and actually creates a merge commit.
        await repo.CreateCommitAsync("main work", "main.txt", "main\n");
        Assert.True((await repo.Git.MergeAsync(repo.Path, "topic")).Success);

        IReadOnlyList<HistoryRow> log = await repo.Git.GetLogAsync(repo.Path, new HistoryFilter
        {
            Count = 50,
            Flags = HistoryFlags.Merges | HistoryFlags.Remotes
        });
        HistoryRow? merge = log.FirstOrDefault(r => r.ParentIds.Length == 2);
        Assert.NotNull(merge);
        Assert.Equal(2, merge!.ParentIds.Length);

        HistoryRow oldest = log.Last();
        InvalidOperationException mergeRange = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.Git.GetInteractiveRebaseTodoAsync(repo.Path, oldest.CommitId));
        Assert.Contains("merge", mergeRange.Message, StringComparison.OrdinalIgnoreCase);

        Avgit.Core.Graph.CommitGraphBuilder.Layout(log);
        Assert.True(merge.Graph.Edges.Count(e => e.Kind == GraphEdgeKind.Out) >= 2);
    }

    [Fact]
    public async Task Summary_counts_a_staged_and_reworked_file_once()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "staged change\n");
        await repo.StageAllAsync();
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "second change\n");

        StatusSnapshot status = await repo.Git.GetStatusAsync(repo.Path);
        Assert.Contains(status.Staged, f => f.RelativePath == "readme.txt");
        Assert.Contains(status.Unstaged, f => f.RelativePath == "readme.txt");

        RepoSummary summary = (await repo.Git.GetStatusAsync(repo.Path)).Summary;
        Assert.Equal(1, summary.Modified);
        Assert.False(summary.IsClean);
    }

    [Fact]
    public async Task Command_failures_are_reported()
    {
        var root = Path.Combine(Path.GetTempPath(), "avgit-badgit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var git = new GitRepoHandler("definitely-not-a-git-binary");
            StatusSnapshot status = await git.GetStatusAsync(root);
            Assert.Empty(status.Unstaged);
            Assert.NotNull(git.LastError);
            Assert.Contains("failed", git.LastError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Merge_conflict_can_be_aborted()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        var file = Path.Combine(repo.Path, "conflict.txt");
        await File.WriteAllTextAsync(file, "base\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "base");

        var main = await repo.Git.GetCurrentBranchAsync(repo.Path);
        Assert.True((await repo.Git.CreateBranchAsync(repo.Path, "conflict", null, true)).Success);
        await File.WriteAllTextAsync(file, "feature\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "feature");

        Assert.True((await repo.Git.CheckoutAsync(repo.Path, main)).Success);
        await File.WriteAllTextAsync(file, "main\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "main");

        ProcessResult merge = await repo.Git.MergeAsync(repo.Path, "conflict");
        Assert.False(merge.Success);

        RepoSummary summary = (await repo.Git.GetStatusAsync(repo.Path)).Summary;
        Assert.True(summary.MergeInProgress);

        StatusSnapshot conflictStatus = await repo.Git.GetStatusAsync(repo.Path);
        Assert.Empty(conflictStatus.Staged);
        ChangedFile conflict = Assert.Single(conflictStatus.Unstaged,
            f => f.Status == PathState.Conflicted);
        ProcessResult discardConflict = await repo.Git.DiscardAsync(repo.Path, [conflict]);
        Assert.False(discardConflict.Success);
        Assert.Contains("conflict", discardConflict.Error, StringComparison.OrdinalIgnoreCase);

        ProcessResult abort = await repo.Git.AbortMergeAsync(repo.Path);
        Assert.True(abort.Success, abort.Combined);
        summary = (await repo.Git.GetStatusAsync(repo.Path)).Summary;
        Assert.False(summary.MergeInProgress);
    }

    [Fact]
    public async Task Discarding_an_unstaged_change_keeps_the_index()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        var file = Path.Combine(repo.Path, "a.txt");
        await File.WriteAllTextAsync(file, "one\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "base");
        await File.WriteAllTextAsync(file, "two\n");
        await repo.StageAllAsync();
        await File.WriteAllTextAsync(file, "three\n");

        StatusSnapshot status = await repo.Git.GetStatusAsync(repo.Path);
        ChangedFile unstaged = Assert.Single(status.Unstaged);
        Assert.False(unstaged.IsStaged);

        ProcessResult discard = await repo.Git.DiscardAsync(repo.Path, [unstaged]);
        Assert.True(discard.Success, discard.Combined);
        Assert.Equal("two", (await File.ReadAllTextAsync(file)).TrimEnd('\r', '\n'));

        StatusSnapshot after = await repo.Git.GetStatusAsync(repo.Path);
        Assert.Contains(after.Staged, f => f.RelativePath == "a.txt");
        Assert.DoesNotContain(after.Unstaged, f => f.RelativePath == "a.txt");
    }

    [Fact]
    public async Task Discarding_an_untracked_file_removes_it()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var file = Path.Combine(repo.Path, "untracked.txt");
        await File.WriteAllTextAsync(file, "untracked\n");
        StatusSnapshot status = await repo.Git.GetStatusAsync(repo.Path);
        ChangedFile record = Assert.Single(status.Unstaged, f => f.RelativePath == "untracked.txt");

        ProcessResult discard = await repo.Git.DiscardAsync(repo.Path, [record]);
        Assert.True(discard.Success, discard.Combined);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Discarding_an_intent_to_add_file_removes_it()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var file = Path.Combine(repo.Path, "intent.txt");
        await File.WriteAllTextAsync(file, "content\n");
        Assert.True((await repo.Git.RunAsync(repo.Path, ["add", "-N", "intent.txt"])).Success);

        StatusSnapshot status = await repo.Git.GetStatusAsync(repo.Path);
        ChangedFile record = Assert.Single(status.Unstaged, f => f.RelativePath == "intent.txt");
        Assert.Equal(PathState.New, record.Status);
        Assert.False(record.IsStaged);

        ProcessResult discard = await repo.Git.DiscardAsync(repo.Path, [record]);
        Assert.True(discard.Success, discard.Combined);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Unstaging_one_file_leaves_the_other_staged()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "b.txt"), "b\n");
        await repo.StageAllAsync();

        StatusSnapshot status = await repo.Git.GetStatusAsync(repo.Path);
        ChangedFile a = status.Staged.First(f => f.RelativePath == "a.txt");
        ProcessResult unstage = await repo.Git.UnstageAsync(repo.Path, [a]);
        Assert.True(unstage.Success, unstage.Combined);

        StatusSnapshot after = await repo.Git.GetStatusAsync(repo.Path);
        Assert.DoesNotContain(after.Staged, f => f.RelativePath == "a.txt");
        Assert.Contains(after.Staged, f => f.RelativePath == "b.txt");
    }

    [Fact]
    public async Task Remotes_can_be_added_and_removed()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var url = "https://example.invalid/owner/repo.git";
        ProcessResult add = await repo.Git.AddRemoteAsync(repo.Path, "origin", url);
        Assert.True(add.Success, add.Combined);

        IReadOnlyDictionary<string, string> remotes = await repo.Git.GetRemotesAsync(repo.Path);
        Assert.Equal(url, remotes["origin"]);

        ProcessResult remove = await repo.Git.DeleteRemoteAsync(repo.Path, "origin");
        Assert.True(remove.Success, remove.Combined);
        Assert.Empty(await repo.Git.GetRemotesAsync(repo.Path));
    }

    [Fact]
    public async Task Interactive_rebase_can_drop_a_commit()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        IReadOnlyList<HistoryRow> log = await repo.Git.GetLogAsync(repo.Path, new HistoryFilter { Count = 20 });
        HistoryRow middle = log.First(r => r.Title == "Add more lines");
        var parent = Assert.Single(middle.ParentIds);

        var todo = await repo.Git.GetInteractiveRebaseTodoAsync(repo.Path, parent);
        Assert.Contains("Add more lines", todo);
        Assert.Contains("Add graph file", todo);

        var edited = string.Join("\n", todo.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(record =>
            {
                var separator = record.IndexOf('\x1f');
                var sha = separator >= 0 ? record[..separator].Trim() : record.Trim();
                var message = separator >= 0 ? record[(separator + 1)..] : string.Empty;
                return new { sha, message };
            })
            .Where(item => item.sha.Length > 0)
            .Select(item => (item.message.Contains("Add more lines", StringComparison.Ordinal) ? "drop " : "pick ") + item.sha));

        ProcessResult result = await repo.Git.InteractiveRebaseAsync(repo.Path, parent, edited);
        Assert.True(result.Success, result.Combined);

        IReadOnlyList<HistoryRow> after = await repo.Git.GetLogAsync(repo.Path, new HistoryFilter { Count = 20 });
        Assert.DoesNotContain(after, r => r.Title == "Add more lines");
        Assert.Contains(after, r => r.Title == "Add graph file");
    }

    [Fact]
    public async Task Interactive_rebase_reword_survives_a_dropped_commit_and_keeps_body()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        IReadOnlyList<HistoryRow> log = await repo.Git.GetLogAsync(repo.Path, new HistoryFilter { Count = 20 });
        HistoryRow middle = log.First(r => r.Title == "Add more lines");
        var baseRevision = Assert.Single(middle.ParentIds);

        var todo = await repo.Git.GetInteractiveRebaseTodoAsync(repo.Path, baseRevision);
        IEnumerable<string> records = todo.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Where(record => record.Contains('\x1f', StringComparison.Ordinal));
        var planLines = new List<string>();
        var rewordMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var fields = record.Split('\x1f', 3);
            var sha = fields[0].Trim();
            var message = fields.Length > 2 ? fields[2] : string.Empty;
            if (message.Contains("Add more lines", StringComparison.Ordinal))
            {
                planLines.Add("drop " + sha);
            }
            else
            {
                planLines.Add("reword " + sha);
                rewordMessages[sha] = "Reworded head\n\nbody preserved after drop";
            }
        }

        ProcessResult result = await repo.Git.InteractiveRebaseAsync(repo.Path, baseRevision,
            string.Join("\n", planLines), rewordMessages);
        Assert.True(result.Success, result.Combined);

        IReadOnlyList<HistoryRow> after = await repo.Git.GetLogAsync(repo.Path, new HistoryFilter { Count = 20 });
        Assert.DoesNotContain(after, r => r.Title == "Add more lines");
        HistoryRow reworded = Assert.Single(after, r => r.Title == "Reworded head");
        CommitRecord? details = await repo.Git.GetCommitDetailsAsync(repo.Path, reworded.CommitId);
        Assert.NotNull(details);
        Assert.Contains("body preserved after drop", details!.Message);
    }

    [Fact]
    public async Task Interactive_rebase_edit_then_reword_keeps_the_reword_message()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        IReadOnlyList<HistoryRow> log = await repo.Git.GetLogAsync(repo.Path, new HistoryFilter { Count = 20 });
        HistoryRow second = log.First(r => r.Title == "Add more lines");
        HistoryRow third = log.First(r => r.Title == "Add graph file");
        var baseRevision = Assert.Single(second.ParentIds);

        var todo = await repo.Git.GetInteractiveRebaseTodoAsync(repo.Path, baseRevision);
        var plan = string.Join("\n", todo.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Where(record => record.Contains('\x1f', StringComparison.Ordinal))
            .Select(record => record.Split('\x1f', 3))
            .Select(fields => new { Sha = fields[0].Trim(), Message = fields.Length > 2 ? fields[2] : string.Empty })
            .Select(item => (item.Message.Contains("Add more lines", StringComparison.Ordinal)
                ? "edit "
                : "reword ") + item.Sha));

        var rewords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [third.CommitId] = "Reworded after edit\n\nbody still here"
        };
        await repo.Git.InteractiveRebaseAsync(repo.Path, baseRevision, plan, rewords);
        Assert.True(Directory.Exists(Path.Combine(repo.Path, ".git", "rebase-merge")),
            "edit action should pause the rebase");

        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "edit change\n");
        await repo.StageAllAsync();
        ProcessResult cont = await repo.Git.ContinueRebaseAsync(repo.Path);
        Assert.True(cont.Success, cont.Combined);

        IReadOnlyList<HistoryRow> after = await repo.Git.GetLogAsync(repo.Path, new HistoryFilter { Count = 20 });
        HistoryRow reworded = Assert.Single(after, r => r.Title == "Reworded after edit");
        CommitRecord? details = await repo.Git.GetCommitDetailsAsync(repo.Path, reworded.CommitId);
        Assert.Contains("body still here", details!.Message);
        Assert.Contains(after, r => r.Title == "Add more lines");
        Assert.DoesNotContain(after, r => r.Title == "Add graph file");
    }

    [Fact]
    public async Task Interactive_rebase_reword_works_in_a_linked_worktree()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var wt = Path.Combine(Path.GetTempPath(), "avgit-wt-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProcessResult add = await ProcessRunner.RunAsync("git", repo.Path, ["worktree", "add", wt, "-b", "wt"]);
            Assert.True(add.Success, add.Combined);

            var git = new GitRepoHandler();
            var mainTip = await git.GetCurrentRevisionAsync(repo.Path);
            await File.WriteAllTextAsync(Path.Combine(wt, "wt.txt"), "worktree\n");
            StatusSnapshot wtStatus = await git.GetStatusAsync(wt);
            await git.StageAsync(wt, wtStatus.Unstaged);
            Assert.True((await git.CommitAsync(wt, "worktree commit")).Success);
            var head = await git.GetCurrentRevisionAsync(wt);

            var todo = await git.GetInteractiveRebaseTodoAsync(wt, mainTip);
            var record = todo.Split('\x1e', StringSplitOptions.RemoveEmptyEntries).First();
            var fields = record.Split('\x1f', 3);
            var sha = fields[0].Trim();
            var plan = "reword " + sha;
            var rewords = new Dictionary<string, string>(StringComparer.Ordinal) { [sha] = "Worktree reword\n\nbody" };

            ProcessResult result = await git.InteractiveRebaseAsync(wt, mainTip, plan, rewords);
            Assert.True(result.Success, result.Combined);

            IReadOnlyList<HistoryRow> after = await git.GetLogAsync(wt, new HistoryFilter { Count = 10 });
            Assert.Contains(after, r => r.Title == "Worktree reword");
            Assert.NotEqual(head, await git.GetCurrentRevisionAsync(wt));
        }
        finally
        {
            try
            {
                await ProcessRunner.RunAsync("git", repo.Path, ["worktree", "remove", "--force", wt]);
                if (Directory.Exists(wt))
                {
                    Directory.Delete(wt, true);
                }
            }
            catch { }
        }
    }

    [Fact]
    public async Task Advanced_services_patch_archive_blame_reflog_and_cherry_pick()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var advanced = new AdvancedGitService(repo.Git, repo.Path);
        var head = await repo.Git.GetCurrentRevisionAsync(repo.Path);

        var patch = await advanced.CreatePatchAsync(head);
        Assert.Contains("Subject:", patch);

        var archive = Path.Combine(repo.Path, "archive.zip");
        ProcessResult archiveResult = await advanced.CreateArchiveAsync(head, archive);
        Assert.True(archiveResult.Success, archiveResult.Combined);
        Assert.True(File.Exists(archive));

        IReadOnlyList<BlameLine> blame = await advanced.GetBlameAsync("readme.txt");
        Assert.NotEmpty(blame);
        Assert.Contains(blame, line => line.Text.Contains("one", StringComparison.Ordinal));

        IReadOnlyList<ReflogEntry> reflog = await advanced.GetReflogAsync();
        Assert.NotEmpty(reflog);

        var main = await repo.Git.GetCurrentBranchAsync(repo.Path);
        Assert.True((await repo.Git.CreateBranchAsync(repo.Path, "pickme", null, checkout: true)).Success);
        var picked = await repo.CreateCommitAsync("pick me", "pick.txt", "pick\n");
        Assert.True((await repo.Git.CheckoutAsync(repo.Path, main)).Success);
        ProcessResult cherry = await advanced.CherryPickAsync(picked);
        Assert.True(cherry.Success, cherry.Combined);
        var pickedHead = await repo.Git.GetCurrentRevisionAsync(repo.Path);
        ProcessResult revert = await advanced.RevertAsync(pickedHead);
        Assert.True(revert.Success, revert.Combined);
    }

    [Fact]
    public async Task Advanced_service_bisect_and_worktrees()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var advanced = new AdvancedGitService(repo.Git, repo.Path);
        IReadOnlyList<WorktreeInfo> worktrees = await advanced.GetWorktreesAsync();
        Assert.NotEmpty(worktrees);

        ProcessResult bisect = await advanced.BisectStartAsync("HEAD", "HEAD~2");
        Assert.True(bisect.Success, bisect.Combined);
        ProcessResult reset = await advanced.BisectResetAsync();
        Assert.True(reset.Success, reset.Combined);
    }

    [Fact]
    public async Task Cherry_pick_conflict_is_detected_and_can_be_aborted()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        var file = Path.Combine(repo.Path, "conflict.txt");
        await File.WriteAllTextAsync(file, "base\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "base");
        var main = await repo.Git.GetCurrentBranchAsync(repo.Path);

        Assert.True((await repo.Git.CreateBranchAsync(repo.Path, "pick", null, checkout: true)).Success);
        await File.WriteAllTextAsync(file, "feature\n");
        await repo.StageAllAsync();
        ProcessResult picked = await repo.Git.CommitAsync(repo.Path, "feature");
        Assert.True(picked.Success);
        var pickedSha = await repo.Git.GetCurrentRevisionAsync(repo.Path);

        Assert.True((await repo.Git.CheckoutAsync(repo.Path, main)).Success);
        await File.WriteAllTextAsync(file, "main\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "main");

        ProcessResult cherry = await repo.Git.CherryPickAsync(repo.Path, pickedSha);
        Assert.False(cherry.Success);
        RepoSummary summary = (await repo.Git.GetStatusAsync(repo.Path)).Summary;
        Assert.True(summary.CherryPickInProgress);

        ProcessResult abort = await repo.Git.CherryPickAbortAsync(repo.Path);
        Assert.True(abort.Success, abort.Combined);
        summary = (await repo.Git.GetStatusAsync(repo.Path)).Summary;
        Assert.False(summary.CherryPickInProgress);
    }

    [Fact]
    public async Task Empty_repository_is_not_an_error()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), "avgit-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var git = new GitRepoHandler();
            Assert.True((await git.InitAsync(path)).Success);
            IReadOnlyList<HistoryRow> log = await git.GetLogAsync(path, new HistoryFilter { Count = 20 });
            Assert.Empty(log);
            Assert.Null(git.LastError);
            StatusSnapshot status = await git.GetStatusAsync(path);
            Assert.Empty(status.Unstaged);
        }
        finally
        {
            try { Directory.Delete(path, true); } catch { }
        }
    }

    [Fact]
    public async Task Remote_branches_checkout_creates_a_tracking_branch()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "avgit-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using TempRepo repo = await TempRepo.CreateAsync();
            var bare = Path.Combine(temp, "remote.git");
            ProcessResult init = await ProcessRunner.RunAsync("git", temp, ["init", "--bare", bare]);
            Assert.True(init.Success, init.Combined);

            var main = await repo.Git.GetCurrentBranchAsync(repo.Path);
            Assert.True((await repo.Git.RunAsync(repo.Path, ["remote", "add", "origin", bare])).Success);
            ProcessResult push = await repo.Git.PushAsync(repo.Path, "origin", main, setUpstream: true);
            Assert.True(push.Success, push.Combined);

            await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "post-push\n");
            await repo.StageAllAsync();
            await repo.Git.CommitAsync(repo.Path, "post-push");
            ProcessResult inferredPush = await repo.Git.PushAsync(repo.Path);
            Assert.True(inferredPush.Success, inferredPush.Combined);

            Assert.True((await repo.Git.CreateBranchAsync(repo.Path, "pushnew", null, checkout: true)).Success);
            await repo.CreateCommitAsync("new branch commit", "newbranch.txt", "new\n");
            ProcessResult newBranchPush = await repo.Git.PushAsync(repo.Path, null, "pushnew", setUpstream: false);
            Assert.True(newBranchPush.Success, newBranchPush.Combined);
            ProcessResult tracking = await repo.Git.RunAsync(repo.Path, ["rev-parse", "--abbrev-ref", "@{u}"]);
            Assert.True(tracking.Success, tracking.Combined);

            var clonePath = Path.Combine(temp, "clone");
            ProcessResult clone = await repo.Git.CloneAsync(bare, clonePath);
            Assert.True(clone.Success, clone.Combined);

            var git = new GitRepoHandler();
            IReadOnlyList<BranchRef> branches = await git.GetBranchesAsync(clonePath);
            Assert.Contains(branches, b => b.Kind == Avgit.Core.Model.RefKind.RemoteBranch
                                           && b.Name.EndsWith("/" + main, StringComparison.Ordinal));
            Assert.DoesNotContain(branches, b => b.Name.EndsWith("/HEAD", StringComparison.Ordinal));

            ProcessResult checkout = await git.CheckoutAsync(clonePath, "origin/" + main);
            Assert.True(checkout.Success, checkout.Combined);
            Assert.Equal(main, await git.GetCurrentBranchAsync(clonePath));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public async Task Push_to_branch_pushRemote_takes_precedence_over_branch_remote()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "avgit-pushremote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using TempRepo repo = await TempRepo.CreateAsync();
            var origin = Path.Combine(temp, "origin.git");
            var fork = Path.Combine(temp, "fork.git");
            Assert.True((await ProcessRunner.RunAsync("git", temp,
                ["init", "--bare", origin])).Success);
            Assert.True((await ProcessRunner.RunAsync("git", temp,
                ["init", "--bare", fork])).Success);

            var main = await repo.Git.GetCurrentBranchAsync(repo.Path);
            Assert.True((await repo.Git.AddRemoteAsync(repo.Path, "origin", origin)).Success);
            Assert.True((await repo.Git.AddRemoteAsync(repo.Path, "fork", fork)).Success);
            ProcessResult first = await repo.Git.PushAsync(repo.Path, "origin", main, setUpstream: true);
            Assert.True(first.Success, first.Combined);
            var originSha = await repo.Git.GetCurrentRevisionAsync(repo.Path);

            await repo.CreateCommitAsync("next", "next.txt", "next\n");
            Assert.True((await repo.Git.RunAsync(repo.Path,
                ["config", $"branch.{main}.pushRemote", "fork"])).Success);

            ProcessResult pushed = await repo.Git.PushAsync(repo.Path, null, main);
            Assert.True(pushed.Success, pushed.Combined);

            ProcessResult onFork = await ProcessRunner.RunAsync("git", temp,
                ["--git-dir", fork, "rev-parse", "--verify", "refs/heads/" + main]);
            Assert.True(onFork.Success, onFork.Combined);

            ProcessResult onOrigin = await ProcessRunner.RunAsync("git", temp,
                ["--git-dir", origin, "rev-parse", "--verify", "refs/heads/" + main]);
            Assert.True(onOrigin.Success, onOrigin.Combined);
            Assert.Equal(originSha, onOrigin.Output.Trim());
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public async Task Detached_HEAD_can_be_pushed_to_a_named_branch()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "avgit-detached-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using TempRepo repo = await TempRepo.CreateAsync();
            var bare = Path.Combine(temp, "remote.git");
            Assert.True((await ProcessRunner.RunAsync("git", temp, ["init", "--bare", bare])).Success);
            Assert.True((await repo.Git.AddRemoteAsync(repo.Path, "origin", bare)).Success);

            var sha = await repo.Git.GetCurrentRevisionAsync(repo.Path);
            Assert.True((await repo.Git.CheckoutAsync(repo.Path, sha)).Success);

            ProcessResult push = await repo.Git.PushAsync(repo.Path, "origin", null, setUpstream: false,
                force: false, detachedTarget: "detached-test");
            Assert.True(push.Success, push.Combined);

            ProcessResult remote = await ProcessRunner.RunAsync("git", temp,
                ["--git-dir", bare, "rev-parse", "--verify", "refs/heads/detached-test"]);
            Assert.True(remote.Success, remote.Combined);
            Assert.Equal(sha, remote.Output.Trim());
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public async Task Commit_template_is_read_from_git_config()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var template = Path.Combine(repo.Path, "commit-template.txt");
        await File.WriteAllTextAsync(template, "Subject line\n\nTicket: \n");
        Assert.True((await repo.Git.RunAsync(repo.Path,
            ["config", "commit.template", "commit-template.txt"])).Success);

        var text = await repo.Git.GetCommitTemplateAsync(repo.Path);
        Assert.Equal("Subject line\n\nTicket: \n", text);
    }

    [Fact]
    public async Task Remotes_can_be_renamed_and_repointed()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        Assert.True((await repo.Git.AddRemoteAsync(repo.Path, "origin",
            "https://example.invalid/first.git")).Success);

        ProcessResult rename = await repo.Git.RenameRemoteAsync(repo.Path, "origin", "upstream");
        Assert.True(rename.Success, rename.Combined);
        ProcessResult setUrl = await repo.Git.SetRemoteUrlAsync(repo.Path, "upstream",
            "https://example.invalid/second.git");
        Assert.True(setUrl.Success, setUrl.Combined);

        IReadOnlyDictionary<string, string> remotes = await repo.Git.GetRemotesAsync(repo.Path);
        Assert.DoesNotContain("origin", remotes.Keys);
        Assert.Equal("https://example.invalid/second.git", remotes["upstream"]);
    }

    [Fact]
    public async Task Interactive_rebase_can_rewrite_from_the_root_commit()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        ProcessResult root = await repo.Git.RunAsync(repo.Path,
            ["rev-list", "--max-parents=0", "HEAD"]);
        Assert.True(root.Success, root.Combined);
        var rootSha = root.Output.Trim();

        var plan = await repo.Git.GetInteractiveRebaseTodoAsync(repo.Path, string.Empty, fromRoot: true);
        Assert.Contains(rootSha, plan);
        var todo = string.Join("\n", plan.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(record => "pick " + record.Split('\x1f')[0].Trim()));

        ProcessResult rebase = await repo.Git.InteractiveRebaseAsync(repo.Path, string.Empty, todo,
            fromRoot: true);
        Assert.True(rebase.Success, rebase.Combined);
        RepoSummary summary = (await repo.Git.GetStatusAsync(repo.Path)).Summary;
        Assert.False(summary.RebaseInProgress);
    }

    [Fact]
    public async Task Git_notes_can_be_added_shown_and_removed()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var advanced = new AdvancedGitService(repo.Git, repo.Path);
        var head = await repo.Git.GetCurrentRevisionAsync(repo.Path);

        ProcessResult add = await advanced.NotesAddAsync(head, "verification note");
        Assert.True(add.Success, add.Combined);
        ProcessResult show = await advanced.NotesShowAsync(head);
        Assert.True(show.Success, show.Combined);
        Assert.Contains("verification note", show.Output);

        ProcessResult remove = await advanced.NotesRemoveAsync(head);
        Assert.True(remove.Success, remove.Combined);
        ProcessResult gone = await advanced.NotesShowAsync(head);
        Assert.False(gone.Success);
    }

    [Fact]
    public async Task Merge_commit_patch_export_is_rejected_instead_of_writing_a_parent_patch()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "base.txt"), "base\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "base");
        var main = await repo.Git.GetCurrentBranchAsync(repo.Path);

        Assert.True((await repo.Git.CreateBranchAsync(repo.Path, "side", null, checkout: true)).Success);
        await repo.CreateCommitAsync("side", "side.txt", "side\n");
        Assert.True((await repo.Git.CheckoutAsync(repo.Path, main)).Success);
        await repo.CreateCommitAsync("main", "main.txt", "main\n");
        ProcessResult merge = await repo.Git.MergeAsync(repo.Path, "side");
        Assert.True(merge.Success, merge.Combined);

        var advanced = new AdvancedGitService(repo.Git, repo.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => advanced.CreatePatchAsync("HEAD"));
    }

    [Fact]
    public async Task Hooks_path_is_absolute_and_exists()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var advanced = new AdvancedGitService(repo.Git, repo.Path);
        var hooks = await advanced.GetHooksPathAsync();
        Assert.NotNull(hooks);
        Assert.True(Path.IsPathRooted(hooks!));
        Assert.True(Directory.Exists(hooks));
    }

    [Fact]
    public void Repository_detection_finds_git()
    {
        var root = Path.Combine(Path.GetTempPath(), "st-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "git", ".git"));
        Directory.CreateDirectory(Path.Combine(root, "none"));
        try
        {
            var manager = new GitManager();
            GitRepository? opened = manager.Open(Path.Combine(root, "git"));
            Assert.NotNull(opened);
            Assert.Equal(Path.Combine(root, "git"), opened!.Root);
            Assert.Null(manager.Open(Path.Combine(root, "none")));
            Assert.Equal(Path.Combine(root, "git"), GitManager.FindRepositoryRoot(Path.Combine(root, "git", "sub")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
