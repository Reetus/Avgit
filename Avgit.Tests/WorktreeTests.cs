using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.App.Views;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>Worktrees: listing them, marking their branches, and creating, opening and removing them.</summary>
public class WorktreeTests
{
    private static readonly string ArtifactDirectory =
        Path.Combine(Path.GetTempPath(), "avgit-headless");

    [Fact]
    public void Porcelain_output_is_parsed()
    {
        const string output = """
            worktree D:/Code/repo
            HEAD 1111111111111111111111111111111111111111
            branch refs/heads/main

            worktree D:/Code/repo-feature
            HEAD 2222222222222222222222222222222222222222
            branch refs/heads/feature/login
            locked on a USB drive

            worktree D:/Code/repo-old
            HEAD 3333333333333333333333333333333333333333
            detached
            prunable gitdir file points to non-existent location

            """;

        IReadOnlyList<WorktreeInfo> worktrees = GitRepoHandler.ParseWorktrees(output.Replace("\n", "\r\n"));

        Assert.Equal(3, worktrees.Count);
        Assert.Equal(Path.GetFullPath("D:/Code/repo"), worktrees[0].Path);
        Assert.Equal("main", worktrees[0].BranchName);
        Assert.False(worktrees[0].IsLocked);
        Assert.Equal("feature/login", worktrees[1].BranchName);
        Assert.True(worktrees[1].IsLocked);
        Assert.True(worktrees[2].IsDetached);
        Assert.True(worktrees[2].IsPrunable);
        Assert.Equal(string.Empty, worktrees[2].BranchName);

        var detached = new WorktreeEntry(worktrees[2], isCurrent: false, isMain: false);
        Assert.Equal("detached at 3333333", detached.BranchText);
        Assert.Equal("missing", detached.Badges);
        Assert.False(detached.CanOpen);
        Assert.True(detached.CanRemove);
        var main = new WorktreeEntry(worktrees[0], isCurrent: true, isMain: true);
        Assert.False(main.CanOpen);
        Assert.False(main.CanRemove);
    }

    [Fact]
    public async Task Refs_snapshot_lists_worktrees_and_marks_their_branches()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var folder = repo.Path + "-wt";
        try
        {
            ProcessResult added = await repo.Git.AddWorktreeAsync(repo.Path, folder, "feature/x", createBranch: true, startPoint: null);
            Assert.True(added.Success, added.Combined);

            RefsSnapshot refs = await repo.Git.GetRefsSnapshotAsync(repo.Path);
            Assert.Null(refs.Error);
            Assert.Equal(2, refs.Worktrees.Count);
            Assert.Equal(Path.GetFullPath(folder), refs.Worktrees[1].Path);

            BranchRef feature = refs.Branches.Single(b => b.Name == "feature/x");
            Assert.True(feature.IsInOtherWorktree);
            Assert.False(feature.CanDelete);
            Assert.False(feature.CanAddWorktree);
            BranchRef current = refs.Branches.Single(b => b.IsCheckedOut);
            Assert.False(current.IsInOtherWorktree);

            // Deleting the folder outside git leaves a prunable entry until it is pruned.
            Directory.Delete(folder, recursive: true);
            RefsSnapshot stale = await repo.Git.GetRefsSnapshotAsync(repo.Path);
            Assert.True(stale.Worktrees[1].IsPrunable);
            ProcessResult pruned = await repo.Git.PruneWorktreesAsync(repo.Path);
            Assert.True(pruned.Success, pruned.Combined);
            RefsSnapshot after = await repo.Git.GetRefsSnapshotAsync(repo.Path);
            Assert.Single(after.Worktrees);
            Assert.True(after.Branches.Single(b => b.Name == "feature/x").CanDelete);
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    [AvaloniaFact]
    public async Task Worktrees_are_created_opened_and_removed_from_the_sidebar()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        ProcessResult branched = await repo.Git.CreateBranchAsync(repo.Path, "feature/login", null, checkout: false);
        Assert.True(branched.Success, branched.Combined);

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-worktrees.json"));
        var dialogs = new FakeDialogService();
        var vm = new MainWindowViewModel(settings, dialogs);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel main = vm.Repository!;
        Assert.False(main.IsLinkedWorktree);
        Assert.False(main.HasLinkedWorktrees);

        var folder = main.SuggestWorktreePath("feature/login");
        Assert.Equal(Path.GetFileName(repo.Path) + "-feature-login", Path.GetFileName(folder));
        try
        {
            // Create a worktree for the existing branch; the dialog accepts the suggested folder
            // and the confirmation opens it in a new tab.
            BranchRef feature = main.Branches.Single(b => b.Name == "feature/login");
            Assert.True(feature.CanAddWorktree);
            dialogs.PromptResponses.Enqueue(folder);
            main.AddWorktreeForBranchCommand.Execute(feature);
            await WaitForAsync(() => vm.Repositories.Count == 2 && !main.IsBusy);
            Assert.Empty(dialogs.Messages);

            RepositoryViewModel linked = vm.Repository!;
            Assert.NotSame(main, linked);
            Assert.True(linked.IsLinkedWorktree);
            await WaitForAsync(() => linked.CurrentBranch == "feature/login" && linked.Worktrees.Count == 2);
            Assert.Equal(Path.GetFullPath(repo.Path), linked.MainWorktreePath);
            Assert.True(linked.Worktrees[1].IsCurrent);

            // The main tab lists both worktrees and marks the branch.
            Assert.Equal(2, main.Worktrees.Count);
            Assert.True(main.Worktrees[0].IsCurrent);
            Assert.True(main.Worktrees[1].CanOpen);
            Assert.True(main.Branches.Single(b => b.Name == "feature/login").IsInOtherWorktree);

            // Checking out that branch in the main tab switches to its worktree instead of failing.
            vm.Repository = main;
            main.CheckoutBranchCommand.Execute(main.Branches.Single(b => b.Name == "feature/login"));
            await WaitForAsync(() => ReferenceEquals(vm.Repository, linked));
            Assert.Empty(dialogs.Messages);
            Assert.NotEqual("feature/login", main.CurrentBranch);

            Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory(ArtifactDirectory);
            window.CaptureRenderedFrame()!.Save(Path.Combine(ArtifactDirectory, "worktrees-linked.png"), PngBitmapEncoderOptions.Default);

            vm.Repository = main;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()!.Save(Path.Combine(ArtifactDirectory, "worktrees.png"), PngBitmapEncoderOptions.Default);

            // Removing it deletes the folder, closes its tab and keeps the branch.
            main.RemoveWorktreeCommand.Execute(main.Worktrees[1]);
            await WaitForAsync(() => vm.Repositories.Count == 1 && main.Worktrees.Count == 1 && !main.IsBusy);
            Assert.False(Directory.Exists(folder));
            Assert.True(main.Branches.Single(b => b.Name == "feature/login").CanDelete);
            Assert.Empty(dialogs.Messages);
        }
        finally
        {
            window.Close();
            DeleteFolder(folder);
        }
    }

    private static void DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(folder, recursive: true);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
