using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>The engine's public surface: opening repositories, error reporting and change lists.</summary>
public class EngineApiTests
{
    [Fact]
    public async Task Open_finds_the_root_from_a_subfolder_and_from_a_linked_worktree()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var sub = Path.Combine(repo.Path, "src", "deep");
        Directory.CreateDirectory(sub);
        var manager = new GitManager();

        GitRepository? opened = manager.Open(sub);
        Assert.NotNull(opened);
        Assert.Equal(Path.GetFullPath(repo.Path), opened!.Root);
        Assert.NotSame(manager.Git, opened.Engine);
        Assert.NotSame(opened.Engine, manager.Open(repo.Path)!.Engine);

        var worktree = Path.Combine(Path.GetTempPath(), "avgit-open-wt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True((await repo.Git.RunAsync(repo.Path, ["worktree", "add", "--detach", worktree, "HEAD"])).Success);
            Assert.True(File.Exists(Path.Combine(worktree, ".git")));
            Assert.True(GitManager.IsRepositoryRoot(worktree));

            GitRepository? linked = manager.Open(worktree);
            Assert.NotNull(linked);
            Assert.Equal(Path.GetFullPath(worktree), linked!.Root);

            StatusSnapshot status = await linked.Engine.GetStatusAsync(linked.Root);
            Assert.Null(status.Error);
            Assert.True(status.Summary.IsDetached);
            Assert.Equal(RepoSummary.DetachedHeadLabel, await linked.Engine.GetCurrentBranchAsync(linked.Root));
        }
        finally
        {
            await repo.Git.RunAsync(repo.Path, ["worktree", "remove", "--force", worktree]);
        }
    }

    [Fact]
    public void Open_returns_null_outside_a_repository_and_for_a_bogus_dot_git_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "avgit-noopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manager = new GitManager();
            Assert.Null(manager.Open(Path.Combine(root, "missing")));
            Assert.Null(manager.Open(string.Empty));

            File.WriteAllText(Path.Combine(root, ".git"), "not a gitdir pointer\n");
            Assert.False(GitManager.IsRepositoryRoot(root));

            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../elsewhere/.git/worktrees/x\n");
            Assert.True(GitManager.IsRepositoryRoot(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Status_errors_go_to_the_snapshot_and_only_GetStatusAsync_records_LastError()
    {
        var root = Path.Combine(Path.GetTempPath(), "avgit-noengine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var engine = new GitRepoHandler("definitely-not-a-git-binary");
            StatusSnapshot snapshot = await engine.GetStatusSnapshotAsync(root);
            Assert.NotNull(snapshot.Error);
            Assert.False(snapshot.Summary.IsClean);
            Assert.Null(engine.LastError);

            StatusSnapshot status = await engine.GetStatusAsync(root);
            Assert.Equal(snapshot.Error, status.Error);
            Assert.Equal(status.Error, engine.LastError);

            engine.ClearLastError();
            Assert.Null(engine.LastError);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Ignored_files_are_listed_only_when_the_mask_asks()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(repo.Path, ".gitignore"), "*.log\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "ignore logs");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "trace.log"), "noise\n");

        StatusSnapshot normal = await repo.Git.GetStatusAsync(repo.Path);
        Assert.Empty(normal.Unstaged);
        Assert.True(normal.Summary.IsClean);

        StatusSnapshot withIgnored = await repo.Git.GetStatusAsync(repo.Path, PathState.WorkingCopyWithIgnoredMask);
        ChangedFile ignored = Assert.Single(withIgnored.Unstaged);
        Assert.Equal("trace.log", ignored.RelativePath);
        Assert.Equal(PathState.Excluded, ignored.Status);
        Assert.True(withIgnored.Summary.IsClean);
    }

    [Fact]
    public async Task Commit_and_range_changes_use_the_commit_area()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var first = (await repo.Git.RunAsync(repo.Path, ["rev-list", "--max-parents=0", "HEAD"])).Output.Trim();
        Assert.True((await repo.Git.RunAsync(repo.Path, ["mv", "program.cs", "main.cs"])).Success);
        await repo.Git.CommitAsync(repo.Path, "rename");
        var head = await repo.Git.GetCurrentRevisionAsync(repo.Path);

        IReadOnlyList<ChangedFile> commit = await repo.Git.GetCommitChangesAsync(repo.Path, head);
        ChangedFile rename = Assert.Single(commit);
        Assert.Equal(PathState.Moved, rename.Status);
        Assert.Equal("program.cs", rename.OldPath);
        Assert.Equal(ChangeArea.Commit, rename.Area);
        Assert.False(rename.IsStaged);

        IReadOnlyList<ChangedFile> range = await repo.Git.GetRangeChangesAsync(repo.Path, first, head);
        Assert.Contains(range, f => f.RelativePath == "main.cs" && f.Status == PathState.Moved);
        Assert.Contains(range, f => f.RelativePath == "graph.txt" && f.Status == PathState.New);
        Assert.Contains(range, f => f.RelativePath == "readme.txt" && f.Status == PathState.Edited);

        CommitResult batched = await repo.Git.GetCommitAsync(repo.Path, head);
        Assert.Null(batched.Error);
        Assert.Equal(PathState.Moved, Assert.Single(batched.Commit!.Files).Status);
    }

    [Fact]
    public void Records_in_different_lists_are_distinct()
    {
        var staged = new ChangedFile { RelativePath = "a.txt", Area = ChangeArea.Index, Status = PathState.Edited };
        var unstaged = new ChangedFile { RelativePath = "a.txt", Area = ChangeArea.WorkTree, Status = PathState.Edited };
        var again = new ChangedFile { RelativePath = "a.txt", Area = ChangeArea.Index, Status = PathState.New };

        Assert.NotEqual(staged, unstaged);
        Assert.Equal(staged, again);
        Assert.Equal(staged.GetHashCode(), again.GetHashCode());

        var changed = new List<string>();
        staged.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        staged.CopyStatusFrom(again);
        Assert.Equal(PathState.New, staged.Status);
        Assert.Contains(nameof(ChangedFile.StatusText), changed);
        Assert.Equal("A", staged.StatusText);
    }
}
