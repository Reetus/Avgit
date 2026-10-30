using System.Globalization;
using System.Reflection;
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
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

public class TrimBranchesParsingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_gone_upstreams_tracking_counts_and_the_tip_commit()
    {
        const string output =
            "refs/heads/feature\taaaa111\t \torigin/feature\t[gone]\t1700000000\tAna Author\tFix\tthe parser\n" +
            "refs/heads/main\tbbbb222\t*\torigin/main\t[ahead 2, behind 1]\t1700000100\tBo\tMain work\n" +
            "refs/heads/local\tcccc333\t \t\t\t1700000200\tCy\tNot pushed\n" +
            "refs/remotes/origin/main\tdddd444\t \t\t\t1700000300\tDee\tRemote tip\n" +
            "refs/remotes/origin/HEAD\tdddd444\t \t\t\t1700000300\tDee\tRemote tip\n" +
            "refs/tags/v1\teeee555\t \t\t\t\t\tRelease 1\n";

        List<BranchRef> branches = GitRepoHandler.ParseBranches(output);

        Assert.Equal(["feature", "main", "local", "origin/main"], branches.Select(b => b.Name));
        BranchRef feature = branches[0];
        Assert.True(feature.UpstreamGone);
        Assert.Equal("origin/feature", feature.Upstream?.RemoteBranchForPull);
        Assert.Equal("The remote branch origin/feature was deleted", feature.UpstreamGoneToolTip);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), feature.LastCommitDate);
        Assert.Equal("Ana Author", feature.LastCommitAuthor);
        Assert.Equal("Fix\tthe parser", feature.LastCommitSubject); // a tab in the subject survives
        Assert.Equal(string.Empty, feature.UpstreamText);

        BranchRef main = branches[1];
        Assert.False(main.UpstreamGone);
        Assert.True(main.IsCheckedOut);
        Assert.Equal(2u, main.Upstream?.Ahead);
        Assert.Equal(1u, main.Upstream?.Behind);
        Assert.Equal("\u21912 \u21931", main.UpstreamText);

        Assert.Null(branches[2].Upstream);
        Assert.False(branches[2].UpstreamGone);
        Assert.Equal(RefKind.RemoteBranch, branches[3].Kind);
        Assert.Equal("Remote tip", branches[3].LastCommitSubject);
    }

    [Fact]
    public void Older_output_without_the_tip_commit_fields_still_parses()
    {
        List<BranchRef> branches = GitRepoHandler.ParseBranches("refs/heads/topic\tabc\t \torigin/topic\t[behind 3]\n");

        BranchRef topic = Assert.Single(branches);
        Assert.Equal(3u, topic.Upstream?.Behind);
        Assert.Null(topic.LastCommitDate);
    }

    [Fact]
    public async Task Candidates_are_gone_branches_not_checked_out_anywhere()
    {
        BranchRef[] branches =
        [
            Branch("gone-old", gone: true, daysOld: 40),
            Branch("current", gone: true, daysOld: 40, checkedOut: true),
            Branch("in-worktree", gone: true, daysOld: 40, worktree: @"C:\elsewhere"),
            Branch("live", gone: false, daysOld: 40),
            Branch("never-pushed", gone: null, daysOld: 40),
            new BranchRef { Name = "origin/x", Kind = RefKind.RemoteBranch, LastCommitDate = Now.AddDays(-40) }
        ];
        TrimBranchesViewModel vm = Create(branches, ["gone-old"], olderThanDays: 0);
        await vm.LoadAsync();

        Assert.Equal(["gone-old"], vm.Rows.Select(r => r.Name));
        vm.IncludeNeverPushed = true;
        Assert.Equal(["gone-old", "never-pushed"], vm.Rows.Select(r => r.Name));
        Assert.Equal("never pushed", vm.Rows[1].UpstreamText);
        Assert.Equal("origin/gone-old (deleted)", vm.Rows[0].UpstreamText);
        Assert.True(vm.Rows[0].IsMerged);
        Assert.True(vm.Rows[1].IsNotMerged);
        Assert.All(vm.Rows, r => Assert.True(r.IsSelected)); // not-merged rows are ticked too
        Assert.Equal("Delete 2 branches", vm.DeleteText);
    }

    [Fact]
    public async Task Age_filter_updates_live_and_keeps_unticked_rows_unticked()
    {
        BranchRef[] branches =
        [
            Branch("three-days", gone: true, daysOld: 3),
            Branch("ten-days", gone: true, daysOld: 10),
            Branch("forty-days", gone: true, daysOld: 40)
        ];
        TrimBranchesViewModel vm = Create(branches, [], olderThanDays: 7);
        await vm.LoadAsync();

        Assert.Equal(7, vm.SelectedAge.Days);
        Assert.Equal(["forty-days", "ten-days"], vm.Rows.Select(r => r.Name)); // oldest first
        Assert.Equal("10 days ago", vm.Rows[1].LastCommitText);
        Assert.Equal(Now.AddDays(-40).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            vm.Rows[0].LastCommitText);

        vm.Rows[0].IsSelected = false;
        Assert.Equal("Delete 1 branch", vm.DeleteText);

        vm.SelectedAge = TrimBranchesViewModel.AgeOptions.Single(o => o.Days == 0);
        Assert.Equal(3, vm.Rows.Count);
        Assert.False(vm.Rows.Single(r => r.Name == "forty-days").IsSelected);
        Assert.Equal(2, vm.SelectedCount);

        vm.SelectedAge = TrimBranchesViewModel.AgeOptions.Single(o => o.Days == 90);
        Assert.Empty(vm.Rows);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.CanDelete);
        Assert.Equal("Delete 0 branches", vm.DeleteText);
    }

    [Fact]
    public void Summary_lists_restore_shas_then_failures()
    {
        TrimBranchRow[] deleted =
        [
            new(Branch("one", gone: true, daysOld: 40), isMerged: true, Now),
            new(Branch("two", gone: true, daysOld: 40), isMerged: false, Now)
        ];

        var summary = RepositoryViewModel.BuildTrimSummary(deleted, [("three", "error: branch 'three' not found")]);

        Assert.Equal(
            "Deleted 2 branches. To restore one, run git branch <name> <sha>:\n" +
            "  one  0123456789\n" +
            "  two  0123456789\n\n" +
            "Could not delete:\n" +
            "  three: error: branch 'three' not found",
            summary);
        Assert.StartsWith("Deleted 1 branch. To restore it,", RepositoryViewModel.BuildTrimSummary([deleted[0]], []));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 1)]
    [InlineData(7, 7)]
    [InlineData(45, 30)]
    [InlineData(365, 90)]
    public void A_remembered_age_selects_the_nearest_shorter_option(int remembered, int expected)
        => Assert.Equal(expected, Create([], [], remembered).SelectedAge.Days);

    private static TrimBranchesViewModel Create(IReadOnlyList<BranchRef> branches, IReadOnlyCollection<string> merged,
        int olderThanDays)
        => new("main", olderThanDays, _ => Task.FromResult(new TrimBranchesSnapshot(branches, merged, null)), () => Now);

    [AvaloniaFact]
    public async Task Long_branch_names_trim_inside_their_pills_instead_of_overlapping()
    {
        List<BranchRef> branches =
        [
            Branch("FD-46664-AlignCapacityPlanningGenerationFlowExcelReport", gone: true, daysOld: 40),
            Branch("FD-42548-ChangeInvoiceGroupingModeMaxLimit50K-with-an-even-longer-suffix-that-cannot-fit", gone: true, daysOld: 45),
            Branch("short", gone: true, daysOld: 50)
        ];
        TrimBranchesViewModel vm = Create(branches, [], olderThanDays: 7);
        await vm.LoadAsync();
        var window = new TrimBranchesWindow { DataContext = vm, Width = 860, Height = 560 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            ItemsControl list = window.FindControl<ItemsControl>("BranchList")!;
            foreach (Control row in list.GetRealizedContainers())
            {
                WrapPanel pills = row.GetVisualDescendants().OfType<WrapPanel>().Single();
                TextBlock date = row.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Text == ((TrimBranchRow)row.DataContext!).LastCommitText);
                var dateLeft = date.TranslatePoint(new Point(0, 0), row)!.Value.X;
                foreach (Border pill in pills.Children.OfType<Border>())
                {
                    var right = pill.TranslatePoint(new Point(pill.Bounds.Width, 0), row)!.Value.X;
                    Assert.True(right <= dateLeft, $"a pill ends at {right}, past the date column at {dateLeft}");
                }
            }

            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "avgit-headless"));
            window.CaptureRenderedFrame()!.Save(Path.Combine(Path.GetTempPath(), "avgit-headless", "trim-branches-long.png"),
                PngBitmapEncoderOptions.Default);
        }
        finally
        {
            window.Close();
        }
    }

    private static BranchRef Branch(string name, bool? gone, int daysOld, bool checkedOut = false, string? worktree = null)
        => new()
        {
            Name = name,
            Kind = RefKind.Branch,
            Target = "0123456789abcdef",
            IsCheckedOut = checkedOut,
            WorktreePath = worktree,
            LastCommitDate = Now.AddDays(-daysOld),
            Upstream = gone is { } isGone
                ? new UpstreamTrack { Remote = "origin", Branch = name, RemoteBranchForPull = "origin/" + name, Gone = isGone }
                : null
        };
}

/// <summary>Trim branches against a real repository whose bare remote deleted some branches.</summary>
public class TrimBranchesRepositoryTests
{
    private static readonly string ArtifactDirectory = Path.Combine(Path.GetTempPath(), "avgit-headless");

    private sealed class Scenario : IDisposable
    {
        public required string Root { get; init; }
        public required string Remote { get; init; }
        public required TempRepo Repo { get; init; }
        public required string Main { get; init; }

        public void Dispose()
        {
            Repo.Dispose();
            try
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Root, true);
            }
            catch
            {
                // Best-effort cleanup of the remote and worktree folders.
            }
        }
    }

    private static async Task<ProcessResult> GitAsync(string directory, params string[] args)
        => await GitWithEnvironmentAsync(directory, null, args);

    private static async Task<ProcessResult> GitWithEnvironmentAsync(string directory,
        IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        ProcessResult result = await ProcessRunner.RunAsync("git", directory,
            ((string[])["-c", "commit.gpgsign=false"]).Concat(args), null, environment);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Combined}");
        return result;
    }

    private static async Task<Scenario> CreateAsync()
    {
        TempRepo repo = await TempRepo.CreateAsync();
        var main = await repo.Git.GetCurrentBranchAsync(repo.Path);
        var root = Path.Combine(Path.GetTempPath(), "avgit-trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "remote.git");
        await GitAsync(root, "init", "-q", "--bare", remote);
        await GitAsync(repo.Path, "remote", "add", "origin", remote);
        await GitAsync(repo.Path, "push", "-q", "-u", "origin", main);
        return new Scenario { Root = root, Remote = remote, Repo = repo, Main = main };
    }

    /// <summary>Commits a file with author and committer dates <paramref name="daysOld"/> days in the past.</summary>
    private static async Task CommitAsync(Scenario s, string file, string message, int daysOld)
    {
        await File.WriteAllTextAsync(Path.Combine(s.Repo.Path, file), message + "\n");
        await GitAsync(s.Repo.Path, "add", "-A");
        var date = $"{DateTimeOffset.UtcNow.AddDays(-daysOld).ToUnixTimeSeconds()} +0000";
        await GitWithEnvironmentAsync(s.Repo.Path,
            new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date },
            "commit", "-q", "-m", message);
    }

    /// <summary>A branch off main with one commit, pushed with an upstream.</summary>
    private static async Task<string> PushedBranchAsync(Scenario s, string name, int daysOld)
    {
        await GitAsync(s.Repo.Path, "checkout", "-q", "-b", name, s.Main);
        await CommitAsync(s, name.Replace('/', '-') + ".txt", "Work on " + name, daysOld);
        var sha = (await GitAsync(s.Repo.Path, "rev-parse", "HEAD")).Output.Trim();
        await GitAsync(s.Repo.Path, "checkout", "-q", s.Main);
        await GitAsync(s.Repo.Path, "push", "-q", "-u", "origin", name);
        return sha;
    }

    /// <summary>What GitHub does after merging a pull request: the branch disappears from the remote.</summary>
    private static Task DeleteOnRemoteAsync(Scenario s, string name)
        => GitAsync(s.Remote, "update-ref", "-d", "refs/heads/" + name);

    private static async Task<bool> BranchExistsAsync(Scenario s, string name)
        => (await ProcessRunner.RunAsync("git", s.Repo.Path, ["show-ref", "--verify", "--quiet", "refs/heads/" + name])).Success;

    private static async Task<(RepositoryViewModel Vm, FakeDialogService Dialogs, AppSettingsService Settings)> OpenAsync(
        Scenario s, int olderThanDays, IGitEngine? engine = null)
    {
        var settings = new AppSettingsService(Path.Combine(s.Root, "settings.json"));
        settings.Settings.AutoRefresh = false;
        settings.Settings.MaintainCommitGraph = false;
        settings.Settings.TrimBranchesOlderThanDays = olderThanDays;
        var dialogs = new FakeDialogService();
        var vm = new RepositoryViewModel(engine ?? s.Repo.Git, s.Repo.Path, settings, dialogs);
        await vm.LoadAsync();
        return (vm, dialogs, settings);
    }

    [AvaloniaFact]
    public async Task Lists_only_gone_branches_after_fetching_with_prune()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        await PushedBranchAsync(s, "gone-a", daysOld: 40);
        await PushedBranchAsync(s, "gone-b", daysOld: 40);
        await PushedBranchAsync(s, "live-c", daysOld: 40);
        await PushedBranchAsync(s, "in-worktree", daysOld: 40);
        await GitAsync(s.Repo.Path, "branch", "never-pushed", s.Main);
        await GitAsync(s.Repo.Path, "worktree", "add", "-q", Path.Combine(s.Root, "wt"), "in-worktree");
        foreach (var name in (string[])["gone-a", "gone-b", "in-worktree", s.Main])
        {
            await DeleteOnRemoteAsync(s, name);
        }

        (RepositoryViewModel? vm, FakeDialogService? dialogs, AppSettingsService _) = await OpenAsync(s, olderThanDays: 0);
        using RepositoryViewModel _ = vm;
        // Not fetched yet: the remote-tracking branches still exist locally.
        Assert.DoesNotContain(vm.Branches, b => b.UpstreamGone);

        List<string>? listed = null, withNeverPushed = null;
        dialogs.TrimBranchesResponse = d =>
        {
            Assert.True(d.FetchFirst);
            listed = [.. d.Rows.Select(r => r.Name)];
            d.IncludeNeverPushed = true;
            withNeverPushed = [.. d.Rows.Select(r => r.Name)];
            return Task.FromResult(false);
        };
        await vm.TrimBranchesAsync();

        Assert.Equal(["gone-a", "gone-b"], listed);
        // Never pushed only on request; the current branch and the worktree's branch are gone too but never offered.
        Assert.Equal(["gone-a", "gone-b", "never-pushed"], withNeverPushed);
        Assert.Empty(dialogs.Confirmations);
        Assert.Empty(dialogs.Messages);
        Assert.True(await BranchExistsAsync(s, "gone-a"));

        // Cancelling still refreshed the sidebar after the dialog's fetch.
        Assert.Equal(["gone-a", "gone-b", "in-worktree", s.Main],
            vm.Branches.Where(b => b.UpstreamGone).Select(b => b.Name).Order(StringComparer.Ordinal));
    }

    [AvaloniaFact]
    public async Task Filters_by_last_commit_age_and_remembers_the_choice()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        foreach ((var name, var days) in (ValueTuple<string, int>[])[("days-3", 3), ("days-10", 10), ("days-40", 40), ("days-100", 100)])
        {
            await PushedBranchAsync(s, name, days);
            await DeleteOnRemoteAsync(s, name);
        }

        (RepositoryViewModel? vm, FakeDialogService? dialogs, AppSettingsService? settings) = await OpenAsync(s, olderThanDays: 7);
        using RepositoryViewModel _ = vm;

        var seen = new Dictionary<int, string[]>();
        dialogs.TrimBranchesResponse = d =>
        {
            foreach (TrimAgeOption option in TrimBranchesViewModel.AgeOptions)
            {
                d.SelectedAge = option;
                seen[option.Days] = [.. d.Rows.Select(r => r.Name)];
            }

            d.SelectedAge = TrimBranchesViewModel.AgeOptions.Single(o => o.Days == 30);
            return Task.FromResult(false);
        };
        await vm.TrimBranchesAsync();

        Assert.Equal(["days-100", "days-40", "days-10", "days-3"], seen[0]);
        Assert.Equal(["days-100", "days-40", "days-10", "days-3"], seen[1]);
        Assert.Equal(["days-100", "days-40", "days-10"], seen[7]);
        Assert.Equal(["days-100", "days-40"], seen[30]);
        Assert.Equal(["days-100"], seen[90]);
        Assert.Equal(30, settings.Settings.TrimBranchesOlderThanDays);
        Assert.Contains("\"TrimBranchesOlderThanDays\": 30", await File.ReadAllTextAsync(settings.SettingsPath));
    }

    /// <summary>merged-f is merged into main; squash-g only through a squash commit, like a squash-merged pull request.</summary>
    private static async Task<(string Merged, string Squashed)> MergedAndSquashedAsync(Scenario s)
    {
        var merged = await PushedBranchAsync(s, "merged-f", daysOld: 40);
        var squashed = await PushedBranchAsync(s, "squash-g", daysOld: 40);
        await GitAsync(s.Repo.Path, "merge", "-q", "--no-edit", "merged-f");
        await GitAsync(s.Repo.Path, "merge", "-q", "--squash", "squash-g");
        await GitAsync(s.Repo.Path, "commit", "-q", "-m", "Squash-merge squash-g");
        await DeleteOnRemoteAsync(s, "merged-f");
        await DeleteOnRemoteAsync(s, "squash-g");
        return (merged, squashed);
    }

    [AvaloniaFact]
    public async Task Merged_branches_use_d_and_confirmed_unmerged_ones_use_D()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        (var mergedSha, var squashedSha) = await MergedAndSquashedAsync(s);
        (IGitEngine engine, RecordingDeleteHandler recorder) = RecordingDeleteHandler.Wrap(s.Repo.Git);
        (RepositoryViewModel? vm, FakeDialogService? dialogs, AppSettingsService _) = await OpenAsync(s, olderThanDays: 7, engine);
        using RepositoryViewModel _ = vm;

        dialogs.TrimBranchesResponse = d =>
        {
            Assert.Equal(["merged-f", "squash-g"], d.Rows.Select(r => r.Name).Order(StringComparer.Ordinal));
            Assert.True(d.Rows.Single(r => r.Name == "merged-f").IsMerged);
            Assert.False(d.Rows.Single(r => r.Name == "squash-g").IsMerged);
            Assert.All(d.Rows, r => Assert.True(r.IsSelected));
            return Task.FromResult(true);
        };
        await vm.TrimBranchesAsync();

        (var _, var question) = Assert.Single(dialogs.Confirmations);
        Assert.Equal($"1 of these isn't merged into {s.Main}; its commits will only be recoverable from the reflog.", question);
        Assert.Equal([("merged-f", false), ("squash-g", true)], recorder.Deletes.Order());
        Assert.False(await BranchExistsAsync(s, "merged-f"));
        Assert.False(await BranchExistsAsync(s, "squash-g"));
        Assert.DoesNotContain(vm.Branches, b => b.Name is "merged-f" or "squash-g");

        var summary = Assert.Single(dialogs.Messages);
        Assert.Contains("git branch <name> <sha>", summary);
        Assert.Contains("merged-f  " + mergedSha[..10], summary);
        Assert.Contains("squash-g  " + squashedSha[..10], summary);
        Assert.DoesNotContain("Could not delete", summary);
    }

    [AvaloniaFact]
    public async Task Cancelling_the_unmerged_confirmation_deletes_nothing()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        await MergedAndSquashedAsync(s);
        (IGitEngine engine, RecordingDeleteHandler recorder) = RecordingDeleteHandler.Wrap(s.Repo.Git);
        (RepositoryViewModel? vm, FakeDialogService? dialogs, AppSettingsService _) = await OpenAsync(s, olderThanDays: 7, engine);
        using RepositoryViewModel _ = vm;

        dialogs.ConfirmResult = false;
        dialogs.TrimBranchesResponse = d => Task.FromResult(d.SelectedCount == 2);
        await vm.TrimBranchesAsync();

        Assert.Single(dialogs.Confirmations);
        Assert.Empty(recorder.Deletes);
        Assert.Empty(dialogs.Messages);
        Assert.True(await BranchExistsAsync(s, "merged-f"));
        Assert.True(await BranchExistsAsync(s, "squash-g"));
    }

    [AvaloniaFact]
    public async Task A_failed_delete_is_reported_with_gits_reason()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        await MergedAndSquashedAsync(s);
        (RepositoryViewModel? vm, FakeDialogService? dialogs, AppSettingsService _) = await OpenAsync(s, olderThanDays: 7);
        using RepositoryViewModel _ = vm;

        dialogs.TrimBranchesResponse = async d =>
        {
            // Only the merged branch, so no extra confirmation; it vanishes before the delete runs.
            d.Rows.Single(r => r.Name == "squash-g").IsSelected = false;
            Assert.Equal("Delete 1 branch", d.DeleteText);
            await GitAsync(s.Repo.Path, "branch", "-D", "merged-f");
            return true;
        };
        await vm.TrimBranchesAsync();

        Assert.Empty(dialogs.Confirmations);
        var summary = Assert.Single(dialogs.Messages);
        Assert.Contains("Could not delete:", summary);
        Assert.Contains("merged-f: error: branch 'merged-f' not found", summary);
        Assert.True(await BranchExistsAsync(s, "squash-g"));
        Assert.Contains("1 failed", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Dialog_opens_from_the_branches_section_and_lists_the_gone_branches()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        await MergedAndSquashedAsync(s);
        await PushedBranchAsync(s, "feature/old-experiment", daysOld: 120);
        await DeleteOnRemoteAsync(s, "feature/old-experiment");

        var settings = new AppSettingsService(Path.Combine(s.Root, "settings-ui.json"));
        settings.Settings.AutoRefresh = false;
        settings.Settings.MaintainCommitGraph = false;
        var dialogs = new DialogService();
        var main = new MainWindowViewModel(settings, dialogs);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 860 };
        dialogs.Owner = window;
        window.Show();
        await main.OpenPathAsync(s.Repo.Path);
        Dispatcher.UIThread.RunJobs();
        RepositoryViewModel repository = main.Repository!;

        Button trim = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Content as string == "Trim branches...");
        Assert.Same(repository.TrimBranchesCommand, trim.Command);
        Point point = trim.TranslatePoint(new Point(trim.Bounds.Width / 2, trim.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        await WaitForAsync(() => window.OwnedWindows.OfType<TrimBranchesWindow>().Any());
        TrimBranchesWindow dialog = window.OwnedWindows.OfType<TrimBranchesWindow>().Single();
        var vm = (TrimBranchesViewModel)dialog.DataContext!;
        await WaitForAsync(() => !vm.IsLoading && vm.Rows.Count == 3);
        Dispatcher.UIThread.RunJobs();

        ItemsControl list = dialog.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "BranchList");
        var rendered = list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text).ToList();
        Assert.Contains("feature/old-experiment", rendered);
        Assert.Contains("merged", rendered);
        Assert.Contains("not merged", rendered);
        Assert.Contains("origin/squash-g (deleted)", rendered);
        Button delete = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DeleteButton");
        Assert.Equal("Delete 3 branches", delete.Content);
        Assert.True(delete.IsEffectivelyEnabled);

        Directory.CreateDirectory(ArtifactDirectory);
        WriteableBitmap? frame = dialog.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactDirectory, "trim-branches.png"), PngBitmapEncoderOptions.Default);

        Button cancel = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel");
        Point cancelPoint = cancel.TranslatePoint(new Point(cancel.Bounds.Width / 2, cancel.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseDown(cancelPoint, MouseButton.Left);
        dialog.MouseUp(cancelPoint, MouseButton.Left);

        // Cancelled after the dialog's fetch: nothing deleted, and the sidebar marks the gone branches.
        await WaitForAsync(() => !window.OwnedWindows.Any() && !repository.IsBusy
                                 && repository.Branches.Count(b => b.UpstreamGone) == 3);
        Assert.True(await BranchExistsAsync(s, "merged-f"));
        Dispatcher.UIThread.RunJobs();
        var markers = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text == "gone" && t.IsEffectivelyVisible).ToList();
        Assert.Equal(3, markers.Count);
        // The marker is a pill; its border carries the tooltip.
        Assert.Contains(markers, m => m.Parent is Border pill && ToolTip.GetTip(pill) as string == "The remote branch origin/merged-f was deleted");
        window.CaptureRenderedFrame()!.Save(Path.Combine(ArtifactDirectory, "trim-branches-sidebar.png"), PngBitmapEncoderOptions.Default);

        window.Close();
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

/// <summary>Forwards to a real engine and records every branch deletion with its force flag.</summary>
public class RecordingDeleteHandler : DispatchProxy
{
    private IGitEngine _inner = null!;

    public List<(string Name, bool Force)> Deletes { get; } = [];

    public static (IGitEngine Engine, RecordingDeleteHandler Recorder) Wrap(IGitEngine inner)
    {
        IGitEngine proxy = Create<IGitEngine, RecordingDeleteHandler>();
        var recorder = (RecordingDeleteHandler)(object)proxy;
        recorder._inner = inner;
        return (proxy, recorder);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IGitEngine.DeleteBranchAsync))
        {
            Deletes.Add(((string)args![1]!, (bool)args[2]!));
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
}
