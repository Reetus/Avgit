using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avgit.App.Resources;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.App.Views;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>Setting, changing and clearing a branch's upstream from the branch context menu.</summary>
public class BranchUpstreamTests
{
    private static readonly string ArtifactDirectory =
        Path.Combine(Path.GetTempPath(), "avgit-headless");

    /// <summary>A repository with a bare origin holding main and feature; local feature tracks nothing.</summary>
    private sealed class Scenario : IDisposable
    {
        public required TempRepo Repo { get; init; }
        public required string Remote { get; init; }
        public required string Main { get; init; }

        public static async Task<Scenario> CreateAsync()
        {
            TempRepo repo = await TempRepo.CreateAsync();
            var remote = repo.Path + "-origin.git";
            await ProcessRunner.RunAsync("git", Path.GetTempPath(), ["init", "-q", "--bare", remote]);
            await repo.Git.AddRemoteAsync(repo.Path, "origin", remote);
            var main = await repo.Git.GetCurrentBranchAsync(repo.Path);
            await repo.Git.CreateBranchAsync(repo.Path, "feature", null, checkout: false);
            await repo.Git.RunAsync(repo.Path, ["push", "-q", "origin", main, "feature"]);
            await repo.Git.RunAsync(repo.Path, ["fetch", "-q", "origin"]);
            return new Scenario { Repo = repo, Remote = remote, Main = main };
        }

        public async Task<string?> UpstreamOfAsync(string branch)
        {
            ProcessResult result = await Repo.Git.RunAsync(Repo.Path, ["rev-parse", "--abbrev-ref", branch + "@{upstream}"]);
            return result.Success ? result.Output.Trim() : null;
        }

        public void Dispose()
        {
            Repo.Dispose();
            if (Directory.Exists(Remote))
            {
                foreach (var file in Directory.EnumerateFiles(Remote, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Remote, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Engine_sets_and_unsets_the_upstream()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario scenario = await Scenario.CreateAsync();
        Assert.Null(await scenario.UpstreamOfAsync("feature"));

        ProcessResult set = await scenario.Repo.Git.SetUpstreamAsync(scenario.Repo.Path, "feature", "origin/feature");
        Assert.True(set.Success, set.Combined);
        Assert.Equal("origin/feature", await scenario.UpstreamOfAsync("feature"));

        ProcessResult unset = await scenario.Repo.Git.UnsetUpstreamAsync(scenario.Repo.Path, "feature");
        Assert.True(unset.Success, unset.Combined);
        Assert.Null(await scenario.UpstreamOfAsync("feature"));
    }

    [AvaloniaFact]
    public async Task Set_change_and_unset_upstream_through_the_view_model()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario scenario = await Scenario.CreateAsync();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, $"settings-upstream-{Guid.NewGuid():N}.json"));
        settings.Settings.AutoRefresh = false;
        var dialogs = new FakeDialogService();
        using var vm = new RepositoryViewModel(scenario.Repo.Git, scenario.Repo.Path, settings, dialogs);
        await vm.LoadAsync();
        BranchRef Feature() => vm.Branches.Single(b => b.Name == "feature");
        Assert.True(Feature().CanSetUpstream);
        Assert.False(Feature().HasUpstream);

        // Set: the remote branch with the same name starts selected.
        dialogs.PickResponses.Enqueue("origin/feature");
        await ExecuteAsync(vm.SetUpstreamCommand, Feature(), vm);
        (string message, IReadOnlyList<string> items, string? selected) = dialogs.Picks[^1];
        Assert.Equal("origin/feature", selected);
        Assert.Contains("origin/" + scenario.Main, items);
        Assert.Equal(string.Format(Strings.Repo_SetUpstreamPrompt, "feature"), message);
        Assert.Equal("origin/feature", await scenario.UpstreamOfAsync("feature"));
        Assert.True(Feature().HasUpstream);
        Assert.Equal("origin/feature", Feature().Upstream?.RemoteBranchForPull);

        // Change: the current upstream starts selected; cancelling changes nothing.
        await ExecuteAsync(vm.SetUpstreamCommand, Feature(), vm);
        Assert.Equal("origin/feature", dialogs.Picks[^1].Selected);
        Assert.Equal(string.Format(Strings.Repo_ChangeUpstreamPrompt, "feature", "origin/feature"), dialogs.Picks[^1].Message);
        Assert.Equal("origin/feature", await scenario.UpstreamOfAsync("feature"));

        dialogs.PickResponses.Enqueue("origin/" + scenario.Main);
        await ExecuteAsync(vm.SetUpstreamCommand, Feature(), vm);
        Assert.Equal("origin/" + scenario.Main, await scenario.UpstreamOfAsync("feature"));

        // Unset.
        await ExecuteAsync(vm.UnsetUpstreamCommand, Feature(), vm);
        Assert.Null(await scenario.UpstreamOfAsync("feature"));
        Assert.True(Feature().CanSetUpstream);
        Assert.Empty(dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task A_gone_upstream_can_be_changed_or_cleared()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario scenario = await Scenario.CreateAsync();
        await scenario.Repo.Git.SetUpstreamAsync(scenario.Repo.Path, "feature", "origin/feature");
        // The remote branch is deleted (e.g. after its pull request merged) and pruned.
        await ProcessRunner.RunAsync("git", scenario.Remote, ["branch", "-D", "feature"]);
        await scenario.Repo.Git.RunAsync(scenario.Repo.Path, ["fetch", "-q", "--prune", "origin"]);

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, $"settings-upstream-{Guid.NewGuid():N}.json"));
        settings.Settings.AutoRefresh = false;
        var dialogs = new FakeDialogService();
        using var vm = new RepositoryViewModel(scenario.Repo.Git, scenario.Repo.Path, settings, dialogs);
        await vm.LoadAsync();
        BranchRef Feature() => vm.Branches.Single(b => b.Name == "feature");
        Assert.True(Feature().UpstreamGone);
        Assert.True(Feature().HasUpstream);

        // The gone remote branch is not offered, so nothing starts selected.
        dialogs.PickResponses.Enqueue("origin/" + scenario.Main);
        await ExecuteAsync(vm.SetUpstreamCommand, Feature(), vm);
        Assert.Null(dialogs.Picks[^1].Selected);
        Assert.DoesNotContain("origin/feature", dialogs.Picks[^1].Items);
        Assert.False(Feature().UpstreamGone);

        await scenario.Repo.Git.SetUpstreamAsync(scenario.Repo.Path, "feature", "origin/" + scenario.Main);
        await ExecuteAsync(vm.UnsetUpstreamCommand, Feature(), vm);
        Assert.Null(await scenario.UpstreamOfAsync("feature"));
    }

    [AvaloniaFact]
    public async Task Without_remote_branches_set_upstream_explains_instead_of_picking()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await repo.Git.CreateBranchAsync(repo.Path, "feature", null, checkout: false);
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, $"settings-upstream-{Guid.NewGuid():N}.json"));
        settings.Settings.AutoRefresh = false;
        var dialogs = new FakeDialogService();
        using var vm = new RepositoryViewModel(repo.Git, repo.Path, settings, dialogs);
        await vm.LoadAsync();

        await ExecuteAsync(vm.SetUpstreamCommand, vm.Branches.Single(b => b.Name == "feature"), vm);
        Assert.Empty(dialogs.Picks);
        Assert.Equal($"{Strings.Repo_OpSetUpstream}: {Strings.Repo_SetUpstreamNoRemoteBranches}", Assert.Single(dialogs.Messages));
    }

    [AvaloniaFact]
    public async Task Branch_menu_offers_set_or_change_and_unset_by_upstream()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario scenario = await Scenario.CreateAsync();
        await scenario.Repo.Git.SetUpstreamAsync(scenario.Repo.Path, scenario.Main, "origin/" + scenario.Main);
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, $"settings-upstream-{Guid.NewGuid():N}.json"));
        settings.Settings.AutoRefresh = false;
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        try
        {
            await vm.OpenPathAsync(scenario.Repo.Path);
            RepositoryViewModel repository = vm.Repository!;
            Dispatcher.UIThread.RunJobs();
            ListBox branches = window.GetVisualDescendants().OfType<ListBox>()
                .First(l => ReferenceEquals(l.ItemsSource, repository.Branches));

            IReadOnlyList<string> VisibleItemsFor(string name)
            {
                BranchRef branch = repository.Branches.Single(b => b.Name == name);
                var row = (Control)branches.ContainerFromItem(branch)!;
                Point point = row.TranslatePoint(new Point(30, row.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Right);
                window.MouseUp(point, MouseButton.Right);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(branch, repository.SelectedBranch);
                ContextMenu menu = branches.ContextMenu!;
                List<string> visible = [.. menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => (string)i.Header!)];
                menu.Close();
                Dispatcher.UIThread.RunJobs();
                return visible;
            }

            IReadOnlyList<string> withoutUpstream = VisibleItemsFor("feature");
            Assert.Contains(Strings.Repo_View_SetUpstream, withoutUpstream);
            Assert.DoesNotContain(Strings.Repo_View_ChangeUpstream, withoutUpstream);
            Assert.DoesNotContain(Strings.Repo_View_UnsetUpstream, withoutUpstream);

            IReadOnlyList<string> withUpstream = VisibleItemsFor(scenario.Main);
            Assert.DoesNotContain(Strings.Repo_View_SetUpstream, withUpstream);
            Assert.Contains(Strings.Repo_View_ChangeUpstream, withUpstream);
            Assert.Contains(Strings.Repo_View_UnsetUpstream, withUpstream);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Picker_filters_keeps_a_matching_selection_and_returns_the_choice()
    {
        string? picked = null;
        List<string> items = ["origin/main", "origin/feature/login", "origin/feature/search", "upstream/main"];
        Window window = DialogService.CreatePickWindow("Set upstream", "Choose the remote branch.", items, "origin/feature/search",
            item => picked = item);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single();
            TextBox filter = window.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.Equal("origin/feature/search", list.SelectedItem);

            // Typing narrows the list; the selection stays while it still matches.
            filter.Focus();
            window.KeyTextInput("feat");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["origin/feature/login", "origin/feature/search"], list.ItemsSource!.Cast<string>());
            Assert.Equal("origin/feature/search", list.SelectedItem);

            Directory.CreateDirectory(ArtifactDirectory);
            window.CaptureRenderedFrame()!.Save(Path.Combine(ArtifactDirectory, "pick-upstream.png"), PngBitmapEncoderOptions.Default);

            // Once it no longer matches, the first match is offered instead.
            filter.Text = "main";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["origin/main", "upstream/main"], list.ItemsSource!.Cast<string>());
            Assert.Equal("origin/main", list.SelectedItem);

            Button ok = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == Strings.Common_OK);
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("origin/main", picked);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command, object parameter, RepositoryViewModel vm)
    {
        command.Execute(parameter);
        var started = Environment.TickCount64;
        // The command runs to completion on the UI thread; wait for its reload to finish.
        do
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
            if (Environment.TickCount64 - started > 15000)
            {
                throw new TimeoutException("The command did not finish in time.");
            }
        }
        while (vm.IsBusy || !command.CanExecute(parameter));
    }
}
