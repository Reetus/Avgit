using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.App.Views;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

public class DiffSelectionTests
{
    private static readonly string ArtifactDirectory =
        Path.Combine(Path.GetTempPath(), "avgit-headless");

    [AvaloniaFact]
    public void Copy_formats_keep_tabs_strip_cr_and_skip_no_newline_markers()
    {
        DiffViewModel viewModel = CreateDiff(out List<DiffLineItem> lines);
        var copied = new List<string?>();
        viewModel.CopyText = copied.Add;

        viewModel.SelectAllLines();
        viewModel.CopySelectedLines();
        Assert.Equal(
            "using System;\nold line\nnew\tline\ntail\ngone\nfresh",
            copied[^1]);
        Assert.DoesNotContain("No newline at end of file", copied[^1], StringComparison.Ordinal);

        viewModel.CopySelectedLinesWithMarkers();
        Assert.Equal(
            " using System;\n-old line\n+new\tline\n tail\n-gone\n+fresh",
            copied[^1]);

        viewModel.CopyNewVersion();
        Assert.Equal(
            "using System;\nnew\tline\ntail\nfresh",
            copied[^1]);

        viewModel.CopyOldVersion();
        Assert.Equal(
            "using System;\nold line\ntail\ngone",
            copied[^1]);

        viewModel.CopyFilePath(lines[0]);
        Assert.Equal("src/file.cs", copied[^1]);
    }

    [AvaloniaFact]
    public async Task Mouse_click_shift_ctrl_drag_and_keyboard_select_rows()
    {
        DiffViewModel viewModel = CreateDiff(out List<DiffLineItem> lines);
        var window = ShowDiff(viewModel, out ItemsControl items);
        try
        {
            await WaitForAsync(() => Container(items, lines[0]) is not null);

            Click(window, items, lines[1]);
            Assert.Equal([lines[1]], viewModel.SelectedLines);

            // Shift+click extends from the anchor.
            Click(window, items, lines[4], RawInputModifiers.Shift);
            Assert.Equal(lines.Skip(1).Take(4), viewModel.SelectedLines);

            // Ctrl+click toggles one row and keeps the rest.
            Click(window, items, lines[2], RawInputModifiers.Control);
            Assert.Equal([lines[1], lines[3], lines[4]], viewModel.SelectedLines);

            // A plain click replaces the selection.
            Click(window, items, lines[6]);
            Assert.Equal([lines[6]], viewModel.SelectedLines);

            // Dragging selects the range from the pressed row to the row under the pointer.
            Point start = PointOf(window, items, lines[1]);
            Point end = PointOf(window, items, lines[4]);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(lines.Skip(1).Take(4), viewModel.SelectedLines);

            // The staging checkbox is a separate concept from row selection.
            DiffLineItem checkLine = lines.First(line => line.ShowCheckBox && line.IsSelected);
            CheckBox checkBox = Container(items, checkLine)!.GetVisualDescendants().OfType<CheckBox>().Single();
            Point checkPoint = checkBox.TranslatePoint(new Point(checkBox.Bounds.Width / 2, checkBox.Bounds.Height / 2), window)!.Value;
            window.MouseDown(checkPoint, MouseButton.Left);
            window.MouseUp(checkPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(checkLine.Line.Selected);
            Assert.True(checkLine.IsSelected);

            // Ctrl+A and Esc work without touching the visual tree per line.
            items.Focus();
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
            Assert.All(lines, line => Assert.True(line.IsSelected));

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Empty(viewModel.SelectedLines);

            // Ctrl+C copies through the same path as the menu.
            string? clipboard = null;
            viewModel.CopyText = text => clipboard = text;
            Click(window, items, lines[0]);
            items.Focus();
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
            Assert.Equal("using System;", clipboard);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Selecting_a_replaced_or_cleared_diff_starts_empty()
    {
        DiffViewModel viewModel = CreateDiff(out List<DiffLineItem> lines);
        viewModel.SelectAllLines();
        Assert.True(viewModel.HasSelection);

        viewModel.SetFiles([SmallFile("other.txt", '+', "different")], "other", false, false, false);
        Assert.Empty(viewModel.SelectedLines);
        Assert.False(viewModel.HasSelection);
        Assert.All(lines, line => Assert.False(line.IsSelected));

        DiffLineItem replacement = viewModel.DisplayItems.OfType<DiffLineItem>().Single();
        viewModel.SelectLine(replacement, extend: false, toggle: false);
        Assert.True(viewModel.HasSelection);

        viewModel.Clear();
        Assert.Empty(viewModel.SelectedLines);
        Assert.False(viewModel.HasSelection);
        Assert.False(replacement.IsSelected);
    }

    [AvaloniaFact]
    public async Task Ctrl_A_selects_a_large_diff_without_realising_every_row()
    {
        const int lineCount = 20_000;
        var file = new FileDiff(new DiffSetup
        {
            Scope = DiffScope.WorkTreeVsIndex,
            Subject = DiffSubject.PendingFiles
        })
        {
            FileName = "large.txt",
            State = PathState.Edited
        };
        var hunk = new Hunk(file) { RawText = "@@ -1,1 +1,20000 @@" };
        for (var i = 0; i < lineCount; i++)
        {
            hunk.LineItems.Add(new HunkLine
            {
                Marker = '+',
                RawText = $"line {i}",
                Text = $"line {i}",
                OldLineNo = string.Empty,
                NewLineNo = (i + 1).ToString()
            });
        }
        file.Hunks.Add(hunk);

        var viewModel = new DiffViewModel();
        viewModel.SetFiles([file], "large", canStage: true, canUnstage: false, canDiscard: true);
        var window = ShowDiff(viewModel, out ItemsControl items);
        try
        {
            await WaitForAsync(() => Container(items, viewModel.DisplayItems.OfType<DiffLineItem>().First()) is not null);

            items.Focus();
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(lineCount, viewModel.SelectedLines.Count);
            var realised = items.GetVisualDescendants().OfType<ContentPresenter>().Count();
            Assert.True(realised < 500, $"expected virtualized rows, but {realised} containers were created");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Menu_copies_and_only_shows_working_copy_actions_when_available()
    {
        DiffViewModel viewModel = CreateDiff(out List<DiffLineItem> lines, canDiscard: false);
        string? clipboard = null;
        viewModel.CopyText = text => clipboard = text;
        var window = ShowDiff(viewModel, out ItemsControl items);
        try
        {
            await WaitForAsync(() => Container(items, lines[1]) is not null);

            Click(window, items, lines[1]);
            OpenMenu(window, items, lines[2]);
            ContextMenu menu = items.ContextMenu!;
            Assert.True(menu.IsOpen);

            Assert.True(FindMenuItem(menu, "Copy").IsVisible);
            Assert.True(FindMenuItem(menu, "Copy with +/- markers").IsVisible);
            Assert.True(FindMenuItem(menu, "Copy new version").IsVisible);
            Assert.True(FindMenuItem(menu, "Copy old version").IsVisible);
            Assert.True(FindMenuItem(menu, "Copy file path").IsVisible);

            // The synthetic diff allows staging, so only the stage action is offered.
            Assert.True(FindMenuItem(menu, "Stage selected lines").IsVisible);
            Assert.False(FindMenuItem(menu, "Unstage selected lines").IsVisible);
            Assert.False(FindMenuItem(menu, "Discard selected lines").IsVisible);

            // Right-clicking an unselected row selects only it and keeps the menu open.
            Assert.Equal([lines[2]], viewModel.SelectedLines);
            FindMenuItem(menu, "Copy").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("new\tline", clipboard);

            menu.Close();
            Dispatcher.UIThread.RunJobs();

            // Headers are not selectable and do not open the line menu.
            Point header = PointOf(window, items, viewModel.DisplayItems[0]);
            window.MouseDown(header, MouseButton.Left);
            window.MouseUp(header, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([lines[2]], viewModel.SelectedLines);
            window.MouseDown(header, MouseButton.Right);
            window.MouseUp(header, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.False(menu.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Stage_unstage_and_discard_selected_lines_through_the_menu()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-diff-menu.json"));
        settings.Settings.AutoRefresh = false;
        var dialogs = new FakeDialogService();
        var vm = new MainWindowViewModel(settings, dialogs);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        ShowFileStatus(window);
        RepositoryViewModel repository = vm.Repository!;

        // Stage one line through the context menu.
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "menu staged\n");
        await repository.RefreshAsync();
        repository.SelectedUnstagedFile = repository.UnstagedFiles.First(f => f.RelativePath == "readme.txt");
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>()
            .Any(line => line.Line.Marker == '+' && line.Text == "menu staged"));

        DiffView view = WindowDiffView(window, repository.WorkingDiff);
        ItemsControl items = DiffViewItems(view);
        DiffLineItem stagedLine = repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>()
            .First(line => line.Line.Marker == '+' && line.Text == "menu staged");
        window.CaptureRenderedFrame();
        await WaitForAsync(() => Container(items, stagedLine) is not null);
        Click(window, items, stagedLine);

        // CopyText set on the main window reaches the diff through the repository.
        string? clipboard = null;
        vm.CopyText = text => clipboard = text;
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        Assert.Equal("menu staged", clipboard);

        OpenMenu(window, items, stagedLine);
        ContextMenu menu = items.ContextMenu!;
        Assert.True(FindMenuItem(menu, "Stage selected lines").IsVisible);
        Assert.True(FindMenuItem(menu, "Discard selected lines").IsVisible);
        FindMenuItem(menu, "Stage selected lines").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Dispatcher.UIThread.RunJobs();

        await WaitForAsync(() => repository.StagedFiles.Any(f => f.RelativePath == "readme.txt") && !repository.IsBusy);
        ProcessResult staged = await repo.Git.RunAsync(repo.Path, ["show", ":readme.txt"]);
        Assert.True(staged.Success, staged.Combined);
        Assert.Contains("menu staged", staged.Output, StringComparison.Ordinal);

        // Unstage it again through the menu.
        repository.SelectedStagedFile = repository.StagedFiles.First(f => f.RelativePath == "readme.txt");
        await WaitForAsync(() => repository.WorkingDiff.CanUnstage
            && repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>()
                .Any(line => line.Line.Marker == '+' && line.Text == "menu staged"));
        DiffView stagedView = WindowDiffView(window, repository.WorkingDiff);
        ItemsControl stagedItems = DiffViewItems(stagedView);
        DiffLineItem unstageLine = repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>()
            .First(line => line.Line.Marker == '+' && line.Text == "menu staged");
        window.CaptureRenderedFrame();
        await WaitForAsync(() => Container(stagedItems, unstageLine) is not null);
        Click(window, stagedItems, unstageLine);
        OpenMenu(window, stagedItems, unstageLine);
        ContextMenu stagedMenu = stagedItems.ContextMenu!;
        Assert.True(FindMenuItem(stagedMenu, "Unstage selected lines").IsVisible);
        FindMenuItem(stagedMenu, "Unstage selected lines").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        stagedMenu.Close();
        Dispatcher.UIThread.RunJobs();

        await WaitForAsync(() => !repository.IsBusy
            && repository.StagedFiles.All(f => f.RelativePath != "readme.txt"));
        ProcessResult unstaged = await repo.Git.RunAsync(repo.Path, ["show", ":readme.txt"]);
        Assert.True(unstaged.Success, unstaged.Combined);
        Assert.DoesNotContain("menu staged", unstaged.Output, StringComparison.Ordinal);

        // Discard selected lines through the menu; the confirmation is kept.
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "menu discarded\n");
        await repository.RefreshAsync();
        repository.SelectedUnstagedFile = repository.UnstagedFiles.First(f => f.RelativePath == "readme.txt");
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>()
            .Any(line => line.Line.Marker == '+' && line.Text == "menu discarded"));
        DiffView discardView = WindowDiffView(window, repository.WorkingDiff);
        ItemsControl discardItems = DiffViewItems(discardView);
        DiffLineItem discardLine = repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>()
            .First(line => line.Line.Marker == '+' && line.Text == "menu discarded");
        window.CaptureRenderedFrame();
        await WaitForAsync(() => Container(discardItems, discardLine) is not null);
        Click(window, discardItems, discardLine);
        OpenMenu(window, discardItems, discardLine);
        ContextMenu discardMenu = discardItems.ContextMenu!;
        Assert.True(FindMenuItem(discardMenu, "Discard selected lines").IsVisible);
        FindMenuItem(discardMenu, "Discard selected lines").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        discardMenu.Close();
        Dispatcher.UIThread.RunJobs();

        await WaitForAsync(() => !repository.IsBusy
            && !File.ReadAllText(Path.Combine(repo.Path, "readme.txt")).Contains("menu discarded", StringComparison.Ordinal));
        Assert.Contains(dialogs.Confirmations, confirmation =>
            confirmation.Message.Contains("Discard the selected changed line?", StringComparison.Ordinal));
        Assert.Empty(dialogs.Messages);

        // A new file still has no line-level discard in the menu.
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "new-file.txt"), "brand new\n");
        await repository.RefreshAsync();
        repository.SelectedUnstagedFile = repository.UnstagedFiles.First(f => f.RelativePath == "new-file.txt");
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>()
            .Any(line => line.Line.Marker == '+' && line.Text == "brand new"));
        DiffView newView = WindowDiffView(window, repository.WorkingDiff);
        ItemsControl newItems = DiffViewItems(newView);
        DiffLineItem newLine = repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>()
            .First(line => line.Line.Marker == '+' && line.Text == "brand new");
        window.CaptureRenderedFrame();
        await WaitForAsync(() => Container(newItems, newLine) is not null);
        Click(window, newItems, newLine);
        OpenMenu(window, newItems, newLine);
        Assert.False(FindMenuItem(newItems.ContextMenu!, "Discard selected lines").IsVisible);
        newItems.ContextMenu!.Close();

        window.Close();
    }

    [AvaloniaFact]
    public async Task Checkbox_clicks_do_not_change_row_selection_and_still_stage_lines()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "readme.txt"), "checkbox line\n");
        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-diff-checkbox.json"));
        settings.Settings.AutoRefresh = false;
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        ShowFileStatus(window);
        RepositoryViewModel repository = vm.Repository!;

        repository.SelectedUnstagedFile = repository.UnstagedFiles.First(f => f.RelativePath == "readme.txt");
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>()
            .Any(line => line.Line.Marker == '+' && line.Text == "checkbox line"));

        DiffView view = WindowDiffView(window, repository.WorkingDiff);
        ItemsControl items = DiffViewItems(view);
        DiffLineItem line = repository.WorkingDiff.DisplayItems.OfType<DiffLineItem>()
            .First(item => item.Line.Marker == '+' && item.Text == "checkbox line");
        window.CaptureRenderedFrame();
        await WaitForAsync(() => Container(items, line) is not null);
        CheckBox checkBox = Container(items, line)!.GetVisualDescendants().OfType<CheckBox>().Single();
        Point point = checkBox.TranslatePoint(new Point(checkBox.Bounds.Width / 2, checkBox.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(line.Line.Selected);
        Assert.False(line.IsSelected);
        Assert.Empty(repository.WorkingDiff.SelectedLines);
        DiffHunkHeaderItem header = repository.WorkingDiff.DisplayItems.OfType<DiffHunkHeaderItem>()
            .First(h => h.CanStage);
        await WaitForAsync(() => header.StageSelectedLinesCommand!.CanExecute(header.Hunk));
        header.StageSelectedLinesCommand!.Execute(header.Hunk);
        await WaitForAsync(() => repository.StagedFiles.Any(f => f.RelativePath == "readme.txt") && !repository.IsBusy);
        Assert.Contains("checkbox line", (await repo.Git.RunAsync(repo.Path, ["show", ":readme.txt"])).Output, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Selection_and_open_menu_render_for_a_screenshot()
    {
        DiffViewModel viewModel = CreateDiff(out List<DiffLineItem> lines);
        viewModel.CopyText = _ => { };
        var window = ShowDiff(viewModel, out ItemsControl items, width: 900, height: 520);
        try
        {
            await WaitForAsync(() => Container(items, lines[1]) is not null);
            Click(window, items, lines[1]);
            Click(window, items, lines[3], RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();

            Directory.CreateDirectory(ArtifactDirectory);
            window.CaptureRenderedFrame()!.Save(
                Path.Combine(ArtifactDirectory, "diff-selection.png"), PngBitmapEncoderOptions.Default);

            OpenMenu(window, items, lines[3]);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()!.Save(
                Path.Combine(ArtifactDirectory, "diff-selection-menu.png"), PngBitmapEncoderOptions.Default);

            items.ContextMenu!.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Discarding_a_selection_across_hunks_asks_once()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        await repo.Git.RunAsync(repo.Path, ["config", "core.autocrlf", "false"]);
        var path = Path.Combine(repo.Path, "long.txt");
        List<string> original = [.. Enumerable.Range(1, 40).Select(i => $"line {i}")];
        await File.WriteAllTextAsync(path, string.Join("\n", original) + "\n");
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "long file");

        // Two hunks; the first adds lines, so the second's line numbers shift.
        List<string> changed = [.. original];
        changed.Insert(3, "top added A");
        changed.Insert(4, "top added B");
        changed[changed.IndexOf("line 35")] = "line 35 changed";
        var changedText = string.Join("\n", changed) + "\n";
        await File.WriteAllTextAsync(path, changedText);

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-diff-discard-once.json"));
        settings.Settings.AutoRefresh = false;
        var dialogs = new FakeDialogService();
        using var repository = new RepositoryViewModel(repo.Git, repo.Path, settings, dialogs);
        await repository.LoadAsync();
        repository.SelectedUnstagedFile = repository.UnstagedFiles.First(f => f.RelativePath == "long.txt");
        await WaitForAsync(() => repository.WorkingDiff.DisplayItems.OfType<DiffHunkHeaderItem>().Count() == 2);

        DiffViewModel diff = repository.WorkingDiff;
        void SelectAcrossHunks()
        {
            List<DiffLineItem> lines = [.. diff.DisplayItems.OfType<DiffLineItem>()];
            diff.SelectLine(lines.First(l => l.Text == "top added A"), extend: false, toggle: false);
            diff.SelectLine(lines.First(l => l.Line.Marker == '-' && l.Text == "line 35"), extend: false, toggle: true);
            diff.SelectLine(lines.First(l => l.Line.Marker == '+' && l.Text == "line 35 changed"), extend: false, toggle: true);
        }

        // Cancelling discards nothing.
        SelectAcrossHunks();
        dialogs.ConfirmResult = false;
        await diff.DiscardSelectedLinesAsync();
        Assert.Single(dialogs.Confirmations);
        Assert.Equal(changedText, await File.ReadAllTextAsync(path));

        // Confirming asks once for both hunks and discards exactly the selected lines.
        dialogs.Confirmations.Clear();
        dialogs.ConfirmResult = true;
        await diff.DiscardSelectedLinesAsync();
        await WaitForAsync(() => !repository.IsBusy);
        (string Title, string Message) confirmation = Assert.Single(dialogs.Confirmations);
        Assert.Equal("Discard the 3 selected changed lines? This cannot be undone.", confirmation.Message);
        List<string> expected = [.. original];
        expected.Insert(3, "top added B");
        Assert.Equal(string.Join("\n", expected) + "\n", await File.ReadAllTextAsync(path));
        Assert.Empty(dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task Dragging_past_the_bottom_edge_scrolls_and_keeps_selecting()
    {
        var file = new FileDiff(new DiffSetup { Scope = DiffScope.WorkTreeVsIndex, Subject = DiffSubject.PendingFiles })
        {
            FileName = "long.txt",
            State = PathState.Edited
        };
        var hunk = new Hunk(file) { RawText = "@@ -1,0 +1,300 @@" };
        for (var i = 1; i <= 300; i++)
        {
            hunk.LineItems.Add(Line('+', $"added {i}", string.Empty, i.ToString()));
        }

        file.Hunks.Add(hunk);
        var viewModel = new DiffViewModel();
        viewModel.SetFiles([file], "diff", canStage: false, canUnstage: false, canDiscard: false);
        List<DiffLineItem> lines = [.. viewModel.DisplayItems.OfType<DiffLineItem>()];

        Window window = ShowDiff(viewModel, out ItemsControl items, height: 300);
        try
        {
            await WaitForAsync(() => Container(items, lines[0]) is not null);
            ScrollViewer scroller = items.GetVisualDescendants().OfType<ScrollViewer>().First();
            var visibleRows = (int)(scroller.Viewport.Height / 22);

            // Press on the first line, then hold the pointer below the list.
            Point start = PointOf(window, items, lines[0]);
            Point below = items.TranslatePoint(new Point(80, items.Bounds.Height + 40), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(below, RawInputModifiers.LeftMouseButton);
            await WaitForAsync(() => scroller.Offset.Y > 200 && viewModel.SelectedLines.Count > visibleRows + 5);

            // The selection is one contiguous range from the anchor down to the bottom edge.
            IReadOnlyList<DiffLineItem> selected = viewModel.SelectedLines;
            Assert.Same(lines[0], selected[0]);
            Assert.Equal(lines.Take(selected.Count), selected);

            // Releasing stops the scrolling.
            window.MouseUp(below, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var stoppedAt = scroller.Offset.Y;
            await Task.Delay(200);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(stoppedAt, scroller.Offset.Y);
        }
        finally
        {
            window.Close();
        }
    }

    private static DiffViewModel CreateDiff(out List<DiffLineItem> lines, bool canDiscard = true)
    {
        var file = new FileDiff(new DiffSetup
        {
            Scope = DiffScope.WorkTreeVsIndex,
            Subject = DiffSubject.PendingFiles
        })
        {
            FileName = "src/file.cs",
            State = PathState.Edited
        };

        var first = new Hunk(file) { RawText = "@@ -1,3 +1,4 @@" };
        first.LineItems.Add(Line(' ', "using System;\r", "1", "1"));
        first.LineItems.Add(Line('-', "old line", "2", string.Empty));
        first.LineItems.Add(Line('+', "new\tline", string.Empty, "2"));
        first.LineItems.Add(Line('\\', " No newline at end of file", string.Empty, string.Empty));
        file.Hunks.Add(first);

        var second = new Hunk(file) { RawText = "@@ -10,2 +11,3 @@" };
        second.LineItems.Add(Line(' ', "tail", "10", "11"));
        second.LineItems.Add(Line('-', "gone", "11", string.Empty));
        second.LineItems.Add(Line('+', "fresh", string.Empty, "12"));
        file.Hunks.Add(second);

        var viewModel = new DiffViewModel();
        viewModel.SetFiles([file], "diff", canStage: true, canUnstage: false, canDiscard: canDiscard);
        lines = viewModel.DisplayItems.OfType<DiffLineItem>().ToList();
        return viewModel;
    }

    private static FileDiff SmallFile(string name, char marker, string text)
    {
        var file = new FileDiff(new DiffSetup
        {
            Scope = DiffScope.WorkTreeVsIndex,
            Subject = DiffSubject.PendingFiles
        })
        {
            FileName = name,
            State = PathState.Edited
        };
        var hunk = new Hunk(file) { RawText = "@@ -1,1 +1,1 @@" };
        hunk.LineItems.Add(Line(marker, text, "1", "1"));
        file.Hunks.Add(hunk);
        return file;
    }

    private static HunkLine Line(char marker, string raw, string oldLineNo, string newLineNo) => new()
    {
        Marker = marker,
        RawText = raw,
        Text = raw.TrimEnd('\r'),
        OldLineNo = oldLineNo,
        NewLineNo = newLineNo
    };

    private static Window ShowDiff(DiffViewModel viewModel, out ItemsControl items, double width = 760, double height = 480)
    {
        var view = new DiffView { DataContext = viewModel };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        items = DiffViewItems(view);
        return window;
    }

    private static void ShowFileStatus(Window window)
    {
        TabControl tabs = window.GetVisualDescendants().OfType<TabControl>()
            .First(tab => tab.Items.OfType<TabItem>()
                .Any(item => string.Equals(item.Header as string, "File Status", StringComparison.Ordinal)));
        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    private static ItemsControl DiffViewItems(DiffView view)
        => view.FindControl<ItemsControl>("DiffItems")
           ?? throw new InvalidOperationException("The diff ItemsControl was not found.");

    private static DiffView WindowDiffView(Window window, DiffViewModel viewModel)
        => window.GetVisualDescendants().OfType<DiffView>()
            .First(view => ReferenceEquals(view.DataContext, viewModel));

    private static ContentPresenter? Container(ItemsControl items, object item)
        => items.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(presenter => ReferenceEquals(presenter.DataContext, item));

    private static Point PointOf(Window window, ItemsControl items, object item)
    {
        ContentPresenter presenter = Container(items, item)
            ?? throw new InvalidOperationException("The diff row is not realised.");
        Point point = presenter.TranslatePoint(new Point(80, presenter.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The diff row is not in the window.");
        return point;
    }

    private static void Click(Window window, ItemsControl items, DiffLineItem line, RawInputModifiers modifiers = 0)
    {
        Point point = PointOf(window, items, line);
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static void OpenMenu(Window window, ItemsControl items, DiffLineItem line)
    {
        Point point = PointOf(window, items, line);
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    private static MenuItem FindMenuItem(ContextMenu menu, string header)
        => menu.Items.OfType<MenuItem>().Single(item => string.Equals(item.Header as string, header, StringComparison.Ordinal));

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
