using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.App.Views;
using Avgit.Core.Diff;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>Viewing a stash's changed files and diff in the history's right-hand panes.</summary>
public class StashViewTests
{
    private static readonly string ArtifactDirectory =
        Path.Combine(Path.GetTempPath(), "avgit-headless");

    [Fact]
    public async Task Stash_changes_and_diff_include_untracked_files()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var head = await repo.Git.GetCurrentRevisionAsync(repo.Path);
        await repo.AddWorkingChangesAsync();
        Assert.True((await repo.Git.StashSaveAsync(repo.Path, "both kinds", includeUntracked: true)).Success);

        StashRecord stash = Assert.Single(await repo.Git.GetStashesAsync(repo.Path));
        Assert.Equal("stash@{0}", stash.Id);
        Assert.EndsWith(": both kinds", stash.Text, StringComparison.Ordinal);
        Assert.Equal(40, stash.Sha.Length);
        Assert.StartsWith(stash.BaseShortSha, head, StringComparison.Ordinal);

        IReadOnlyList<ChangedFile> files = await repo.Git.GetStashChangesAsync(repo.Path, stash.Sha);
        Assert.Contains(files, f => f.RelativePath == "readme.txt" && f.Status == PathState.Edited);
        Assert.Contains(files, f => f.RelativePath == "untracked.txt" && f.Status == PathState.Untracked);
        Assert.Equal(2, files.Count);

        var raw = await repo.Git.GetDiffAsync(repo.Path, new DiffSpec
        {
            Kind = DiffRequestKind.Stash,
            Revision = stash.Sha,
            Files = ["untracked.txt"]
        });
        List<FileDiff> parsed = UnifiedDiffParser.Parse(raw,
            new DiffParseOptions { Setup = new DiffSetup { Subject = DiffSubject.StashSnapshot } });
        FileDiff untracked = Assert.Single(parsed);
        Assert.Equal(PathState.New, untracked.State);
        Assert.Contains("+untracked", raw, StringComparison.Ordinal);

        var tracked = await repo.Git.GetDiffAsync(repo.Path, new DiffSpec
        {
            Kind = DiffRequestKind.Stash,
            Revision = stash.Sha,
            Files = ["readme.txt"]
        });
        Assert.Contains("+working change", tracked, StringComparison.Ordinal);
        Assert.DoesNotContain("untracked", tracked, StringComparison.Ordinal);

        // Without paths the whole stash is one diff, untracked files included.
        var whole = await repo.Git.GetDiffAsync(repo.Path, new DiffSpec { Kind = DiffRequestKind.Stash, Revision = stash.Sha });
        Assert.Contains("b/readme.txt", whole, StringComparison.Ordinal);
        Assert.Contains("b/untracked.txt", whole, StringComparison.Ordinal);
        Assert.Null(repo.Git.LastError);
    }

    [AvaloniaFact]
    public async Task A_stash_of_tracked_changes_shows_only_those_files()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "tracked only\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "program.cs"), "class Program { int x; }\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "left-behind.txt"), "not stashed\n");
        Assert.True((await repo.Git.StashSaveAsync(repo.Path, "tracked", includeUntracked: false)).Success);

        using RepositoryViewModel vm = await OpenAsync(repo, repo.Git, "settings-stash-tracked.json");
        StashEntry stash = Assert.Single(vm.Stashes);
        Assert.Equal("tracked", stash.Title);
        Assert.StartsWith("On ", stash.BaseText, StringComparison.Ordinal);
        Assert.EndsWith(" at " + vm.LogRows[0].Row.ShortId, stash.BaseText, StringComparison.Ordinal);

        vm.SelectedStash = stash;
        await WaitForStashAsync(vm, stash, "program.cs");

        Assert.True(vm.IsViewingStash);
        Assert.False(vm.IsShowingCommitDetails);
        Assert.Equal(["program.cs", "readme.txt"], vm.CommitFiles.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.All(vm.CommitFiles, f => Assert.Equal(PathState.Edited, f.Status));
        Assert.Contains(vm.CommitDiff.DisplayItems.OfType<DiffLineItem>(), l => l.Text.Contains("int x", StringComparison.Ordinal));

        vm.SelectedCommitFile = vm.CommitFiles.First(f => f.RelativePath == "readme.txt");
        await WaitForAsync(() => vm.CommitDiff.Title == $"stash {stash.ShortSha}  readme.txt"
            && vm.CommitDiff.DisplayItems.OfType<DiffLineItem>().Any(l => l.Text.Contains("tracked only", StringComparison.Ordinal)));
    }

    [AvaloniaFact]
    public async Task Clicking_a_stash_shows_it_and_clicking_a_commit_returns_to_the_commit()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await repo.AddWorkingChangesAsync();
        Assert.True((await repo.Git.StashSaveAsync(repo.Path, "Try a new layout", includeUntracked: true)).Success);

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-stash-click.json"));
        settings.Settings.AutoRefresh = false;
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        Dispatcher.UIThread.RunJobs();
        RepositoryViewModel repository = vm.Repository!;
        await WaitForAsync(() => repository.CommitInfo?.Sha == repository.LogRows[0].Row.CommitId
            && repository.Stashes.Count == 1);
        Assert.Empty(repository.UnstagedFiles);

        // Clicking a stash from File Status brings the History tab forward.
        TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "MainTabs");
        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        StashEntry stash = repository.Stashes[0];
        Click(window, StashRow(window, 0), new Point(30, 10));
        await WaitForStashAsync(repository, stash, "readme.txt");
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.Same(stash, repository.SelectedStash);
        Assert.Contains(repository.CommitFiles, f => f.RelativePath == "readme.txt" && f.Status == PathState.Edited);
        Assert.Contains(repository.CommitFiles, f => f.RelativePath == "untracked.txt" && f.StatusText == "?");

        // One click only shows it: nothing was applied.
        await repository.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(repository.UnstagedFiles);
        Assert.True(repository.IsViewingStash);

        Control header = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == "StashHeader");
        await WaitForAsync(() => header.IsEffectivelyVisible);
        TextBlock[] texts = header.GetVisualDescendants().OfType<TextBlock>().ToArray();
        Assert.Contains(texts, t => t.Text == "Try a new layout");
        Assert.Contains(texts, t => t.Text == "stash@{0}");
        Assert.Contains(texts, t => t.Text == stash.BaseText && t.Text!.EndsWith(" at " + repository.LogRows[0].Row.ShortId, StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Text == stash.Date);

        repository.SelectedCommitFile = repository.CommitFiles.First(f => f.RelativePath == "untracked.txt");
        await WaitForAsync(() => repository.CommitDiff.Title == $"stash {stash.ShortSha}  untracked.txt"
            && repository.CommitDiff.DisplayItems.OfType<DiffLineItem>().Any(l => l.Text.Contains("content", StringComparison.Ordinal)));
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ArtifactDirectory);
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactDirectory, "stash-view.png"), PngBitmapEncoderOptions.Default);

        // Clicking the commit the history still highlights shows that commit again.
        ListBox commits = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "CommitList");
        Assert.Contains(repository.LogRows[0], commits.SelectedItems!.OfType<CommitRowViewModel>());
        Click(window, (Control)commits.ContainerFromIndex(0)!, new Point(40, 10));
        await WaitForAsync(() => !repository.IsViewingStash
            && repository.IsShowingCommitDetails
            && repository.CommitInfo?.Sha == repository.LogRows[0].Row.CommitId
            && repository.CommitDiff.Title.StartsWith(repository.LogRows[0].Row.ShortId + "  ", StringComparison.Ordinal));
        Assert.Null(repository.SelectedStash);
        Assert.False(header.IsEffectivelyVisible);

        // Another commit works the same way after viewing the stash again.
        Click(window, StashRow(window, 0), new Point(30, 10));
        await WaitForStashAsync(repository, repository.Stashes[0], "readme.txt");
        Click(window, (Control)commits.ContainerFromIndex(1)!, new Point(40, 10));
        await WaitForAsync(() => !repository.IsViewingStash
            && repository.CommitInfo?.Sha == repository.LogRows[1].Row.CommitId);

        // A double-click still applies the stash; its first click shows it.
        Control row = StashRow(window, 0);
        Click(window, row, new Point(30, 10));
        Click(window, row, new Point(30, 10));
        await WaitForAsync(() => repository.UnstagedFiles.Any(f => f.RelativePath == "readme.txt")
            && repository.UnstagedFiles.Any(f => f.RelativePath == "untracked.txt"));
        Assert.True(repository.IsViewingStash);
        Assert.Single(repository.Stashes);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_stashed_image_uses_the_image_comparison()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var logo = Path.Combine(repo.Path, "logo.png");
        ImageDiffTests.WritePng(logo, 40, 30, 0xFFD03030);
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "Add logo");
        ImageDiffTests.WritePng(logo, 50, 50, 0xFF3060D0);
        ImageDiffTests.WritePng(Path.Combine(repo.Path, "new.png"), 16, 16, 0xFFE0C020);
        Assert.True((await repo.Git.StashSaveAsync(repo.Path, "images", includeUntracked: true)).Success);

        using RepositoryViewModel vm = await OpenAsync(repo, repo.Git, "settings-stash-images.json");
        StashEntry stash = vm.Stashes[0];
        vm.SelectedStash = stash;
        // The newest commit added logo.png too; wait for the stash's diff to replace its one.
        await WaitForStashAsync(vm, stash, "logo.png");
        Assert.Equal(PathState.Untracked, vm.CommitFiles.First(f => f.RelativePath == "new.png").Status);

        DiffImageItem changed = await WaitForImageAsync(vm.CommitDiff, "logo.png");
        Assert.Equal(["Old File", "New File"], changed.Sides.Select(s => s.Label));
        Assert.Equal(ImageOrigin.Object($"{stash.Sha}^1:logo.png"), changed.Sides[0].Origin);
        Assert.Equal(ImageOrigin.Object($"{stash.Sha}:logo.png"), changed.Sides[1].Origin);
        Assert.Equal(new PixelSize(40, 30), changed.Sides[0].Image!.PixelSize);
        Assert.Equal(new PixelSize(50, 50), changed.Sides[1].Image!.PixelSize);

        // An untracked image is only in the stash's third parent.
        vm.SelectedCommitFile = vm.CommitFiles.First(f => f.RelativePath == "new.png");
        await WaitForStashAsync(vm, stash, "new.png");
        DiffImageItem added = await WaitForImageAsync(vm.CommitDiff, "new.png");
        DiffImageSide only = Assert.Single(added.Sides);
        Assert.Equal(ImageOrigin.Object($"{stash.Sha}^3:new.png"), only.Origin);
        Assert.Equal(new PixelSize(16, 16), only.Image!.PixelSize);
    }

    [AvaloniaFact]
    public async Task The_viewed_stash_is_followed_by_sha_and_header_buttons_apply_pop_and_drop()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var first = await StashFileAsync(repo, "readme.txt", "first\n", "first stash");
        await StashFileAsync(repo, "program.cs", "second\n", "second stash");

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-stash-follow.json"));
        settings.Settings.AutoRefresh = false;
        var dialogs = new FakeDialogService();
        var vm = new MainWindowViewModel(settings, dialogs);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        Dispatcher.UIThread.RunJobs();
        RepositoryViewModel repository = vm.Repository!;
        await WaitForAsync(() => repository.Stashes.Count == 2 && repository.CommitInfo is not null);

        StashEntry viewed = repository.Stashes.First(s => s.Sha == first);
        Assert.Equal("stash@{1}", viewed.Name);
        Click(window, StashRow(window, 1), new Point(30, 10));
        await WaitForStashAsync(repository, viewed, "readme.txt");

        // A new stash moves the viewed one to stash@{2}; a refresh keeps showing it.
        var third = await StashFileAsync(repo, "graph.txt", "third\n", "third stash");
        await repository.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        await WaitForAsync(() => repository.Stashes.Count == 3);
        Assert.True(repository.IsViewingStash);
        Assert.Equal(first, repository.ViewedStash!.Sha);
        Assert.Equal("stash@{2}", repository.ViewedStash.Name);
        Assert.Same(repository.ViewedStash, repository.SelectedStash);
        Assert.Equal(["readme.txt"], repository.CommitFiles.Select(f => f.RelativePath));
        ListBox stashList = StashList(window);
        Assert.Same(repository.ViewedStash, stashList.SelectedItem);

        // Apply keeps the stash and its view.
        Click(window, HeaderButton(window, "Apply"), new Point(8, 8));
        await WaitForAsync(() => repository.UnstagedFiles.Any(f => f.RelativePath == "readme.txt"));
        Assert.True(repository.IsViewingStash);
        Assert.Equal(3, repository.Stashes.Count);

        // Pop removes the stash and returns to the commit view.
        Click(window, StashRow(window, 0), new Point(30, 10));
        await WaitForStashAsync(repository, repository.Stashes.First(s => s.Sha == third), "graph.txt");
        Click(window, HeaderButton(window, "Pop"), new Point(8, 8));
        await WaitForAsync(() => !repository.IsViewingStash
            && repository.Stashes.Count == 2
            && repository.UnstagedFiles.Any(f => f.RelativePath == "graph.txt")
            && repository.CommitInfo?.Sha == repository.SelectedCommit?.Row.CommitId);
        Assert.DoesNotContain(repository.Stashes, s => s.Sha == third);

        // Drop asks first, then leaves the view as well.
        StashEntry toDrop = repository.Stashes.First(s => s.Sha == first);
        Click(window, StashRow(window, repository.Stashes.IndexOf(toDrop)), new Point(30, 10));
        await WaitForStashAsync(repository, toDrop, "readme.txt");
        Click(window, HeaderButton(window, "Drop"), new Point(8, 8));
        await WaitForAsync(() => !repository.IsViewingStash && repository.Stashes.Count == 1);
        Assert.Contains(dialogs.Confirmations, c => c.Title == "Drop stash");
        Assert.DoesNotContain(repository.Stashes, s => s.Sha == first);
        Assert.True(repository.IsShowingCommitDetails);
        Assert.Empty(dialogs.Messages);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Dropping_the_viewed_stash_elsewhere_leaves_the_stash_view_on_refresh()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await StashFileAsync(repo, "readme.txt", "dropped\n", "to drop");
        using RepositoryViewModel vm = await OpenAsync(repo, repo.Git, "settings-stash-dropped.json");
        vm.SelectedStash = vm.Stashes[0];
        await WaitForStashAsync(vm, vm.Stashes[0], "readme.txt");

        Assert.True((await repo.Git.StashDropAsync(repo.Path, "stash@{0}")).Success);
        await vm.RefreshAsync();
        await WaitForAsync(() => !vm.IsViewingStash
            && vm.CommitInfo?.Sha == vm.LogRows[0].Row.CommitId
            && vm.CommitDiff.Title.StartsWith(vm.LogRows[0].Row.ShortId + "  ", StringComparison.Ordinal));
        Assert.Empty(vm.Stashes);
        Assert.Null(vm.SelectedStash);
    }

    [AvaloniaFact]
    public async Task A_late_commit_load_does_not_overwrite_a_newer_stash_view()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await StashFileAsync(repo, "readme.txt", "stashed\n", "race");
        IGitEngine handler = GatedCommitHandler.Wrap(repo.Git);
        using RepositoryViewModel vm = await OpenAsync(repo, handler, "settings-stash-race-commit.json");
        CommitRowViewModel older = vm.LogRows[1];

        GatedCommitHandler.Gate gate = GatedCommitHandler.HoldCommitDetails(handler);
        vm.SetContextCommit(older);
        await WaitForAsync(() => gate.Entered);

        // The stash is clicked while the commit's details are still loading.
        StashEntry stash = vm.Stashes[0];
        vm.SelectedStash = stash;
        await WaitForStashAsync(vm, stash, "readme.txt");

        gate.Release();
        await WaitForAsync(() => gate.Completed);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsViewingStash);
        Assert.Equal(stash.Sha, vm.ViewedStash?.Sha);
        Assert.Equal(["readme.txt"], vm.CommitFiles.Select(f => f.RelativePath));
        Assert.Equal($"stash {stash.ShortSha}  readme.txt", vm.CommitDiff.Title);
        Assert.NotEqual(older.Row.CommitId, vm.CommitInfo?.Sha);
    }

    [AvaloniaFact]
    public async Task A_late_stash_load_does_not_overwrite_a_newer_commit_selection()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await StashFileAsync(repo, "readme.txt", "stashed\n", "race");
        IGitEngine handler = GatedCommitHandler.Wrap(repo.Git);
        using RepositoryViewModel vm = await OpenAsync(repo, handler, "settings-stash-race-stash.json");
        CommitRowViewModel older = vm.LogRows[1];

        GatedCommitHandler.Gate gate = GatedCommitHandler.HoldStashChanges(handler);
        vm.SelectedStash = vm.Stashes[0];
        await WaitForAsync(() => gate.Entered);

        // A commit is clicked while the stash's files are still loading.
        vm.SetContextCommit(older);
        await WaitForAsync(() => vm.CommitInfo?.Sha == older.Row.CommitId
            && vm.CommitDiff.Title.StartsWith(older.Row.ShortId + "  ", StringComparison.Ordinal));

        gate.Release();
        await WaitForAsync(() => gate.Completed);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsViewingStash);
        Assert.Null(vm.SelectedStash);
        Assert.Equal(older.Row.CommitId, vm.CommitInfo?.Sha);
        Assert.StartsWith(older.Row.ShortId + "  ", vm.CommitDiff.Title);
        Assert.DoesNotContain(vm.CommitFiles, f => f.Status == PathState.Untracked);
        Assert.Contains(vm.CommitFiles, f => f.RelativePath == "readme.txt");
    }

    /// <summary>Changes one file, stashes it and returns the new stash's SHA.</summary>
    private static async Task<string> StashFileAsync(TempRepo repo, string file, string content, string message)
    {
        await File.AppendAllTextAsync(Path.Combine(repo.Path, file), content);
        ProcessResult result = await repo.Git.StashSaveAsync(repo.Path, message, includeUntracked: true);
        Assert.True(result.Success, result.Combined);
        return (await repo.Git.GetStashesAsync(repo.Path))[0].Sha;
    }

    private static async Task<RepositoryViewModel> OpenAsync(TempRepo repo, IGitEngine handler, string settingsFile)
    {
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, settingsFile));
        settings.Settings.AutoRefresh = false;
        var vm = new RepositoryViewModel(handler, repo.Path, settings, new FakeDialogService());
        await vm.LoadAsync();
        await WaitForAsync(() => vm.CommitInfo is not null);
        return vm;
    }

    /// <summary>Waits until <paramref name="stash"/> is shown with <paramref name="file"/>'s diff.</summary>
    private static Task WaitForStashAsync(RepositoryViewModel vm, StashEntry stash, string file)
        => WaitForAsync(() => vm.IsViewingStash
            && vm.ViewedStash?.Sha == stash.Sha
            && vm.CommitFiles.Any(f => f.RelativePath == file)
            && vm.CommitDiff.Title == $"stash {stash.ShortSha}  {file}"
            && vm.CommitDiff.DisplayItems.Count > 0);

    private static ListBox StashList(Window window)
        => window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "StashList");

    private static Control StashRow(Window window, int index)
    {
        Control row = (Control)StashList(window).ContainerFromIndex(index)!;
        row.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        return row;
    }

    private static Button HeaderButton(Window window, string content)
        => window.GetVisualDescendants().OfType<Control>().First(c => c.Name == "StashHeader")
            .GetVisualDescendants().OfType<Button>().First(b => b.Content as string == content);

    /// <summary>A real left click at <paramref name="offset"/> inside <paramref name="target"/>.</summary>
    private static void Click(Window window, Control target, Point offset)
    {
        Point point = target.TranslatePoint(offset, window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<DiffImageItem> WaitForImageAsync(DiffViewModel diff, string fileName)
    {
        DiffImageItem? item = null;
        await WaitForAsync(() =>
        {
            item = diff.DisplayItems.OfType<DiffImageItem>().FirstOrDefault(i => i.File.FileName == fileName);
            return item is not null && item.Sides.All(s => s.HasImage);
        });
        return item!;
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
