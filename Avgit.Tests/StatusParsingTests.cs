using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>
/// Porcelain v2 (<c>-z</c>) and <c>--name-status -z</c> parsing from hand-written
/// output, plus real-git scenarios for conflicts, renames and in-progress operations.
/// </summary>
public class StatusParsingTests
{
    private const string Root = "repo";
    private const string Hashes = "100644 100644 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222";
    private const string MainHeaders = "# branch.oid 3333333333333333333333333333333333333333\0# branch.head main\0";

    private static string Ordinary(string xy, string path, string sub = "N...")
        => $"1 {xy} {sub} {Hashes} {path}\0";

    private static string Renamed(string xy, string score, string path, string original)
        => $"2 {xy} N... {Hashes} {score} {path}\0{original}\0";

    private static string Unmerged(string xy, string path)
        => $"u {xy} N... 100644 100644 100644 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 4444444444444444444444444444444444444444 {path}\0";

    private static StatusSnapshot Parse(string output, PathState mask = PathState.WorkingCopyMask,
        RepoOperations operations = RepoOperations.None)
        => GitRepoHandler.ParseStatus(Root, output, mask, operations);

    [Theory]
    [InlineData('M', PathState.Edited)]
    [InlineData('T', PathState.Edited)]
    [InlineData('A', PathState.New)]
    [InlineData('D', PathState.Removed)]
    public void Index_letters_map_to_the_staged_list(char x, PathState expected)
    {
        StatusSnapshot status = Parse(MainHeaders + Ordinary(x + ".", "file.txt"));

        ChangedFile staged = Assert.Single(status.Staged);
        Assert.Empty(status.Unstaged);
        Assert.Equal(expected, staged.Status);
        Assert.Equal(expected, staged.IndexStatus);
        Assert.Equal(PathState.Unchanged, staged.WorkTreeStatus);
        Assert.Equal(ChangeArea.Index, staged.Area);
        Assert.True(staged.IsStaged);
        Assert.Equal("file.txt", staged.RelativePath);
        Assert.Equal(Path.Combine(Root, "file.txt"), staged.FullPath);
    }

    [Theory]
    [InlineData('M', PathState.Edited)]
    [InlineData('T', PathState.Edited)]
    [InlineData('A', PathState.New)]
    [InlineData('D', PathState.Removed)]
    public void Work_tree_letters_map_to_the_unstaged_list(char y, PathState expected)
    {
        StatusSnapshot status = Parse(MainHeaders + Ordinary("." + y, "dir/file.txt"));

        ChangedFile unstaged = Assert.Single(status.Unstaged);
        Assert.Empty(status.Staged);
        Assert.Equal(expected, unstaged.Status);
        Assert.Equal(PathState.Unchanged, unstaged.IndexStatus);
        Assert.Equal(expected, unstaged.WorkTreeStatus);
        Assert.Equal(ChangeArea.WorkTree, unstaged.Area);
        Assert.False(unstaged.IsStaged);
        Assert.Equal("file.txt", unstaged.FileName);
    }

    [Fact]
    public void Both_sides_changed_appears_in_both_lists()
    {
        StatusSnapshot status = Parse(MainHeaders + Ordinary("MM", "both.txt") + Ordinary("AD", "gone.txt"));

        Assert.Equal(["both.txt", "gone.txt"], status.Staged.Select(f => f.RelativePath));
        Assert.Equal(["both.txt", "gone.txt"], status.Unstaged.Select(f => f.RelativePath));
        Assert.Equal(PathState.New, status.Staged[1].Status);
        Assert.Equal(PathState.Removed, status.Unstaged[1].Status);
        Assert.Equal(PathState.New, status.Unstaged[1].IndexStatus);

        // Each path counts once, by its staged state.
        Assert.Equal(1, status.Summary.Modified);
        Assert.Equal(1, status.Summary.Added);
        Assert.Equal(0, status.Summary.Removed);
        Assert.False(status.Summary.IsClean);
    }

    [Fact]
    public void Renames_and_copies_keep_the_original_path_even_with_spaces_and_newlines()
    {
        StatusSnapshot status = Parse(MainHeaders
            + Renamed("R.", "R100", "new name\nwith newline.txt", "old name.txt")
            + Renamed("C.", "C75", "copy.txt", "source dir/source.txt")
            + Renamed("RM", "R90", "moved.txt", "before.txt"));

        Assert.Equal(3, status.Staged.Count);
        ChangedFile rename = status.Staged[0];
        Assert.Equal(PathState.Moved, rename.Status);
        Assert.Equal("new name\nwith newline.txt", rename.RelativePath);
        Assert.Equal("old name.txt", rename.OldPath);
        Assert.True(rename.HasOldPath);

        ChangedFile copy = status.Staged[1];
        Assert.Equal(PathState.Duplicated, copy.Status);
        Assert.Equal("source dir/source.txt", copy.OldPath);

        // The work-tree side of a renamed file is a plain edit of the new path.
        ChangedFile edited = Assert.Single(status.Unstaged);
        Assert.Equal("moved.txt", edited.RelativePath);
        Assert.Equal(PathState.Edited, edited.Status);
        Assert.Null(edited.OldPath);

        Assert.Equal(2, status.Summary.Renamed);
        Assert.Equal(1, status.Summary.Copied);
    }

    [Theory]
    [InlineData("DD")]
    [InlineData("AU")]
    [InlineData("UD")]
    [InlineData("UA")]
    [InlineData("DU")]
    [InlineData("AA")]
    [InlineData("UU")]
    public void Every_unmerged_combination_is_conflicted_and_only_unstaged(string xy)
    {
        StatusSnapshot status = Parse(MainHeaders + Unmerged(xy, "clash.txt") + Ordinary(xy, "ordinary-form.txt"));

        Assert.Empty(status.Staged);
        Assert.Equal(2, status.Unstaged.Count);
        Assert.All(status.Unstaged, f =>
        {
            Assert.Equal(PathState.Conflicted, f.Status);
            Assert.Equal(ChangeArea.WorkTree, f.Area);
        });
        Assert.Equal("clash.txt", status.Unstaged[0].RelativePath);
        Assert.Equal(2, status.Summary.Conflicted);
    }

    [Fact]
    public void Untracked_and_ignored_entries()
    {
        var output = MainHeaders + "? new file.txt\0? folder/\0! bin/\0";

        StatusSnapshot normal = Parse(output);
        Assert.Equal(["new file.txt", "folder/"], normal.Unstaged.Select(f => f.RelativePath));
        Assert.All(normal.Unstaged, f => Assert.Equal(PathState.Untracked, f.Status));
        Assert.Equal(2, normal.Summary.Untracked);

        StatusSnapshot withIgnored = Parse(output, PathState.WorkingCopyWithIgnoredMask);
        ChangedFile ignored = Assert.Single(withIgnored.Unstaged, f => f.Status == PathState.Excluded);
        Assert.Equal("bin/", ignored.RelativePath);
        Assert.Equal("I", ignored.StatusText);
    }

    [Fact]
    public void Ignored_files_alone_leave_the_tree_clean()
    {
        StatusSnapshot status = Parse(MainHeaders + "! obj/\0", PathState.WorkingCopyWithIgnoredMask);
        Assert.Single(status.Unstaged);
        Assert.True(status.Summary.IsClean);
    }

    [Fact]
    public void The_mask_filters_entries()
    {
        var output = MainHeaders + Ordinary("M.", "a.txt") + Ordinary(".D", "b.txt") + "? c.txt\0" + Unmerged("UU", "d.txt");

        StatusSnapshot tracked = Parse(output, PathState.ChangedMask);
        Assert.DoesNotContain(tracked.Unstaged, f => f.Status == PathState.Untracked);
        Assert.Equal(0, tracked.Summary.Untracked);

        StatusSnapshot onlyEdits = Parse(output, PathState.Edited);
        Assert.Single(onlyEdits.Staged);
        Assert.Empty(onlyEdits.Unstaged);
        Assert.Equal(1, onlyEdits.Summary.Modified);
        Assert.Equal(0, onlyEdits.Summary.Conflicted);
    }

    [Fact]
    public void Submodule_commit_changes_are_reported_as_such()
    {
        StatusSnapshot status = Parse(MainHeaders
            + Ordinary(".M", "lib/moved", "SC..")
            + Ordinary(".M", "lib/dirty", "S.M.")
            + Ordinary("M.", "lib/staged", "S..."));

        Assert.Equal(PathState.SubmoduleCommitChanged, status.Unstaged.Single(f => f.RelativePath == "lib/moved").Status);
        Assert.Equal(PathState.Edited, status.Unstaged.Single(f => f.RelativePath == "lib/dirty").Status);
        Assert.Equal(PathState.SubmoduleCommitChanged, Assert.Single(status.Staged).Status);
        Assert.Equal(3, status.Summary.Modified);
    }

    [Fact]
    public void Branch_headers_with_upstream()
    {
        StatusSnapshot status = Parse(
            "# branch.oid 3333333333333333333333333333333333333333\0# branch.head feature/x\0" +
            "# branch.upstream origin/feature/x\0# branch.ab +2 -5\0");

        RepoSummary summary = status.Summary;
        Assert.Equal("feature/x", summary.Branch);
        Assert.Equal("feature/x", status.Branch);
        Assert.False(summary.IsDetached);
        Assert.Equal("3333333333333333333333333333333333333333", summary.HeadId);
        Assert.Equal(summary.HeadId, status.Revision);
        Assert.Equal("origin/feature/x", summary.Upstream);
        Assert.Equal(2, summary.Ahead);
        Assert.Equal(5, summary.Behind);
        Assert.True(summary.IsClean);
        Assert.Null(status.Error);
    }

    [Fact]
    public void Detached_head_without_upstream()
    {
        StatusSnapshot status = Parse("# branch.oid 3333333333333333333333333333333333333333\0# branch.head (detached)\0");

        Assert.True(status.Summary.IsDetached);
        Assert.Null(status.Summary.Branch);
        Assert.Equal(RepoSummary.DetachedHeadLabel, status.Branch);
        Assert.Null(status.Summary.Upstream);
        Assert.Equal(0, status.Summary.Ahead);
        Assert.Equal(0, status.Summary.Behind);
    }

    [Fact]
    public void Empty_repository_with_no_commits()
    {
        StatusSnapshot status = Parse("# branch.oid (initial)\0# branch.head main\0? first.txt\0");

        Assert.True(status.Summary.IsUnborn);
        Assert.Null(status.Summary.HeadId);
        Assert.Equal(string.Empty, status.Revision);
        Assert.Equal("main", status.Branch);
        Assert.Single(status.Unstaged);

        StatusSnapshot nothing = Parse(string.Empty);
        Assert.Empty(nothing.Staged);
        Assert.Empty(nothing.Unstaged);
        Assert.True(nothing.Summary.IsClean);
    }

    [Fact]
    public void Operations_are_carried_into_the_summary()
    {
        StatusSnapshot status = Parse(MainHeaders, operations: RepoOperations.Merge | RepoOperations.Bisect);
        Assert.True(status.Summary.MergeInProgress);
        Assert.True(status.Summary.BisectInProgress);
        Assert.False(status.Summary.RebaseInProgress);
    }

    [Fact]
    public void Operations_are_detected_from_git_dir_markers()
    {
        var gitDir = Path.Combine(Path.GetTempPath(), "avgit-ops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gitDir);
        try
        {
            Assert.Equal(RepoOperations.None, GitRepoHandler.DetectOperations(gitDir));

            File.WriteAllText(Path.Combine(gitDir, "MERGE_HEAD"), "x\n");
            File.WriteAllText(Path.Combine(gitDir, "CHERRY_PICK_HEAD"), "x\n");
            File.WriteAllText(Path.Combine(gitDir, "REVERT_HEAD"), "x\n");
            File.WriteAllText(Path.Combine(gitDir, "BISECT_LOG"), "x\n");
            Assert.Equal(RepoOperations.Merge | RepoOperations.CherryPick | RepoOperations.Revert | RepoOperations.Bisect,
                GitRepoHandler.DetectOperations(gitDir));
            File.Delete(Path.Combine(gitDir, "MERGE_HEAD"));
            File.Delete(Path.Combine(gitDir, "CHERRY_PICK_HEAD"));
            File.Delete(Path.Combine(gitDir, "REVERT_HEAD"));
            File.Delete(Path.Combine(gitDir, "BISECT_LOG"));

            var apply = Path.Combine(gitDir, "rebase-apply");
            Directory.CreateDirectory(apply);
            Assert.Equal(RepoOperations.Rebase, GitRepoHandler.DetectOperations(gitDir));
            File.WriteAllText(Path.Combine(apply, "applying"), string.Empty);
            Assert.Equal(RepoOperations.ApplyMailbox, GitRepoHandler.DetectOperations(gitDir));
            Directory.Delete(apply, true);

            Directory.CreateDirectory(Path.Combine(gitDir, "rebase-merge"));
            Assert.Equal(RepoOperations.Rebase, GitRepoHandler.DetectOperations(gitDir));
        }
        finally
        {
            Directory.Delete(gitDir, true);
        }
    }

    [Fact]
    public void Name_status_renames_copies_and_type_changes()
    {
        List<ChangedFile> files = GitRepoHandler.ParseNameStatus(Root,
            "\nR087\0old name.txt\0new name.txt\0C100\0src.txt\0src copy.txt\0T\0link\0A\0added.txt\0" +
            "M\0edited.txt\0D\0removed.txt\0U\0conflict.txt\0X\0unknown.txt\0B\0broken.txt\0");

        Assert.Equal(["new name.txt", "src copy.txt", "link", "added.txt", "edited.txt", "removed.txt", "conflict.txt"],
            files.Select(f => f.RelativePath));
        Assert.Equal(PathState.Moved, files[0].Status);
        Assert.Equal("old name.txt", files[0].OldPath);
        Assert.Equal(PathState.Duplicated, files[1].Status);
        Assert.Equal("src.txt", files[1].OldPath);
        Assert.Equal(PathState.Edited, files[2].Status);
        Assert.Null(files[2].OldPath);
        Assert.Equal(PathState.New, files[3].Status);
        Assert.Equal(PathState.Edited, files[4].Status);
        Assert.Equal(PathState.Removed, files[5].Status);
        Assert.Equal(PathState.Conflicted, files[6].Status);
        Assert.All(files, f => Assert.Equal(ChangeArea.Commit, f.Area));
    }

    [Fact]
    public async Task Merge_conflict_is_only_an_unstaged_conflicted_entry()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        await CreateDivergedBranchesAsync(repo, "topic");
        Assert.False((await repo.Git.MergeAsync(repo.Path, "topic")).Success);

        StatusSnapshot status = await repo.Git.GetStatusAsync(repo.Path);
        Assert.Null(status.Error);
        Assert.Empty(status.Staged);
        ChangedFile conflict = Assert.Single(status.Unstaged);
        Assert.Equal("conflict.txt", conflict.RelativePath);
        Assert.Equal(PathState.Conflicted, conflict.Status);
        Assert.True(status.Summary.MergeInProgress);
        Assert.Equal(1, status.Summary.Conflicted);
    }

    [Fact]
    public async Task Staged_rename_is_reported_with_its_original_path()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        Assert.True((await repo.Git.RunAsync(repo.Path, ["mv", "program.cs", "renamed program.cs"])).Success);

        StatusSnapshot status = await repo.Git.GetStatusAsync(repo.Path);
        ChangedFile rename = Assert.Single(status.Staged);
        Assert.Equal(PathState.Moved, rename.Status);
        Assert.Equal("renamed program.cs", rename.RelativePath);
        Assert.Equal("program.cs", rename.OldPath);
        Assert.Empty(status.Unstaged);
        Assert.Equal(1, status.Summary.Renamed);
    }

    [Fact]
    public async Task File_staged_and_modified_again_is_in_both_lists()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var file = Path.Combine(repo.Path, "readme.txt");
        await File.AppendAllTextAsync(file, "staged\n");
        await repo.StageAllAsync();
        await File.AppendAllTextAsync(file, "unstaged\n");

        StatusSnapshot status = await repo.Git.GetStatusAsync(repo.Path);
        ChangedFile staged = Assert.Single(status.Staged);
        ChangedFile unstaged = Assert.Single(status.Unstaged);
        Assert.Equal("readme.txt", staged.RelativePath);
        Assert.Equal("readme.txt", unstaged.RelativePath);
        Assert.Equal(PathState.Edited, staged.Status);
        Assert.Equal(PathState.Edited, unstaged.Status);
        Assert.Equal(1, status.Summary.Modified);
        Assert.False(status.Summary.IsDetached);
        Assert.Equal(await repo.Git.GetCurrentRevisionAsync(repo.Path), status.Revision);
    }

    [Fact]
    public async Task Cherry_pick_in_progress_is_detected()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        var topicTip = await CreateDivergedBranchesAsync(repo, "topic");
        Assert.False((await repo.Git.CherryPickAsync(repo.Path, topicTip)).Success);

        RepoSummary summary = (await repo.Git.GetStatusAsync(repo.Path)).Summary;
        Assert.True(summary.CherryPickInProgress);
        Assert.False(summary.MergeInProgress);
        Assert.Equal(RepoOperations.CherryPick, summary.Operations);
    }

    [Fact]
    public async Task Revert_in_progress_is_detected()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        var file = Path.Combine(repo.Path, "conflict.txt");
        await File.WriteAllTextAsync(file, "one\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "one");
        await File.WriteAllTextAsync(file, "two\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "two");
        var two = await repo.Git.GetCurrentRevisionAsync(repo.Path);
        await File.WriteAllTextAsync(file, "three\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "three");

        Assert.False((await repo.Git.RevertAsync(repo.Path, two)).Success);

        RepoSummary summary = (await repo.Git.GetStatusAsync(repo.Path)).Summary;
        Assert.True(summary.RevertInProgress);
        Assert.Equal(RepoOperations.Revert, summary.Operations);
    }

    [Fact]
    public async Task Rebase_in_progress_is_detected()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        await CreateDivergedBranchesAsync(repo, "topic");
        Assert.True((await repo.Git.CheckoutAsync(repo.Path, "topic")).Success);
        var main = (await repo.Git.RunAsync(repo.Path, ["rev-parse", "--abbrev-ref", "@{-1}"])).Output.Trim();
        Assert.False((await repo.Git.RebaseAsync(repo.Path, main)).Success);

        StatusSnapshot status = await repo.Git.GetStatusAsync(repo.Path);
        Assert.True(status.Summary.RebaseInProgress);
        Assert.False(status.Summary.ApplyMailboxInProgress);
        Assert.True(status.Summary.IsDetached);
        Assert.Contains(status.Unstaged, f => f.Status == PathState.Conflicted);

        Assert.True((await repo.Git.AbortRebaseAsync(repo.Path)).Success);
        Assert.False((await repo.Git.GetStatusAsync(repo.Path)).Summary.RebaseInProgress);
    }

    [Fact]
    public async Task Linked_worktree_operations_come_from_its_own_git_dir()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        var topicTip = await CreateDivergedBranchesAsync(repo, "topic");
        var worktree = Path.Combine(Path.GetTempPath(), "avgit-wt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True((await repo.Git.RunAsync(repo.Path, ["worktree", "add", "--detach", worktree, "HEAD"])).Success);
            var engine = new GitRepoHandler();
            Assert.False((await engine.CherryPickAsync(worktree, topicTip)).Success);

            Assert.True((await engine.GetStatusAsync(worktree)).Summary.CherryPickInProgress);
            Assert.False((await repo.Git.GetStatusAsync(repo.Path)).Summary.CherryPickInProgress);
        }
        finally
        {
            await repo.Git.RunAsync(repo.Path, ["worktree", "remove", "--force", worktree]);
        }
    }

    /// <summary>Two branches that both change conflict.txt; leaves the original branch checked out.</summary>
    private static async Task<string> CreateDivergedBranchesAsync(TempRepo repo, string topic)
    {
        var file = Path.Combine(repo.Path, "conflict.txt");
        await File.WriteAllTextAsync(file, "base\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "base");
        var main = await repo.Git.GetCurrentBranchAsync(repo.Path);

        Assert.True((await repo.Git.CreateBranchAsync(repo.Path, topic, null, checkout: true)).Success);
        await File.WriteAllTextAsync(file, "topic\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "topic");
        var topicTip = await repo.Git.GetCurrentRevisionAsync(repo.Path);

        Assert.True((await repo.Git.CheckoutAsync(repo.Path, main)).Success);
        await File.WriteAllTextAsync(file, "main\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "main");
        return topicTip;
    }
}
