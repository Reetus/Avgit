using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avgit.App.Controls;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.App.Views;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

public class HeadlessUiTests
{
    private static readonly string ArtifactDirectory =
        Path.Combine(Path.GetTempPath(), "avgit-headless");

    [AvaloniaFact]
    public void Welcome_screen_renders()
    {
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-welcome.json"));
        var dialogs = new FakeDialogService();
        var vm = new MainWindowViewModel(settings, dialogs);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        Directory.CreateDirectory(ArtifactDirectory);
        frame!.Save(Path.Combine(ArtifactDirectory, "welcome.png"), PngBitmapEncoderOptions.Default);
        Assert.True(File.Exists(Path.Combine(ArtifactDirectory, "welcome.png")));
        Assert.True(vm.WelcomeVisible);

        ProgressBar? busyBar = window.GetVisualDescendants().OfType<ProgressBar>().FirstOrDefault();
        Assert.NotNull(busyBar);
        Assert.False(busyBar!.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Repository_history_and_file_status_render_headlessly()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await repo.AddWorkingChangesAsync();

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-repo.json"));
        var dialogs = new FakeDialogService();
        var vm = new MainWindowViewModel(settings, dialogs);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };

        window.Show();
        await vm.OpenPathAsync(repo.Path);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Repository);
        Assert.NotEmpty(vm.Repository!.LogRows);
        Assert.NotEmpty(vm.Repository.CommitFiles);
        Assert.NotEmpty(vm.Repository.UnstagedFiles);
        Assert.NotEmpty(vm.Repository.CommitDiff.DisplayItems);
        Assert.Empty(dialogs.Messages);

        ListBox? commitList = window.GetVisualDescendants().OfType<ListBox>()
            .FirstOrDefault(list => list.Name == "CommitList");
        Assert.NotNull(commitList);
        Assert.NotNull(commitList!.ContextMenu);
        var menuHeaders = commitList.ContextMenu!.Items.OfType<MenuItem>()
            .Select(item => item.Header as string)
            .ToList();
        Assert.Contains("Checkout commit", menuHeaders);
        Assert.Contains("Interactive rebase from here...", menuHeaders);
        Assert.Contains("Reset --hard to this commit", menuHeaders);

        var rowContainer = commitList.ContainerFromIndex(0) as Control;
        Assert.NotNull(rowContainer);
        Point? rowPoint = rowContainer!.TranslatePoint(new Point(12, 12), window);
        Assert.NotNull(rowPoint);
        window.MouseDown(rowPoint!.Value, MouseButton.Right);
        window.MouseUp(rowPoint.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(commitList.ContextMenu!.IsOpen, "commit context menu should open on a row");
        commitList.ContextMenu.Close();
        Dispatcher.UIThread.RunJobs();

        Point? emptyPoint = commitList.TranslatePoint(new Point(commitList.Bounds.Width / 2, commitList.Bounds.Height - 4), window);
        Assert.NotNull(emptyPoint);
        window.MouseDown(emptyPoint!.Value, MouseButton.Right);
        window.MouseUp(emptyPoint.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.False(commitList.ContextMenu.IsOpen, "context menu should not open on empty list space");

        Button? addRemoteButton = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content as string, "+ Add remote", StringComparison.Ordinal));
        Assert.NotNull(addRemoteButton);
        Assert.Same(vm.Repository.AddRemoteCommand, addRemoteButton!.Command);

        Directory.CreateDirectory(ArtifactDirectory);
        WriteableBitmap? history = window.CaptureRenderedFrame();
        Assert.NotNull(history);
        history!.Save(Path.Combine(ArtifactDirectory, "history.png"), PngBitmapEncoderOptions.Default);

        TabControl? tabControl = window.GetVisualDescendants().OfType<TabControl>()
            .FirstOrDefault(t => t.Items.OfType<TabItem>()
                .Any(item => string.Equals(item.Header as string, "File Status", StringComparison.Ordinal)));
        Assert.NotNull(tabControl);
        tabControl!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        WriteableBitmap? workingFrame = window.CaptureRenderedFrame();
        Assert.NotNull(workingFrame);
        workingFrame!.Save(Path.Combine(ArtifactDirectory, "file-status.png"), PngBitmapEncoderOptions.Default);

        DiffView? workingDiffView = window.GetVisualDescendants()
            .OfType<DiffView>()
            .FirstOrDefault(v => ReferenceEquals(v.DataContext, vm.Repository.WorkingDiff));
        Assert.NotNull(workingDiffView);
        Assert.NotEmpty(vm.Repository.WorkingDiff.DisplayItems);

        var diffCheckBoxes = workingDiffView!.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => c.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(diffCheckBoxes);
        Assert.All(diffCheckBoxes, box =>
        {
            Assert.True(box.Bounds.Width >= 16, $"checkbox width was {box.Bounds.Width}");
            Assert.True(box.Bounds.Height >= 16, $"checkbox height was {box.Bounds.Height}");
        });

        ItemsControl? diffItems = workingDiffView.GetVisualDescendants().OfType<ItemsControl>()
            .FirstOrDefault(c => c.DataContext == workingDiffView.DataContext);
        Assert.NotNull(diffItems);
        Assert.True(diffItems!.Bounds.Width >= workingDiffView.Bounds.Width - 4,
            $"diff content width {diffItems.Bounds.Width} vs pane {workingDiffView.Bounds.Width}");

        // The left-side dropdowns must span the sidebar rather than collapsing to content width.
        var expanders = window.GetVisualDescendants().OfType<Expander>().ToList();
        Assert.NotEmpty(expanders);
        Assert.All(expanders, expander =>
            Assert.True(expander.Bounds.Width >= 200,
                $"Expander '{expander.Header}' width was {expander.Bounds.Width}"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Ref_pills_fold_overflow_and_narrow_columns_collapse()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var head = await repo.Git.GetCurrentRevisionAsync(repo.Path);
        await repo.Git.RunAsync(repo.Path, ["update-ref", "refs/remotes/origin/master", head]);
        await repo.Git.RunAsync(repo.Path, ["symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/master"]);
        foreach (var name in (string[])["feature/port-selection-message-prompt-gumps", "feature/rehue-friends-layers", "release/2026.10"])
        {
            await repo.Git.RunAsync(repo.Path, ["branch", name]);
        }

        await repo.Git.RunAsync(repo.Path, ["update-ref", "refs/remotes/origin/feature/remote-only-branch", head]);
        await repo.Git.RunAsync(repo.Path, ["tag", "v1.0.0"]);

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-pills.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 700 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        Dispatcher.UIThread.RunJobs();

        CommitRowViewModel top = vm.Repository!.LogRows[0];
        // master folds origin/master in, and origin/HEAD is dropped.
        Assert.Contains(top.Pills, p => p.Text == "master" && p.Remotes == "origin" && p.Kind == RefPillKind.CurrentBranch);
        Assert.DoesNotContain(top.Pills, p => p.Text.EndsWith("HEAD", StringComparison.Ordinal));
        Assert.Equal(RefPillKind.CurrentBranch, top.Pills[0].Kind);

        ListBox commitList = window.GetVisualDescendants().OfType<ListBox>().First(list => list.Name == "CommitList");
        Directory.CreateDirectory(ArtifactDirectory);
        foreach (var width in (int[])[1360, 1000, 760])
        {
            window.Width = width;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()!.Save(Path.Combine(ArtifactDirectory, $"ref-pills-{width}.png"), PngBitmapEncoderOptions.Default);

            var row = (Control)commitList.ContainerFromIndex(0)!;
            RefPillsPanel pills = row.GetVisualDescendants().OfType<Avgit.App.Controls.RefPillsPanel>().Single();
            DockPanel message = row.GetVisualDescendants().OfType<DockPanel>().Single();
            Assert.True(pills.Bounds.Width <= message.Bounds.Width / 2 + 1,
                $"pills {pills.Bounds.Width} exceed half of {message.Bounds.Width} at window width {width}");
            TextBlock author = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("author"));
            Assert.Equal(commitList.Bounds.Width >= 620, author.IsEffectivelyVisible);
            Assert.True(message.Bounds.Right <= row.Bounds.Width, "message area must not run past the row");
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Staging_a_hunk_updates_models_and_ui()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "stage this line\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "untracked.txt"), "untracked\n");

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-stage.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        await vm.OpenPathAsync(repo.Path);

        RepositoryViewModel repository = vm.Repository!;
        ChangedFile file = repository.UnstagedFiles.First(f => f.RelativePath == "readme.txt");
        repository.SelectedUnstagedFile = file;
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.Count > 0);

        DiffHunkHeaderItem hunkHeader = repository.WorkingDiff.DisplayItems.OfType<DiffHunkHeaderItem>()
            .First(h => h.CanStage);
        hunkHeader.StageCommand!.Execute(hunkHeader.Hunk);
        await WaitForAsync(() => repository.StagedFiles.Any(f => f.RelativePath == "readme.txt") && !repository.IsBusy);

        Assert.Contains(repository.StagedFiles, f => f.RelativePath == "readme.txt");
        Assert.DoesNotContain(repository.UnstagedFiles, f => f.RelativePath == "readme.txt");

        // Staging the hunk of an untracked file must stage the whole file.
        ChangedFile untracked = repository.UnstagedFiles.First(f => f.RelativePath == "untracked.txt");
        repository.SelectedUnstagedFile = untracked;
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.OfType<DiffFileHeaderItem>()
            .Any(h => h.File.FileName == "untracked.txt"));
        DiffHunkHeaderItem untrackedHunk = repository.WorkingDiff.DisplayItems.OfType<DiffHunkHeaderItem>()
            .First(h => h.CanStage);
        await WaitForAsync(() => untrackedHunk.StageCommand!.CanExecute(untrackedHunk.Hunk));
        untrackedHunk.StageCommand!.Execute(untrackedHunk.Hunk);
        await WaitForAsync(() => repository.StagedFiles.Any(f => f.RelativePath == "untracked.txt"), timeoutMs: 8000);

        Assert.Contains(repository.StagedFiles, f => f.RelativePath == "untracked.txt");
    }

    [AvaloniaFact]
    public async Task Multiple_repositories_open_in_tabs_and_close()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo first = await TempRepo.CreateAsync();
        using TempRepo second = await TempRepo.CreateAsync(withHistory: false);
        await File.WriteAllTextAsync(Path.Combine(second.Path, "second.txt"), "second\n");

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-tabs.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        window.Show();

        await vm.OpenPathAsync(first.Path);
        await vm.OpenPathAsync(first.Path + System.IO.Path.DirectorySeparatorChar);
        Assert.Single(vm.Repositories);

        await vm.OpenPathAsync(second.Path);

        Assert.Equal(2, vm.Repositories.Count);
        Assert.Same(vm.Repositories[1], vm.Repository);
        Assert.True(vm.HasRepository);
        Assert.All(vm.Repositories, repository => Assert.False(repository.IsBusy));
        Assert.DoesNotContain("Opening", vm.Repositories[0].StatusMessage);

        Dispatcher.UIThread.RunJobs();
        Button? addButton = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content as string, "+", StringComparison.Ordinal));
        Assert.NotNull(addButton);
        Assert.Same(vm.ShowHomeCommand, addButton!.Command);

        vm.ShowHomeCommand.Execute(null);
        Assert.True(vm.WelcomeVisible);
        Assert.False(vm.ShowRepositoryContent);
        Assert.NotNull(vm.Repository);
        Assert.Equal(2, vm.Repositories.Count);

        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? homeFrame = window.CaptureRenderedFrame();
        Assert.NotNull(homeFrame);
        homeFrame!.Save(Path.Combine(ArtifactDirectory, "home-with-open-tabs.png"), PngBitmapEncoderOptions.Default);

        ListBox? tabs = window.FindControl<ListBox>("RepositoryTabs");
        Assert.NotNull(tabs);
        var selectedTab = tabs!.ContainerFromIndex(1) as Control;
        Assert.NotNull(selectedTab);
        Point? tabPoint = selectedTab!.TranslatePoint(new Point(20, 10), window);
        Assert.NotNull(tabPoint);
        window.MouseDown(tabPoint!.Value, MouseButton.Left);
        window.MouseUp(tabPoint.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.WelcomeVisible);
        Assert.True(vm.ShowRepositoryContent);

        vm.Repository = vm.Repositories[1];
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? tabsFrame = window.CaptureRenderedFrame();
        Assert.NotNull(tabsFrame);
        tabsFrame!.Save(Path.Combine(ArtifactDirectory, "repository-tabs.png"), PngBitmapEncoderOptions.Default);

        vm.Repository = vm.Repositories[0];
        Assert.Equal(first.Path, vm.Repository!.Path);

        vm.CloseRepositoryCommand.Execute(vm.Repositories[1]);
        Assert.Single(vm.Repositories);
        Assert.Equal(first.Path, vm.Repository!.Path);

        vm.CloseRepositoryCommand.Execute(vm.Repository);
        Assert.Empty(vm.Repositories);
        Assert.True(vm.WelcomeVisible);

        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_the_active_tab_selects_its_neighbour()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo first = await TempRepo.CreateAsync();
        using TempRepo second = await TempRepo.CreateAsync(withHistory: false);
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-tabclose.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());

        await vm.OpenPathAsync(first.Path);
        await vm.OpenPathAsync(second.Path);
        RepositoryViewModel active = vm.Repository!;
        Assert.Same(vm.Repositories[1], active);

        vm.CloseRepositoryCommand.Execute(active);
        Assert.Single(vm.Repositories);
        Assert.Same(vm.Repositories[0], vm.Repository);
        Assert.True(vm.ShowRepositoryContent);
        Assert.False(vm.WelcomeVisible);
    }

    [AvaloniaFact]
    public async Task Selecting_a_branch_selects_its_tip_commit()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var main = await repo.Git.GetCurrentBranchAsync(repo.Path);
        Assert.True((await repo.Git.CreateBranchAsync(repo.Path, "feature/jump", null, checkout: true)).Success);
        var featureSha = await repo.CreateCommitAsync("feature jump", "feature.txt", "feature\n");
        Assert.True((await repo.Git.CheckoutAsync(repo.Path, main)).Success);

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-branchjump.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;

        BranchRef branch = repository.Branches.First(b => b.Name == "feature/jump");
        repository.SelectedBranch = branch;

        await WaitForAsync(() => repository.SelectedCommit?.Row.CommitId == featureSha);
        Assert.Equal(featureSha, repository.SelectedCommit!.Row.CommitId);
    }

    [AvaloniaFact]
    public async Task Merge_conflict_shows_the_banner_and_can_be_aborted()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
        var file = Path.Combine(repo.Path, "conflict.txt");
        await File.WriteAllTextAsync(file, "base\n");
        await repo.StageAllAsync();
        Assert.True((await repo.Git.CommitAsync(repo.Path, "base")).Success);

        var main = await repo.Git.GetCurrentBranchAsync(repo.Path);
        Assert.True((await repo.Git.CreateBranchAsync(repo.Path, "conflict", null, checkout: true)).Success);
        await File.WriteAllTextAsync(file, "feature\n");
        await repo.StageAllAsync();
        Assert.True((await repo.Git.CommitAsync(repo.Path, "feature")).Success);
        Assert.True((await repo.Git.CheckoutAsync(repo.Path, main)).Success);
        await File.WriteAllTextAsync(file, "main\n");
        await repo.StageAllAsync();
        Assert.True((await repo.Git.CommitAsync(repo.Path, "main")).Success);

        var dialogs = new FakeDialogService();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-conflict.json"));
        var vm = new MainWindowViewModel(settings, dialogs);
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;

        CommitRowViewModel featureRow = repository.LogRows.First(r => r.Row.Title == "feature");
        repository.SelectedCommit = featureRow;
        await WaitForAsync(() => repository.SelectedCommit == featureRow);
        repository.MergeCommitCommand.Execute(null);

        await WaitForAsync(() => repository.HasConflict);
        Assert.True(repository.IsMergeInProgress);
        Assert.NotEmpty(dialogs.Messages);

        repository.AbortMergeCommand.Execute(null);
        await WaitForAsync(() => !repository.HasConflict);
        Assert.False(repository.IsMergeInProgress);
    }

    [AvaloniaFact]
    public async Task File_watcher_refresh_keeps_the_selected_commit_and_diff()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-watchselect.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;

        CommitRowViewModel older = repository.LogRows[1];
        repository.SelectedCommit = older;
        await WaitForAsync(() => ReferenceEquals(repository.SelectedCommit, older)
            && repository.CommitInfo?.ShortSha == older.Row.ShortId
            && repository.CommitDiff.DisplayItems.Count > 0);

        // Let the initial async diff load settle before capturing it.
        await Task.Delay(750);
        Dispatcher.UIThread.RunJobs();

        CommitRowViewModel selected = repository.SelectedCommit;
        CommitInfoViewModel? info = repository.CommitInfo;
        var diffCount = repository.CommitDiff.DisplayItems.Count;

        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "watcher change\n");
        await WaitForAsync(() => repository.UnstagedFiles.Any(f => f.RelativePath == "readme.txt"));
        await Task.Delay(900);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(selected, repository.SelectedCommit);
        Assert.Same(info, repository.CommitInfo);
        Assert.Equal(diffCount, repository.CommitDiff.DisplayItems.Count);
    }

    [AvaloniaFact]
    public async Task Multiple_selected_commits_can_be_cherry_picked()
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
        await repo.CreateCommitAsync("pick one", "one.txt", "one\n");
        await repo.CreateCommitAsync("pick two", "two.txt", "two\n");
        Assert.True((await repo.Git.CheckoutAsync(repo.Path, main)).Success);

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-multipick.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;

        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        ListBox? commitList = window.GetVisualDescendants().OfType<ListBox>()
            .FirstOrDefault(list => list.Name == "CommitList");
        Assert.NotNull(commitList);

        CommitRowViewModel first = repository.LogRows.First(r => r.Row.Title == "pick one");
        CommitRowViewModel second = repository.LogRows.First(r => r.Row.Title == "pick two");
        commitList!.SelectedItems!.Clear();
        commitList.SelectedItems.Add(first);
        commitList.SelectedItems.Add(second);
        Dispatcher.UIThread.RunJobs();

        // Simulate the context-menu path: changing the active commit must not
        // collapse the ListBox multi-selection.
        repository.SetContextCommit(second);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, commitList.SelectedItems.Count);

        repository.CherryPickCommand.Execute(null);

        List<Avgit.Core.Model.HistoryRow>? headLog = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var current = (await repo.Git.GetLogAsync(repo.Path,
                new Avgit.Core.Model.HistoryFilter { Count = 10, StartRef = main })).ToList();
            if (current.Any(r => r.Title == "pick one")
                && current.Any(r => r.Title == "pick two"))
            {
                headLog = current;
                break;
            }
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.NotNull(headLog);
        var one = headLog!.FindIndex(r => r.Title == "pick one");
        var two = headLog.FindIndex(r => r.Title == "pick two");
        Assert.True(two >= 0 && one >= 0, "Both commits must be on the current branch.");
        Assert.True(two < one, "Cherry-pick must apply the oldest selected commit first.");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Discarding_an_untracked_hunk_removes_the_file()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var path = Path.Combine(repo.Path, "new-file.txt");
        await File.WriteAllTextAsync(path, "new content\n");

        var dialogs = new FakeDialogService();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-untrackeddiscard.json"));
        var vm = new MainWindowViewModel(settings, dialogs);
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;

        ChangedFile file = repository.UnstagedFiles.First(f => f.RelativePath == "new-file.txt");
        repository.SelectedUnstagedFile = file;
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.OfType<DiffFileHeaderItem>()
            .Any(header => header.File.FileName == "new-file.txt"));

        DiffHunkHeaderItem hunk = repository.WorkingDiff.DisplayItems.OfType<DiffHunkHeaderItem>()
            .First(item => item.CanDiscard);
        hunk.DiscardCommand!.Execute(hunk.Hunk);

        await WaitForAsync(() => !File.Exists(path)
            && repository.UnstagedFiles.All(f => f.RelativePath != "new-file.txt"));
        Assert.False(File.Exists(path));
        Assert.DoesNotContain(repository.UnstagedFiles, f => f.RelativePath == "new-file.txt");
    }

    [AvaloniaFact]
    public async Task Remotes_can_be_added_and_removed_through_the_viewmodel()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var dialogs = new FakeDialogService();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-remotes.json"));
        var vm = new MainWindowViewModel(settings, dialogs);
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;

        dialogs.PromptResponses.Enqueue("origin");
        dialogs.PromptResponses.Enqueue("https://example.invalid/team/repo.git");
        repository.AddRemoteCommand.Execute(null);
        await WaitForAsync(() => repository.Remotes.Any(r => r.Name == "origin")
            && repository.AddRemoteCommand.CanExecute(null));

        RemoteEntry remote = repository.Remotes.First(r => r.Name == "origin");
        repository.DeleteRemoteCommand.Execute(remote);
        await WaitForAsync(() => repository.Remotes.Count == 0
            && repository.DeleteRemoteCommand.CanExecute(null)
            && repository.AddRemoteCommand.CanExecute(null));

        dialogs.PromptResponses.Enqueue("bad");
        dialogs.PromptResponses.Enqueue("not a url");
        repository.AddRemoteCommand.Execute(null);
        await WaitForAsync(() => dialogs.Messages.Any(message => message.Contains("http(s)", StringComparison.Ordinal)));
        Assert.Empty(repository.Remotes);
    }

    [AvaloniaFact]
    public async Task Interactive_rebase_rejects_a_non_ancestor_commit()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var main = await repo.Git.GetCurrentBranchAsync(repo.Path);
        Assert.True((await repo.Git.CreateBranchAsync(repo.Path, "other", null, checkout: true)).Success);
        var otherSha = await repo.CreateCommitAsync("other work", "other.txt", "other\n");
        Assert.True((await repo.Git.CheckoutAsync(repo.Path, main)).Success);

        var dialogs = new FakeDialogService();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-ancestor.json"));
        var vm = new MainWindowViewModel(settings, dialogs);
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;
        repository.SelectedCommit = repository.LogRows.First(r => r.Row.CommitId == otherSha);

        repository.InteractiveRebaseCommand.Execute(null);
        await WaitForAsync(() => dialogs.Messages.Any(message =>
            message.Contains("not an ancestor", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(dialogs.Messages, message =>
            message.Contains("not an ancestor", StringComparison.OrdinalIgnoreCase));
        Assert.False(repository.IsRebaseInProgress);
    }

    [AvaloniaFact]
    public void Interactive_rebase_dialog_uses_action_dropdowns_and_right_aligned_buttons()
    {
        var commits = new List<RebasePlanItem>
        {
            new() { Sha = "aaaa111", ShortSha = "aaaa111", Message = "first", Action = "pick" },
            new() { Sha = "bbbb222", ShortSha = "bbbb222", Message = "second", Action = "pick" },
            new() { Sha = "cccc333", ShortSha = "cccc333", Message = "third", Action = "pick" }
        };
        var dialogVm = new InteractiveRebaseWindowViewModel(commits);
        var window = new InteractiveRebaseWindow
        {
            DataContext = dialogVm,
            Width = 860,
            Height = 560
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Directory.CreateDirectory(ArtifactDirectory);
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactDirectory, "interactive-rebase.png"), PngBitmapEncoderOptions.Default);

        var combos = window.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(3, combos.Count);
        Assert.All(combos, combo => Assert.Contains("reword", (IEnumerable<string>)combo.ItemsSource!));

        Button runButton = window.GetVisualDescendants().OfType<Button>()
            .First(button => string.Equals(button.Content as string, "Run rebase", StringComparison.Ordinal));
        Button cancelButton = window.GetVisualDescendants().OfType<Button>()
            .First(button => string.Equals(button.Content as string, "Cancel", StringComparison.Ordinal));
        Point runPos = runButton.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;
        Point cancelPos = cancelButton.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;
        Assert.True(cancelPos.X > window.Width / 2);
        Assert.True(cancelPos.Y > window.Height / 2);
        Assert.True(runPos.X > cancelPos.X);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, runButton.HorizontalContentAlignment);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, cancelButton.HorizontalContentAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, runButton.VerticalContentAlignment);

        dialogVm.Rows[0].Action = "reword";
        Dispatcher.UIThread.RunJobs();
        var messageBoxes = window.GetVisualDescendants().OfType<TextBox>()
            .Where(box => box.Text is "first" or "second" or "third")
            .ToList();
        Assert.Equal(3, messageBoxes.Count);
        Assert.True(messageBoxes.Single(box => box.Text == "first").IsEnabled);
        Assert.False(messageBoxes.Single(box => box.Text == "second").IsEnabled);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Advanced_git_window_renders_all_tool_tabs()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-advanced.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        await vm.OpenPathAsync(repo.Path);

        var window = new AdvancedGitWindow(vm.Repository!) { Width = 860, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        TabControl? tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        Assert.NotNull(tabs);
        Assert.True(tabs!.Items.Count >= 10, $"expected all advanced tabs, got {tabs.Items.Count}");

        Directory.CreateDirectory(ArtifactDirectory);
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactDirectory, "advanced-git.png"), PngBitmapEncoderOptions.Default);
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_dialog_buttons_are_right_aligned_and_center_labelled()
    {
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-window.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        var window = new SettingsWindow(settings, vm) { Width = 640, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Content is "Save" or "Cancel")
            .ToList();
        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, button =>
        {
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, button.HorizontalContentAlignment);
            Point position = button.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;
            Assert.True(position.X > window.Width / 2, "dialog buttons should be right-aligned");
            Assert.True(position.Y > window.Height / 2, "dialog buttons should sit at the bottom");
        });

        Directory.CreateDirectory(ArtifactDirectory);
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactDirectory, "settings-dialog.png"), PngBitmapEncoderOptions.Default);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Selected_lines_can_be_staged()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "selected line\n");

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-lines.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        await vm.OpenPathAsync(repo.Path);

        RepositoryViewModel repository = vm.Repository!;
        ChangedFile file = repository.UnstagedFiles.First(f => f.RelativePath == "readme.txt");
        repository.SelectedUnstagedFile = file;
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.OfType<DiffFileHeaderItem>()
            .Any(h => h.File.FileName == "readme.txt"));

        DiffHunkHeaderItem hunk = repository.WorkingDiff.DisplayItems.OfType<DiffHunkHeaderItem>()
            .First(h => h.CanStage);
        HunkLine addedLine = hunk.Hunk.LineItems.First(l => l.Marker == '+');
        addedLine.Selected = true;

        Assert.NotNull(hunk.StageSelectedLinesCommand);
        Assert.True(hunk.StageSelectedLinesCommand!.CanExecute(hunk.Hunk));
        hunk.StageSelectedLinesCommand.Execute(hunk.Hunk);

        await WaitForAsync(() => repository.StagedFiles.Any(f => f.RelativePath == "readme.txt") && !repository.IsBusy);
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>()
            .Any(l => l.Line.Marker == '+' && l.Text == "selected line"));

        Assert.Contains(repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>(),
            l => l.Line.Marker == '+' && l.Text == "selected line");
    }

    [AvaloniaFact]
    public async Task Selected_lines_of_a_new_file_can_be_staged_and_unstaged()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await repo.Git.RunAsync(repo.Path, ["config", "core.autocrlf", "false"]);
        var newFile = Path.Combine(repo.Path, "lines.txt");
        await File.WriteAllTextAsync(newFile, "a\r\nb\r\nc\r\n");

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-newlines.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;

        repository.SelectedUnstagedFile = repository.UnstagedFiles.First(f => f.RelativePath == "lines.txt");
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>().Count() == 3);
        var lines = repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>().ToList();
        // A new file has no old-side line numbers, so that gutter column is hidden.
        Assert.All(lines, l => Assert.False(l.ShowBeforeLineNo));
        Assert.All(lines, l => Assert.True(l.ShowAfterLineNo));

        // Discarding selected lines of a new file must not delete the file.
        lines[0].Line.Selected = true;
        DiffHunkHeaderItem header = repository.WorkingDiff.DisplayItems.OfType<DiffHunkHeaderItem>().Single();
        header.DiscardSelectedLinesCommand!.Execute(header.Hunk);
        await WaitForAsync(() => !repository.IsBusy);
        Assert.Equal("a\r\nb\r\nc\r\n", await File.ReadAllTextAsync(newFile));

        lines[2].Line.Selected = true;
        await WaitForAsync(() => header.StageSelectedLinesCommand!.CanExecute(header.Hunk));
        header.StageSelectedLinesCommand!.Execute(header.Hunk);
        await WaitForAsync(() => repository.StagedFiles.Any(f => f.RelativePath == "lines.txt") && !repository.IsBusy);

        ProcessResult staged = await repo.Git.RunAsync(repo.Path, ["show", ":lines.txt"]);
        Assert.True(staged.Success, staged.Combined);
        Assert.Equal("a\r\nc\r\n", staged.Output);
        Assert.Contains(repository.UnstagedFiles, f => f.RelativePath == "lines.txt");

        repository.SelectedStagedFile = repository.StagedFiles.First(f => f.RelativePath == "lines.txt");
        await WaitForAsync(() => repository.WorkingDiff.CanUnstage
                                 && repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>().Count() == 2);
        var stagedLines = repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>().ToList();
        stagedLines[1].Line.Selected = true;
        DiffHunkHeaderItem stagedHeader = repository.WorkingDiff.DisplayItems.OfType<DiffHunkHeaderItem>().Single();
        await WaitForAsync(() => stagedHeader.UnstageSelectedLinesCommand!.CanExecute(stagedHeader.Hunk));
        stagedHeader.UnstageSelectedLinesCommand!.Execute(stagedHeader.Hunk);
        await WaitForAsync(() => !repository.IsBusy);

        ProcessResult afterUnstage = await repo.Git.RunAsync(repo.Path, ["show", ":lines.txt"]);
        Assert.True(afterUnstage.Success, afterUnstage.Combined);
        Assert.Equal("a\r\n", afterUnstage.Output);
        Assert.Equal("a\r\nb\r\nc\r\n", await File.ReadAllTextAsync(newFile));
    }

    [AvaloniaFact]
    public async Task Refresh_keeps_the_unstaged_selection_and_does_not_rerender_the_diff()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "staged change\n");
        await repo.Git.RunAsync(repo.Path, ["add", "readme.txt"]);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a-untracked.txt"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "b-untracked.txt"), "b\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "c-untracked.txt"), "c\n");

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-refresh-select.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;
        Assert.NotEmpty(repository.StagedFiles);

        ChangedFile selected = repository.UnstagedFiles.First(f => f.RelativePath == "b-untracked.txt");
        repository.UnstagedFiles.First(f => f.RelativePath == "c-untracked.txt").Included = true;
        repository.SelectedUnstagedFile = selected;
        await WaitForAsync(() => repository.WorkingDiff.Title.Contains("b-untracked.txt"));
        DiffDisplayItem firstDiffItem = repository.WorkingDiff.DisplayItems[0];

        // An unrelated change (what the file watcher reacts to) must not move the
        // selection to the staged list, replace the records or re-render the diff.
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "d-untracked.txt"), "d\n");
        await repository.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(selected, repository.SelectedUnstagedFile);
        Assert.Null(repository.SelectedStagedFile);
        Assert.Contains(repository.UnstagedFiles, f => f.RelativePath == "d-untracked.txt");
        Assert.True(repository.UnstagedFiles.First(f => f.RelativePath == "c-untracked.txt").Included);
        Assert.Same(firstDiffItem, repository.WorkingDiff.DisplayItems[0]);

        // Staging the selected file moves the selection to its neighbour in the
        // same list, not to the top of the staged list.
        repository.StageSelectedCommand.Execute(null);
        await WaitForAsync(() => repository.StagedFiles.Any(f => f.RelativePath == "c-untracked.txt") && !repository.IsBusy);
        // c was checked, so "Stage selected" staged the checked file and kept b selected.
        Assert.Same(selected, repository.SelectedUnstagedFile);

        repository.UnstagedFiles.ToList().ForEach(f => f.Included = false);
        repository.StageSelectedCommand.Execute(null);
        await WaitForAsync(() => repository.StagedFiles.Any(f => f.RelativePath == "b-untracked.txt") && !repository.IsBusy);
        Assert.Null(repository.SelectedStagedFile);
        Assert.Equal("d-untracked.txt", repository.SelectedUnstagedFile?.RelativePath);
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

