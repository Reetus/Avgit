using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>Tab restore, paging, caching, untracked folders and watcher behaviour for large repositories.</summary>
public class PerformanceFeatureTests
{
    private static string NewSettingsPath()
        => Path.Combine(Path.GetTempPath(), "avgit-perf-tests", Guid.NewGuid().ToString("N"), "settings.json");

    private static AppSettingsService NewSettings(bool autoRefresh = false)
    {
        var settings = new AppSettingsService(NewSettingsPath());
        settings.Settings.AutoRefresh = autoRefresh;
        settings.Settings.MaintainCommitGraph = false;
        return settings;
    }

    [AvaloniaFact]
    public async Task Restored_tabs_appear_at_once_and_only_the_active_one_loads()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo a = await TempRepo.CreateAsync();
        using TempRepo b = await TempRepo.CreateAsync();
        using TempRepo c = await TempRepo.CreateAsync();
        AppSettingsService settings = NewSettings();
        settings.Settings.OpenRepositories = [a.Path, b.Path, c.Path];
        settings.Settings.ActiveRepository = b.Path;

        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        await vm.RestoreOpenTabsAsync();

        Assert.Equal(3, vm.Repositories.Count);
        Assert.Equal(b.Path, vm.Repository?.Path);
        Assert.True(vm.Repository!.IsLoaded);
        Assert.False(vm.Repositories[0].IsLoaded);
        Assert.False(vm.Repositories[2].IsLoaded);

        // Showing another tab loads it then, and it becomes the remembered tab.
        vm.Repository = vm.Repositories[2];
        await WaitForAsync(() => vm.Repositories[2].IsLoaded && vm.Repositories[2].LogRows.Count > 0);
        Assert.Equal(c.Path, settings.Settings.ActiveRepository);
        Assert.False(vm.Repositories[0].IsLoaded);

        vm.Repositories.ToList().ForEach(r => r.Dispose());
    }

    [AvaloniaFact]
    public async Task Load_more_appends_pages_with_a_continuous_graph()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        await ImportLinearHistoryAsync(repo, 1_150);

        using var vm = new RepositoryViewModel(repo.Git, repo.Path, NewSettings(), new FakeDialogService());
        await vm.LoadAsync();
        Assert.Equal(500, vm.LogRows.Count);

        vm.LoadMoreLogCommand.Execute(null);
        await WaitForAsync(() => vm.LogRows.Count == 1000 && !vm.IsLoadingHistory);
        vm.LoadMoreLogCommand.Execute(null);
        await WaitForAsync(() => vm.LogRows.Count == 1150 && !vm.IsLoadingHistory);

        var expected = (await repo.Git.RunAsync(repo.Path, ["log", "--topo-order", "--format=%H", "--all"]))
            .Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, vm.LogRows.Select(r => r.Row.CommitId));
        // Each appended page continues the lanes from the last row of the previous page,
        // exactly as if all rows had been laid out at once.
        GraphTests.AssertRowsJoin(vm.LogRows[499].Graph, vm.LogRows[500].Graph);
        GraphTests.AssertRowsJoin(vm.LogRows[999].Graph, vm.LogRows[1000].Graph);
        List<HistoryRow> whole = [.. vm.LogRows.Select(r => GraphTests.CopyRow(r.Row))];
        Avgit.Core.Graph.CommitGraphBuilder.Layout(whole);
        for (var i = 0; i < whole.Count; i++)
        {
            GraphTests.AssertSameLayout(whole[i].Graph, vm.LogRows[i].Graph);
        }

        // A later full reload keeps all loaded commits.
        await vm.RefreshAsync();
        Assert.Equal(1150, vm.LogRows.Count);
    }

    [AvaloniaFact]
    public async Task Reopening_shows_cached_history_until_the_live_load_finishes()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        AppSettingsService settings = NewSettings();
        using (var first = new RepositoryViewModel(repo.Git, repo.Path, settings, new FakeDialogService()))
        {
            await first.LoadAsync();
            Assert.False(first.IsShowingCachedHistory);
        }
        await WaitForAsync(() => Directory.Exists(Path.Combine(Path.GetDirectoryName(settings.SettingsPath)!, "cache"))
                                 && Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(settings.SettingsPath)!, "cache"), "*.json").Any());

        // Hold git log so the cached rows are observable before the live ones.
        IGitEngine handler = GatedLogHandler.Wrap(repo.Git);
        GatedLogHandler.LogGate gate = GatedLogHandler.Gate(handler);
        using var second = new RepositoryViewModel(handler, repo.Path, settings, new FakeDialogService());
        Task load = second.LoadAsync();
        await WaitForAsync(() => gate.Entered);
        Assert.True(second.IsShowingCachedHistory);
        Assert.Equal(3, second.LogRows.Count);
        Assert.Contains("cached", second.LogCountText);

        gate.Release();
        await load;
        Assert.False(second.IsShowingCachedHistory);
        Assert.Equal(3, second.LogRows.Count);
    }

    [AvaloniaFact]
    public async Task Untracked_folders_can_be_listed_as_one_entry()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        Directory.CreateDirectory(Path.Combine(repo.Path, "newdir", "nested"));
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "newdir", "a.txt"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "newdir", "nested", "b.txt"), "b\n");

        AppSettingsService settings = NewSettings();
        settings.Settings.ShowFilesInUntrackedFolders = false;
        using var vm = new RepositoryViewModel(repo.Git, repo.Path, settings, new FakeDialogService());
        await vm.LoadAsync();

        ChangedFile folder = Assert.Single(vm.UnstagedFiles);
        Assert.Equal("newdir/", folder.RelativePath);
        vm.SelectedUnstagedFile = folder;
        await WaitForAsync(() => vm.WorkingDiff.EmptyMessage.Contains("Untracked folder with 2 file(s)"));

        vm.StageSelectedCommand.Execute(null);
        await WaitForAsync(() => vm.StagedFiles.Count == 2 && !vm.IsBusy);
        Assert.Contains(vm.StagedFiles, f => f.RelativePath == "newdir/nested/b.txt");
    }

    [AvaloniaFact]
    public async Task Inactive_tabs_refresh_when_shown_again()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        using var vm = new RepositoryViewModel(repo.Git, repo.Path, NewSettings(autoRefresh: true), new FakeDialogService());
        await vm.LoadAsync();
        Assert.Empty(vm.UnstagedFiles);

        vm.SetActive(false);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "while-hidden.txt"), "x\n");
        await PumpAsync(2000);
        Assert.Empty(vm.UnstagedFiles);

        vm.SetActive(true);
        await WaitForAsync(() => vm.UnstagedFiles.Any(f => f.RelativePath == "while-hidden.txt"));
    }

    [AvaloniaFact]
    public async Task Large_diffs_only_create_controls_for_visible_lines()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        // A change every 10 lines gives ~300 separate hunks (~2,700 diff rows); one huge
        // new file would instead hit the parser's collapse and per-hunk limits.
        using TempRepo repo = await TempRepo.CreateAsync();
        var bigPath = Path.Combine(repo.Path, "big.txt");
        await File.WriteAllTextAsync(bigPath, string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"line {i}")) + "\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "big file");
        await File.WriteAllTextAsync(bigPath, string.Join("\n", Enumerable.Range(1, 3000)
            .Select(i => i % 10 == 0 ? $"changed {i}" : $"line {i}")) + "\n");

        AppSettingsService settings = NewSettings();
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        var window = new Avgit.App.Views.MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;

        TabControl tabs = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Avalonia.Controls.TabControl>().First(t => t.Items.Count == 2);
        tabs.SelectedIndex = 1;
        repository.SelectedUnstagedFile = repository.UnstagedFiles.First(f => f.RelativePath == "big.txt");
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>().Count() >= 2000);
        await PumpAsync(300);

        var realized = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .Count(p => p.DataContext is DiffLineItem && p.IsVisible);
        Assert.InRange(realized, 1, 200);

        window.Close();
        vm.Repositories.ToList().ForEach(r => r.Dispose());
    }

    [Fact]
    public async Task Commit_graph_is_written_once_when_missing()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var graph = Path.Combine(GitRepoHandler.GetCommonGitDir(repo.Path), "objects", "info", "commit-graph");
        if (File.Exists(graph))
        {
            File.Delete(graph);
        }

        Assert.True(await repo.Git.EnsureCommitGraphAsync(repo.Path));
        Assert.True(File.Exists(graph));
        Assert.False(await repo.Git.EnsureCommitGraphAsync(repo.Path));
    }

    [Fact]
    public async Task Commit_snapshot_reads_details_tags_and_files_in_one_call()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "renamed-source.txt"), "same content\nline two\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "add source");
        await repo.Git.RunAsync(repo.Path, ["mv", "renamed-source.txt", "renamed target.txt"]);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "graph.txt"), "changed\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "Rename and edit\n\nBody line.");
        await repo.Git.CreateTagAsync(repo.Path, "v-annotated", null, "annotated tag");
        await repo.Git.CreateTagAsync(repo.Path, "v-light", null, null);

        CommitResult result = await repo.Git.GetCommitAsync(repo.Path, "HEAD");
        Assert.Null(result.Error);
        CommitSnapshot commit = result.Commit!;
        Assert.Equal("Rename and edit\n\nBody line.", commit.Details.Message?.Replace("\r\n", "\n"));
        Assert.NotNull(commit.Details.Tags);
        Assert.Equal(["v-annotated", "v-light"], commit.Details.Tags);
        Assert.Contains(commit.Files, f => f.Status == PathState.Moved
                                           && f.RelativePath == "renamed target.txt"
                                           && f.OldPath == "renamed-source.txt");
        Assert.Contains(commit.Files, f => f.Status == PathState.Edited && f.RelativePath == "graph.txt");

        // Root commit: every file is added.
        var root = (await repo.Git.RunAsync(repo.Path, ["rev-list", "--max-parents=0", "HEAD"])).Output.Trim();
        CommitSnapshot rootCommit = (await repo.Git.GetCommitAsync(repo.Path, root)).Commit!;
        Assert.All(rootCommit.Files, f => Assert.Equal(PathState.New, f.Status));
        Assert.Empty(rootCommit.Details.ParentIds!);
    }

    /// <summary>Creates a linear history of <paramref name="count"/> commits in one fast-import run.</summary>
    private static async Task ImportLinearHistoryAsync(TempRepo repo, int count)
    {
        var stream = new StringBuilder();
        var time = 1_600_000_000L;
        for (var i = 1; i <= count; i++)
        {
            var message = $"commit {i}";
            var content = $"line {i}\n";
            stream.Append("commit refs/heads/main\n");
            stream.Append($"mark :{i}\n");
            stream.Append($"committer Test User <test@example.com> {time + i * 60} +0000\n");
            stream.Append($"data {Encoding.UTF8.GetByteCount(message)}\n{message}\n");
            if (i > 1)
            {
                stream.Append($"from :{i - 1}\n");
            }

            stream.Append($"M 100644 inline file.txt\ndata {Encoding.UTF8.GetByteCount(content)}\n{content}\n");
        }

        ProcessResult import = await repo.Git.RunAsync(repo.Path, ["fast-import", "--quiet"], stream.ToString());
        Assert.True(import.Success, import.Combined);
        ProcessResult checkout = await repo.Git.RunAsync(repo.Path, ["checkout", "-q", "-f", "main"]);
        Assert.True(checkout.Success, checkout.Combined);
    }

    private static async Task PumpAsync(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 20000)
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
