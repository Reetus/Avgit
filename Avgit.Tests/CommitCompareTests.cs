using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.App.Views;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

public class CommitCompareTests
{
    private static readonly string ArtifactDirectory =
        Path.Combine(Path.GetTempPath(), "avgit-headless");

    [AvaloniaFact]
    public async Task Two_selected_commits_show_their_range_with_the_older_commit_as_base()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-compare.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        Dispatcher.UIThread.RunJobs();
        RepositoryViewModel repository = vm.Repository!;

        CommitRowViewModel newest = repository.LogRows.First(r => r.Row.Title == "Add graph file");
        CommitRowViewModel middle = repository.LogRows.First(r => r.Row.Title == "Add more lines");
        CommitRowViewModel oldest = repository.LogRows.First(r => r.Row.Title == "Initial commit");
        Assert.True(repository.LogRows.IndexOf(oldest) > repository.LogRows.IndexOf(middle),
            "the test needs non-adjacent rows");
        ListBox list = GetCommitList(window);

        // Click the newer commit first; the older side must still become the base.
        SelectCommits(list, newest, oldest);
        await WaitForAsync(() => repository.IsComparingCommits
            && repository.CompareBaseSha == oldest.Row.ShortId
            && repository.CompareTargetSha == newest.Row.ShortId
            && repository.CommitFiles.Count > 0
            && repository.CommitDiff.DisplayItems.Count > 0
            && repository.CommitDiff.Title.StartsWith(
                $"{oldest.Row.ShortId}..{newest.Row.ShortId}", StringComparison.Ordinal));

        Assert.Equal("Initial commit", repository.CompareBaseSubject);
        Assert.Equal("Add graph file", repository.CompareTargetSubject);
        Assert.Contains(repository.CommitFiles, f => f.RelativePath == "readme.txt");
        Assert.Contains(repository.CommitFiles, f => f.RelativePath == "graph.txt");
        Assert.StartsWith($"{oldest.Row.ShortId}..{newest.Row.ShortId}", repository.CommitDiff.Title);

        Directory.CreateDirectory(ArtifactDirectory);
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactDirectory, "compare-two-commits.png"), PngBitmapEncoderOptions.Default);

        // The click order must not matter: leave the comparison, then select the
        // same pair with the other click order.
        SelectCommits(list, middle);
        await WaitForAsync(() => !repository.IsComparingCommits && repository.CommitInfo?.Sha == middle.Row.CommitId);
        SelectCommits(list, oldest, newest);
        await WaitForAsync(() => repository.IsComparingCommits
            && repository.CompareBaseSha == oldest.Row.ShortId
            && repository.CompareTargetSha == newest.Row.ShortId
            && repository.CommitDiff.Title.StartsWith(
                $"{oldest.Row.ShortId}..{newest.Row.ShortId}", StringComparison.Ordinal));

        repository.SwapCompareCommand.Execute(null);
        await WaitForAsync(() => repository.CompareBaseSha == newest.Row.ShortId
            && repository.CompareTargetSha == oldest.Row.ShortId
            && repository.CommitDiff.Title.StartsWith(
                $"{newest.Row.ShortId}..{oldest.Row.ShortId}", StringComparison.Ordinal));

        WriteableBitmap? swappedFrame = window.CaptureRenderedFrame();
        Assert.NotNull(swappedFrame);
        swappedFrame!.Save(Path.Combine(ArtifactDirectory, "compare-two-commits-swapped.png"), PngBitmapEncoderOptions.Default);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Selecting_one_commit_again_restores_the_single_commit_view()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-compare-single.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        Dispatcher.UIThread.RunJobs();
        RepositoryViewModel repository = vm.Repository!;

        CommitRowViewModel newest = repository.LogRows[0];
        CommitRowViewModel oldest = repository.LogRows[2];
        ListBox list = GetCommitList(window);

        SelectCommits(list, newest, oldest);
        await WaitForAsync(() => repository.IsComparingCommits && repository.CommitDiff.DisplayItems.Count > 0);

        SelectCommits(list, oldest);
        await WaitForAsync(() => !repository.IsComparingCommits
            && repository.CommitInfo?.Sha == oldest.Row.CommitId
            && repository.CommitDiff.Title.StartsWith(oldest.Row.ShortId + "  ", StringComparison.Ordinal));

        Assert.Equal(oldest.Row.CommitId, repository.SelectedCommit?.Row.CommitId);
        Assert.Contains(repository.CommitFiles, f => f.RelativePath == "program.cs");
        Assert.DoesNotContain(repository.CommitFiles, f => f.RelativePath == "graph.txt");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Two_commits_on_different_branches_can_be_compared()
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
        await repo.CreateCommitAsync("side work", "side.txt", "side\n");
        Assert.True((await repo.Git.CheckoutAsync(repo.Path, main)).Success);
        await repo.CreateCommitAsync("main work", "main.txt", "main\n");

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-compare-branches.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;

        CommitRowViewModel mainRow = repository.LogRows.First(r => r.Row.Title == "main work");
        CommitRowViewModel sideRow = repository.LogRows.First(r => r.Row.Title == "side work");
        CommitRowViewModel expectedBase = repository.LogRows.IndexOf(mainRow) > repository.LogRows.IndexOf(sideRow)
            ? mainRow
            : sideRow;
        CommitRowViewModel expectedTarget = ReferenceEquals(expectedBase, mainRow) ? sideRow : mainRow;

        repository.SetContextCommit(expectedTarget);
        repository.SetSelectedCommits([expectedTarget, expectedBase]);
        await WaitForAsync(() => repository.IsComparingCommits
            && repository.CompareBaseSha == expectedBase.Row.ShortId
            && repository.CompareTargetSha == expectedTarget.Row.ShortId
            && repository.CommitFiles.Count > 0
            && repository.CommitDiff.DisplayItems.Count > 0
            && repository.CommitDiff.Title.StartsWith(
                $"{expectedBase.Row.ShortId}..{expectedTarget.Row.ShortId}", StringComparison.Ordinal));

        Assert.Contains(repository.CommitFiles, f => f.RelativePath == "side.txt");
        Assert.Contains(repository.CommitFiles, f => f.RelativePath == "main.txt");
        Assert.StartsWith($"{expectedBase.Row.ShortId}..{expectedTarget.Row.ShortId}", repository.CommitDiff.Title);
    }

    [AvaloniaFact]
    public async Task Three_selected_commits_show_the_active_commits_details()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-compare-three.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        Dispatcher.UIThread.RunJobs();
        RepositoryViewModel repository = vm.Repository!;

        CommitRowViewModel newest = repository.LogRows[0];
        CommitRowViewModel middle = repository.LogRows[1];
        CommitRowViewModel oldest = repository.LogRows[2];
        ListBox list = GetCommitList(window);

        SelectCommits(list, newest, oldest);
        await WaitForAsync(() => repository.IsComparingCommits && repository.CommitDiff.DisplayItems.Count > 0);

        // A third Ctrl+click leaves the comparison and shows the added commit.
        list.SelectedItems!.Add(middle);
        Dispatcher.UIThread.RunJobs();
        await WaitForAsync(() => !repository.IsComparingCommits
            && repository.SelectedCommits.Count == 3
            && repository.SelectedCommit == middle
            && repository.CommitInfo?.Sha == middle.Row.CommitId);
        Assert.Equal(middle.Row.CommitId, repository.CommitInfo?.Sha);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Comparing_with_a_stored_base_uses_the_same_comparison_view()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-compare-base.json"));
        settings.Settings.AutoRefresh = false;
        using var vm = new RepositoryViewModel(repo.Git, repo.Path, settings, new FakeDialogService());
        await vm.LoadAsync();

        CommitRowViewModel newest = vm.LogRows[0];
        CommitRowViewModel oldest = vm.LogRows[2];
        vm.SetContextCommit(oldest);
        await WaitForAsync(() => vm.CommitInfo?.Sha == oldest.Row.CommitId);

        vm.SetCompareBaseCommand.Execute(null);
        vm.SetContextCommit(newest);
        await WaitForAsync(() => vm.CommitInfo?.Sha == newest.Row.CommitId);
        vm.CompareWithBaseCommand.Execute(null);

        await WaitForAsync(() => vm.IsComparingCommits
            && vm.CompareBaseSha == oldest.Row.ShortId
            && vm.CompareTargetSha == newest.Row.ShortId
            && vm.CommitFiles.Count > 0
            && vm.CommitDiff.Title.StartsWith(
                $"{oldest.Row.ShortId}..{newest.Row.ShortId}", StringComparison.Ordinal));
        Assert.Contains(vm.CommitFiles, f => f.RelativePath == "readme.txt");
    }

    [AvaloniaFact]
    public async Task A_late_range_load_does_not_overwrite_a_newer_single_selection()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        IGitEngine handler = GatedCommitHandler.Wrap(repo.Git);
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-compare-race-single.json"));
        settings.Settings.AutoRefresh = false;
        using var vm = new RepositoryViewModel(handler, repo.Path, settings, new FakeDialogService());
        await vm.LoadAsync();

        CommitRowViewModel newest = vm.LogRows[0];
        CommitRowViewModel oldest = vm.LogRows[^1];
        CommitRowViewModel single = vm.LogRows[1];

        GatedCommitHandler.Gate gate = GatedCommitHandler.HoldRangeChanges(handler);
        vm.SetContextCommit(newest);
        vm.SetSelectedCommits([newest, oldest]);
        await WaitForAsync(() => gate.Entered);

        // The user goes back to one commit while the comparison is still loading.
        vm.SetContextCommit(single);
        vm.SetSelectedCommits([single]);
        await WaitForAsync(() => vm.CommitInfo?.Sha == single.Row.CommitId);

        gate.Release();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(250);

        Assert.False(vm.IsComparingCommits);
        Assert.Equal(single.Row.CommitId, vm.CommitInfo?.Sha);
        Assert.Equal(single.Row.CommitId, vm.SelectedCommit?.Row.CommitId);
        Assert.StartsWith(single.Row.ShortId + "  ", vm.CommitDiff.Title);
    }

    [AvaloniaFact]
    public async Task A_late_single_load_does_not_overwrite_a_newer_comparison()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        IGitEngine handler = GatedCommitHandler.Wrap(repo.Git);
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-compare-race-pair.json"));
        settings.Settings.AutoRefresh = false;
        using var vm = new RepositoryViewModel(handler, repo.Path, settings, new FakeDialogService());
        await vm.LoadAsync();

        CommitRowViewModel newest = vm.LogRows[0];
        CommitRowViewModel oldest = vm.LogRows[^1];
        CommitRowViewModel single = vm.LogRows[1];

        GatedCommitHandler.Gate gate = GatedCommitHandler.HoldCommitDetails(handler);
        vm.SetContextCommit(single);
        await WaitForAsync(() => gate.Entered);

        // Two commits are selected before the single-commit lookup finishes.
        vm.SetSelectedCommits([newest, oldest]);
        await WaitForAsync(() => vm.IsComparingCommits
            && vm.CommitDiff.DisplayItems.Count > 0
            && vm.CommitDiff.Title.StartsWith(
                $"{oldest.Row.ShortId}..{newest.Row.ShortId}", StringComparison.Ordinal));

        gate.Release();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(250);

        Assert.True(vm.IsComparingCommits);
        Assert.Equal(oldest.Row.ShortId, vm.CompareBaseSha);
        Assert.Equal(newest.Row.ShortId, vm.CompareTargetSha);
        Assert.StartsWith($"{oldest.Row.ShortId}..{newest.Row.ShortId}", vm.CommitDiff.Title);
    }

    [AvaloniaFact]
    public async Task Reverting_two_selected_commits_still_works()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-compare-revert.json"));
        settings.Settings.AutoRefresh = false;
        using var vm = new RepositoryViewModel(repo.Git, repo.Path, settings, new FakeDialogService());
        await vm.LoadAsync();

        CommitRowViewModel newest = vm.LogRows[0];
        CommitRowViewModel older = vm.LogRows[1];
        vm.SetContextCommit(newest);
        vm.SetSelectedCommits([newest, older]);
        await WaitForAsync(() => vm.IsComparingCommits && vm.CommitFiles.Count > 0);
        Assert.Equal(2, vm.SelectedCommits.Count);

        // Setting a compare base only records it; the comparison stays on screen.
        vm.SetCompareBaseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsComparingCommits);
        Assert.Equal(older.Row.ShortId, vm.CompareBaseSha);

        vm.RevertCommitCommand.Execute(null);
        await WaitForAsync(() => !vm.IsBusy
            && vm.LogRows.Count(r => r.Row.Title.StartsWith("Revert \"", StringComparison.Ordinal)) >= 2);
    }

    [AvaloniaFact]
    public async Task Refreshing_keeps_the_two_commit_comparison()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-compare-refresh.json"));
        settings.Settings.AutoRefresh = false;
        using var vm = new RepositoryViewModel(repo.Git, repo.Path, settings, new FakeDialogService());
        await vm.LoadAsync();

        CommitRowViewModel newest = vm.LogRows[0];
        CommitRowViewModel oldest = vm.LogRows[2];
        vm.SetContextCommit(newest);
        vm.SetSelectedCommits([newest, oldest]);
        await WaitForAsync(() => vm.IsComparingCommits
            && vm.CompareBaseSha == oldest.Row.ShortId
            && vm.CommitDiff.DisplayItems.Count > 0
            && vm.CommitDiff.Title.StartsWith(
                $"{oldest.Row.ShortId}..{newest.Row.ShortId}", StringComparison.Ordinal));

        var diffCount = vm.CommitDiff.DisplayItems.Count;
        var fileCount = vm.CommitFiles.Count;
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsComparingCommits);
        Assert.Equal(oldest.Row.ShortId, vm.CompareBaseSha);
        Assert.Equal(newest.Row.ShortId, vm.CompareTargetSha);
        Assert.Equal(fileCount, vm.CommitFiles.Count);
        Assert.Equal(diffCount, vm.CommitDiff.DisplayItems.Count);
        Assert.Equal(2, vm.SelectedCommits.Count);
    }

    private static ListBox GetCommitList(Window window)
    {
        ListBox? list = window.GetVisualDescendants().OfType<ListBox>()
            .FirstOrDefault(l => l.Name == "CommitList");
        Assert.NotNull(list);
        return list!;
    }

    private static void SelectCommits(ListBox list, params CommitRowViewModel[] rows)
    {
        list.SelectedItems!.Clear();
        foreach (CommitRowViewModel row in rows)
        {
            list.SelectedItems.Add(row);
        }

        Dispatcher.UIThread.RunJobs();
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

/// <summary>Forwards to a real handler but can hold one range or commit lookup until released.</summary>
public class GatedCommitHandler : DispatchProxy
{
    private IGitEngine _inner = null!;
    private Gate? _rangeGate;
    private Gate? _commitGate;

    public sealed class Gate
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Entered;
        public Task WaitAsync() => _release.Task;
        public void Release() => _release.TrySetResult();
    }

    public static IGitEngine Wrap(IGitEngine inner)
    {
        IGitEngine proxy = Create<IGitEngine, GatedCommitHandler>();
        ((GatedCommitHandler)(object)proxy)._inner = inner;
        return proxy;
    }

    /// <summary>Holds the next GetRangeChangesAsync call until <see cref="Gate.Release"/>.</summary>
    public static Gate HoldRangeChanges(IGitEngine proxy)
    {
        var gate = new Gate();
        ((GatedCommitHandler)(object)proxy)._rangeGate = gate;
        return gate;
    }

    /// <summary>Holds the next GetCommitAsync call until <see cref="Gate.Release"/>.</summary>
    public static Gate HoldCommitDetails(IGitEngine proxy)
    {
        var gate = new Gate();
        ((GatedCommitHandler)(object)proxy)._commitGate = gate;
        return gate;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IGitEngine.GetRangeChangesAsync) && _rangeGate is { } rangeGate)
        {
            _rangeGate = null;
            return GatedRangeAsync(rangeGate, (string)args![0]!, (string)args[1]!, (string)args[2]!,
                (CancellationToken)args[3]!);
        }

        if (targetMethod.Name == nameof(IGitEngine.GetCommitAsync) && _commitGate is { } commitGate)
        {
            _commitGate = null;
            return GatedCommitAsync(commitGate, (string)args![0]!, (string)args[1]!, (CancellationToken)args[2]!);
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private async Task<IReadOnlyList<ChangedFile>> GatedRangeAsync(Gate gate, string path, string from,
        string to, CancellationToken ct)
    {
        gate.Entered = true;
        await gate.WaitAsync();
        return await _inner.GetRangeChangesAsync(path, from, to, ct);
    }

    private async Task<CommitResult> GatedCommitAsync(Gate gate, string path, string revision, CancellationToken ct)
    {
        gate.Entered = true;
        await gate.WaitAsync();
        return await _inner.GetCommitAsync(path, revision, ct);
    }
}
