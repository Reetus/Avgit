using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.App.Views;

/// <summary>
/// Tools for Git features outside the daily log/stage/commit flow. Optional
/// helpers (git-lfs, git-flow, git-svn) are invoked normally and their absence is
/// reported by the standard error dialog.
/// </summary>
public sealed class AdvancedGitWindow : Window
{
    private readonly RepositoryViewModel _repository;
    private readonly DialogService _dialogs;
    private readonly TextBlock _status;
    private readonly ListBox _submodules = new() { Height = 220 };
    private readonly ListBox _lfsPatterns = new() { Height = 150 };
    private readonly ListBox _lfsLocks = new() { Height = 120 };
    private readonly ListBox _worktrees = new() { Height = 200 };
    private readonly ListBox _customActions = new() { Height = 150 };
    private IReadOnlyList<WorktreeInfo> _worktreeItems = [];
    private IReadOnlyList<SubmoduleInfo> _submoduleItems = [];
    private string _lastOutput = string.Empty;

    public AdvancedGitWindow(RepositoryViewModel repository)
    {
        _repository = repository;
        _dialogs = new DialogService { Owner = this };
        Title = $"Advanced Git - {repository.Name}";
        Width = 860;
        Height = 640;
        MinWidth = 700;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
        _status = new TextBlock
        {
            Text = "Choose an operation.",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#A0A0A0")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 4)
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                BuildTabs(),
                _status
            }
        };
        Grid.SetRow(_status, 1);
        _ = LoadAdvancedListsAsync();
    }

    private async Task LoadAdvancedListsAsync()
    {
        // Run sequentially because the service exposes one LastError slot.
        await RefreshSubmodulesAsync();
        await RefreshLfsAsync();
        await RefreshLfsLocksAsync();
        await RefreshWorktreesAsync();
    }

    private Control BuildTabs()
    {
        var tabs = new TabControl
        {
            Margin = new Thickness(10, 8, 10, 0)
        };
        tabs.Items.Add(Tab("Submodules", BuildSubmodules()));
        tabs.Items.Add(Tab("LFS", BuildLfs()));
        tabs.Items.Add(Tab("Flow", BuildFlow()));
        tabs.Items.Add(Tab("Subtree", BuildSubtree()));
        tabs.Items.Add(Tab("SVN", BuildSvn()));
        tabs.Items.Add(Tab("Bisect", BuildBisect()));
        tabs.Items.Add(Tab("Worktrees", BuildWorktrees()));
        tabs.Items.Add(Tab("Patches", BuildPatches()));
        tabs.Items.Add(Tab("Diagnostics", BuildDiagnostics()));
        tabs.Items.Add(Tab("Extras", BuildExtras()));
        tabs.Items.Add(Tab("Custom", BuildCustom()));
        return tabs;
    }

    private static TabItem Tab(string header, Control content) => new() { Header = header, Content = content };

    private static Control Page(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        foreach (Control child in children)
        {
            panel.Children.Add(child);
        }

        return new ScrollViewer { Content = panel };
    }

    private static Control Row(params Control[] children)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (Control child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = text,
            Classes = { "toolbar" },
            Margin = new Thickness(0, 0, 6, 6)
        };
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _status.Text = ex.Message;
            }
        };
        return button;
    }

    private Task<ProcessResult> RunShellAsync(string command)
        => ProcessRunner.RunShellAsync(_repository.Path, command);

    private async Task RunAsync(string label, Func<Task<ProcessResult>> action)
    {
        _status.Text = label + "...";
        try
        {
            ProcessResult result = await action();
            _lastOutput = result.Combined;
            if (!result.Success)
            {
                _status.Text = label + " failed.";
                await _dialogs.ShowMessageAsync(label + " failed", result.Combined);
            }
            else
            {
                _status.Text = string.IsNullOrWhiteSpace(result.Output) ? label + " completed." : result.Output.Trim();
            }
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            await _dialogs.ShowMessageAsync(label + " failed", ex.Message);
        }
    }

    private async Task RunSimpleAsync(string label, Func<Task> action)
    {
        _status.Text = label + "...";
        try
        {
            await action();
            _status.Text = label + " completed.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            await _dialogs.ShowMessageAsync(label + " failed", ex.Message);
        }
    }

    private Control BuildSubmodules()
    {
        Button add = Button("Add...", async () =>
        {
            var url = await _dialogs.PromptAsync("Add submodule", "Repository URL");
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var path = await _dialogs.PromptAsync("Add submodule", "Local path");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await RunAsync("Add submodule", () => _repository.Advanced.SubmoduleAddAsync(url, path));
            await RefreshSubmodulesAsync();
        });
        return Page(
            new TextBlock { Text = "Submodules", FontWeight = FontWeight.SemiBold },
            _submodules,
            Row(
                Button("Refresh", RefreshSubmodulesAsync),
                Button("Init", () => RunAsync("Init submodules", _repository.Advanced.SubmoduleInitAsync)),
                Button("Update", async () => { await RunAsync("Update submodules", _repository.Advanced.SubmoduleUpdateAsync); await RefreshSubmodulesAsync(); }),
                Button("Sync", () => RunAsync("Sync submodules", _repository.Advanced.SubmoduleSyncAsync)),
                Button("Deinit", async () =>
                {
                    var selected = _submodules.SelectedIndex;
                    if (selected < 0 || selected >= _submoduleItems.Count)
                    {
                        return;
                    }

                    SubmoduleInfo item = _submoduleItems[selected];
                    if (!await _dialogs.ConfirmAsync("Deinit submodule",
                            $"Deinit '{item.Path}'? Uncommitted changes in the submodule working tree will be discarded."))
                    {
                        return;
                    }

                    await RunAsync("Deinit submodule",
                        () => _repository.Advanced.SubmoduleDeinitAsync(item.Path));
                    await RefreshSubmodulesAsync();
                }),
                Button("Remove", async () =>
                {
                    var selected = _submodules.SelectedIndex;
                    if (selected < 0 || selected >= _submoduleItems.Count)
                    {
                        return;
                    }

                    SubmoduleInfo item = _submoduleItems[selected];
                    if (!await _dialogs.ConfirmAsync("Remove submodule",
                            $"Remove '{item.Path}' from the index and working tree?"))
                    {
                        return;
                    }

                    await RunAsync("Remove submodule",
                        () => _repository.Advanced.SubmoduleRemoveAsync(item.Path));
                    await RefreshSubmodulesAsync();
                }),
                add));
    }

    private async Task RefreshSubmodulesAsync()
    {
        try
        {
            IReadOnlyList<SubmoduleInfo> list = await _repository.Advanced.GetSubmodulesAsync();
            if (!string.IsNullOrEmpty(_repository.Advanced.LastError))
            {
                _status.Text = _repository.Advanced.LastError;
                return;
            }
            _submoduleItems = list;
            _submodules.ItemsSource = list
                .Select(item => $"{item.State} {item.Sha[..Math.Min(8, item.Sha.Length)]}  {item.Path}  {item.Description}")
                .ToList();
            _status.Text = $"{list.Count} submodule(s).";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private Control BuildLfs()
    {
        return Page(
            new TextBlock { Text = "Git LFS", FontWeight = FontWeight.SemiBold },
            _lfsPatterns,
            new TextBlock { Text = "Locks", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
            _lfsLocks,
            Row(
                Button("Refresh locks", RefreshLfsLocksAsync),
                Button("Lock...", async () =>
                {
                    var file = await _dialogs.PromptAsync("Lock with Git LFS", "File path");
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        return;
                    }

                    await RunAsync("Lock " + file, () => _repository.Advanced.LfsLockAsync(file));
                    await RefreshLfsLocksAsync();
                }),
                Button("Unlock...", async () =>
                {
                    var file = await _dialogs.PromptAsync("Unlock with Git LFS", "File path");
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        return;
                    }

                    await RunAsync("Unlock " + file, () => _repository.Advanced.LfsUnlockAsync(file));
                    await RefreshLfsLocksAsync();
                })),
            Row(
                Button("Refresh patterns", RefreshLfsAsync),
                Button("Init", () => RunAsync("Git LFS init", _repository.Advanced.LfsInstallAsync)),
                Button("Track...", async () =>
                {
                    var pattern = await _dialogs.PromptAsync("Track with Git LFS", "Pattern", "*.bin");
                    if (string.IsNullOrWhiteSpace(pattern))
                    {
                        return;
                    }

                    await RunAsync("Track " + pattern, () => _repository.Advanced.LfsTrackAsync(pattern));
                    await RefreshLfsAsync();
                }),
                Button("Untrack...", async () =>
                {
                    var pattern = await _dialogs.PromptAsync("Untrack Git LFS", "Pattern");
                    if (string.IsNullOrWhiteSpace(pattern))
                    {
                        return;
                    }

                    await RunAsync("Untrack " + pattern, () => _repository.Advanced.LfsUntrackAsync(pattern));
                    await RefreshLfsAsync();
                }),
                Button("Fetch", () => RunAsync("Git LFS fetch", _repository.Advanced.LfsFetchAsync)),
                Button("Pull", () => RunAsync("Git LFS pull", _repository.Advanced.LfsPullAsync))));
    }

    private async Task RefreshLfsAsync()
    {
        try
        {
            IReadOnlyList<string> patterns = await _repository.Advanced.GetLfsPatternsAsync();
            if (!string.IsNullOrEmpty(_repository.Advanced.LastError))
            {
                _status.Text = _repository.Advanced.LastError;
                return;
            }
            _lfsPatterns.ItemsSource = patterns;
            _status.Text = $"{patterns.Count} tracked LFS pattern(s).";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async Task RefreshLfsLocksAsync()
    {
        try
        {
            IReadOnlyList<string> locks = await _repository.Advanced.GetLfsLocksAsync();
            if (!string.IsNullOrEmpty(_repository.Advanced.LastError))
            {
                _status.Text = _repository.Advanced.LastError;
                return;
            }
            _lfsLocks.ItemsSource = locks;
            _status.Text = $"{locks.Count} LFS lock(s).";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private Control BuildFlow()
    {
        return Page(
            new TextBlock { Text = "Git Flow", FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "Requires git-flow (AVH edition).", Classes = { "muted" }, FontSize = 11 },
            Row(
                Button("Init", () => RunAsync("Git Flow init", _repository.Advanced.FlowInitAsync)),
                Button("Start...", async () =>
                {
                    var kind = await _dialogs.PromptAsync("Git Flow", "Branch type (feature/release/hotfix/support)", "feature");
                    if (string.IsNullOrWhiteSpace(kind))
                    {
                        return;
                    }

                    var name = await _dialogs.PromptAsync("Git Flow", "Name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return;
                    }

                    await RunAsync($"Start {kind}/{name}", () => _repository.Advanced.FlowStartAsync(kind, name));
                }),
                Button("Finish...", async () =>
                {
                    var kind = await _dialogs.PromptAsync("Git Flow", "Branch type (feature/release/hotfix/support)", "feature");
                    if (string.IsNullOrWhiteSpace(kind))
                    {
                        return;
                    }

                    kind = kind.Trim().ToLowerInvariant();
                    var name = await _dialogs.PromptAsync("Git Flow", "Name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return;
                    }

                    string? tagMessage = null;
                    if (kind is "release" or "hotfix")
                    {
                        tagMessage = await _dialogs.PromptAsync("Git Flow", "Tag message", $"Finish {kind} {name}");
                        if (tagMessage is null)
                        {
                            return;
                        }
                    }
                    await RunAsync($"Finish {kind}/{name}", () => _repository.Advanced.FlowFinishAsync(kind, name, tagMessage));
                }),
                Button("List...", async () =>
                {
                    var kind = await _dialogs.PromptAsync("Git Flow", "Branch type", "feature");
                    if (string.IsNullOrWhiteSpace(kind))
                    {
                        return;
                    }

                    await RunAsync($"List {kind}", () => _repository.Advanced.FlowListAsync(kind));
                })));
    }

    private Control BuildSubtree()
    {
        return Page(
            new TextBlock { Text = "Git Subtree", FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "Examples: prefix=libs/foo, remote URL, ref=main.", Classes = { "muted" }, FontSize = 11 },
            Row(
                Button("Add...", async () =>
                {
                    var prefix = await _dialogs.PromptAsync("Subtree add", "Prefix");
                    if (string.IsNullOrWhiteSpace(prefix))
                    {
                        return;
                    }

                    var url = await _dialogs.PromptAsync("Subtree add", "Repository URL");
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return;
                    }

                    var reference = await _dialogs.PromptAsync("Subtree add", "Reference", "main");
                    if (string.IsNullOrWhiteSpace(reference))
                    {
                        return;
                    }

                    (string? Value, bool Flag)? squash = await _dialogs.PromptWithCheckBoxAsync("Subtree add", "Squash history?", "", "Squash history", true);
                    if (squash is null)
                    {
                        return;
                    }

                    await RunAsync("Subtree add", () => _repository.Advanced.SubtreeAddAsync(prefix, url, reference, squash.Value.Flag));
                }),
                Button("Pull...", async () =>
                {
                    var prefix = await _dialogs.PromptAsync("Subtree pull", "Prefix");
                    if (string.IsNullOrWhiteSpace(prefix))
                    {
                        return;
                    }

                    var remote = await _dialogs.PromptAsync("Subtree pull", "Remote or URL", "origin");
                    if (string.IsNullOrWhiteSpace(remote))
                    {
                        return;
                    }

                    var reference = await _dialogs.PromptAsync("Subtree pull", "Reference", "main");
                    if (string.IsNullOrWhiteSpace(reference))
                    {
                        return;
                    }

                    (string? Value, bool Flag)? squash = await _dialogs.PromptWithCheckBoxAsync("Subtree pull", "Squash history?", "", "Squash history", true);
                    if (squash is null)
                    {
                        return;
                    }

                    await RunAsync("Subtree pull", () => _repository.Advanced.SubtreePullAsync(prefix, remote, reference, squash.Value.Flag));
                }),
                Button("Push...", async () =>
                {
                    var prefix = await _dialogs.PromptAsync("Subtree push", "Prefix");
                    if (string.IsNullOrWhiteSpace(prefix))
                    {
                        return;
                    }

                    var remote = await _dialogs.PromptAsync("Subtree push", "Remote or URL", "origin");
                    if (string.IsNullOrWhiteSpace(remote))
                    {
                        return;
                    }

                    var reference = await _dialogs.PromptAsync("Subtree push", "Reference", "main");
                    if (string.IsNullOrWhiteSpace(reference))
                    {
                        return;
                    }

                    await RunAsync("Subtree push", () => _repository.Advanced.SubtreePushAsync(prefix, remote, reference));
                })));
    }

    private Control BuildSvn()
    {
        return Page(
            new TextBlock { Text = "Git-SVN bridge", FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "Requires a git-svn initialized repository.", Classes = { "muted" }, FontSize = 11 },
            Row(
                Button("Fetch", () => RunAsync("git svn fetch", _repository.Advanced.SvnFetchAsync)),
                Button("Rebase", () => RunAsync("git svn rebase", _repository.Advanced.SvnRebaseAsync)),
                Button("Dcommit", () => RunAsync("git svn dcommit", _repository.Advanced.SvnDcommitAsync)),
                Button("Info", () => RunAsync("git svn info", _repository.Advanced.SvnInfoAsync))));
    }

    private Control BuildBisect()
    {
        return Page(
            new TextBlock { Text = "Bisect", FontWeight = FontWeight.SemiBold },
            Row(
                Button("Start...", async () =>
                {
                    var bad = await _dialogs.PromptAsync("Bisect", "Bad revision", "HEAD");
                    if (bad is null)
                    {
                        return;
                    }

                    var good = await _dialogs.PromptAsync("Bisect", "Good revision (optional)", "");
                    if (good is null)
                    {
                        return;
                    }

                    await RunAsync("Start bisect", () => _repository.Advanced.BisectStartAsync(bad, string.IsNullOrWhiteSpace(good) ? null : good));
                }),
                Button("Good", () => RunAsync("Bisect good", () => _repository.Advanced.BisectGoodAsync(_repository.SelectedCommit?.Row.CommitId))),
                Button("Bad", () => RunAsync("Bisect bad", () => _repository.Advanced.BisectBadAsync(_repository.SelectedCommit?.Row.CommitId))),
                Button("Reset", () => RunAsync("Bisect reset", _repository.Advanced.BisectResetAsync)),
                Button("Log", async () =>
                {
                    ProcessResult result = await _repository.Advanced.BisectLogAsync();
                    await _dialogs.ShowTextAsync("Bisect log", result.Combined);
                })));
    }

    private Control BuildWorktrees()
    {
        return Page(
            new TextBlock { Text = "Linked worktrees", FontWeight = FontWeight.SemiBold },
            _worktrees,
            Row(
                Button("Refresh", RefreshWorktreesAsync),
                Button("Add...", async () =>
                {
                    var path = await _dialogs.PromptAsync("Add worktree", "Path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return;
                    }
                    // An empty answer means "none"; Cancel (null) aborts.
                    var branch = await _dialogs.PromptAsync("Add worktree", "New branch (optional)", "");
                    if (branch is null)
                    {
                        return;
                    }

                    var start = await _dialogs.PromptAsync("Add worktree", "Start point (optional)", "");
                    if (start is null)
                    {
                        return;
                    }

                    await RunAsync("Add worktree", () => _repository.Advanced.WorktreeAddAsync(path,
                        string.IsNullOrWhiteSpace(branch) ? null : branch,
                        string.IsNullOrWhiteSpace(start) ? null : start));
                    await RefreshWorktreesAsync();
                }),
                Button("Remove...", async () =>
                {
                    var selected = _worktrees.SelectedIndex;
                    if (selected < 0 || selected >= _worktreeItems.Count)
                    {
                        return;
                    }

                    WorktreeInfo item = _worktreeItems[selected];
                    if (!await _dialogs.ConfirmAsync("Remove worktree", $"Remove worktree '{item.Path}'?"))
                    {
                        return;
                    }

                    await RunAsync("Remove worktree", () => _repository.Advanced.WorktreeRemoveAsync(item.Path, true));
                    await RefreshWorktreesAsync();
                }),
                Button("Open folder", () =>
                {
                    var selected = _worktrees.SelectedIndex;
                    if (selected < 0 || selected >= _worktreeItems.Count)
                    {
                        return Task.CompletedTask;
                    }

                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = _worktreeItems[selected].Path, UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        _status.Text = ex.Message;
                    }
                    return Task.CompletedTask;
                })));
    }

    private async Task RefreshWorktreesAsync()
    {
        try
        {
            _worktreeItems = await _repository.Advanced.GetWorktreesAsync();
            if (!string.IsNullOrEmpty(_repository.Advanced.LastError))
            {
                _status.Text = _repository.Advanced.LastError;
                return;
            }
            _worktrees.ItemsSource = _worktreeItems
                .Select(item => $"{item.Path}  [{item.Branch}{(item.IsDetached ? " detached" : "")}]")
                .ToList();
            _status.Text = $"{_worktreeItems.Count} worktree(s).";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private Control BuildPatches()
    {
        return Page(
            new TextBlock { Text = "Patches and archives", FontWeight = FontWeight.SemiBold },
            Row(
                Button("Create patch for selected commit...", async () =>
                {
                    var sha = _repository.SelectedCommit?.Row.CommitId;
                    if (sha is null) { await _dialogs.ShowMessageAsync("Patch", "Select a commit first."); return; }
                    var file = await _dialogs.PickSaveFileAsync("Save patch", "commit.patch");
                    if (file is null)
                    {
                        return;
                    }

                    await RunSimpleAsync("Create patch", async () =>
                        await File.WriteAllTextAsync(file, await _repository.Advanced.CreatePatchAsync(sha)));
                }),
                Button("Apply patch...", async () =>
                {
                    var file = await _dialogs.PickFileAsync("Apply patch");
                    if (file is null)
                    {
                        return;
                    }

                    await RunAsync("Apply patch", () => _repository.Advanced.ApplyPatchAsync(file));
                }),
                Button("Export archive...", async () =>
                {
                    var sha = _repository.SelectedCommit?.Row.CommitId;
                    if (sha is null) { await _dialogs.ShowMessageAsync("Archive", "Select a commit first."); return; }
                    var file = await _dialogs.PickSaveFileAsync("Export archive", "repo.zip");
                    if (file is null)
                    {
                        return;
                    }

                    await RunAsync("Export archive", () => _repository.Advanced.CreateArchiveAsync(sha, file));
                }),
                Button("Push tag...", async () =>
                {
                    var tag = await _dialogs.PromptAsync("Push tag", "Tag name");
                    if (string.IsNullOrWhiteSpace(tag))
                    {
                        return;
                    }

                    var remote = await _dialogs.PromptAsync("Push tag", "Remote", "origin");
                    if (remote is null)
                    {
                        return;
                    }

                    await RunAsync("Push tag", () => _repository.Advanced.TagPushAsync(tag,
                        string.IsNullOrWhiteSpace(remote) ? null : remote));
                }),
                Button("Rebase keeping merges...", async () =>
                {
                    var onto = await _dialogs.PromptAsync("Rebase preserving merges", "Onto", "HEAD~1");
                    if (string.IsNullOrWhiteSpace(onto))
                    {
                        return;
                    }

                    await RunAsync("Rebase preserving merges", () => _repository.Advanced.RebasePreservingMergesAsync(onto));
                }),
                Button("Rebase root keeping merges", async () =>
                {
                    if (!await _dialogs.ConfirmAsync("Rebase from root",
                            "Rewrite the entire history from the root commit? This cannot be undone."))
                    {
                        return;
                    }

                    await RunAsync("Rebase from root", _repository.Advanced.RebaseRootPreservingMergesAsync);
                })));
    }

    private Control BuildDiagnostics()
    {
        return Page(
            new TextBlock { Text = "Diagnostics", FontWeight = FontWeight.SemiBold },
            Row(
                Button("Reflog", async () =>
                {
                    IReadOnlyList<ReflogEntry> entries = await _repository.Advanced.GetReflogAsync();
                    if (!string.IsNullOrEmpty(_repository.Advanced.LastError))
                    {
                        await _dialogs.ShowMessageAsync("Reflog", _repository.Advanced.LastError);
                        return;
                    }
                    var text = string.Join("\n", entries.Select(e => $"{e.ShortSha}  {e.Selector}  {e.Date}  {e.Subject}"));
                    await _dialogs.ShowTextAsync("Reflog", text);
                }),
                Button("File history...", async () =>
                {
                    var file = await _dialogs.PromptAsync("File history", "Repository-relative file", _repository.SelectedCommitFile?.RelativePath ?? "");
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        return;
                    }

                    IReadOnlyList<HistoryRow> rows = await _repository.Handler.GetLogAsync(_repository.Path, new HistoryFilter
                    {
                        Path = file,
                        TrackRenames = true,
                        Count = 200,
                        Flags = HistoryFlags.Merges
                    });
                    var text = string.Join("\n", rows.Select(r => $"{r.ShortId}  {r.Timestamp:yyyy-MM-dd}  {r.AuthorDisplay,-24}  {r.Title}"));
                    await _dialogs.ShowTextAsync("File history - " + file, text);
                }),
                Button("Blame...", async () =>
                {
                    var file = await _dialogs.PromptAsync("Blame", "Repository-relative file", _repository.SelectedCommitFile?.RelativePath ?? "");
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        return;
                    }

                    IReadOnlyList<BlameLine> lines = await _repository.Advanced.GetBlameAsync(file);
                    if (!string.IsNullOrEmpty(_repository.Advanced.LastError))
                    {
                        await _dialogs.ShowMessageAsync("Blame", _repository.Advanced.LastError);
                        return;
                    }
                    var text = string.Join("\n", lines.Select(l => $"{l.LineNumber,6} {l.Sha[..Math.Min(8, l.Sha.Length)]} {l.Author,-22} {l.Date}  {l.Text}"));
                    await _dialogs.ShowTextAsync("Blame - " + file, text);
                }),
                Button("Search log...", async () =>
                {
                    var text = await _dialogs.PromptAsync("Search log", "Message text");
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return;
                    }

                    ProcessResult result = await _repository.Advanced.SearchLogAsync(text);
                    await _dialogs.ShowTextAsync("Search results", result.Combined);
                }),
                Button("Search working tree...", async () =>
                {
                    var text = await _dialogs.PromptAsync("Search working tree", "Text");
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return;
                    }

                    ProcessResult result = await _repository.Advanced.GrepAsync(text);
                    await _dialogs.ShowTextAsync("Working tree matches", result.Combined);
                }),
                Button("Preview stash...", async () =>
                {
                    var name = await _dialogs.PromptAsync("Stash preview", "Stash", "stash@{0}");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return;
                    }

                    ProcessResult result = await _repository.Advanced.StashShowAsync(name);
                    await _dialogs.ShowTextAsync("Stash " + name, result.Combined);
                }),
                Button("Rename branch...", async () =>
                {
                    var oldName = await _dialogs.PromptAsync("Rename branch", "Current branch name", _repository.CurrentBranch);
                    if (string.IsNullOrWhiteSpace(oldName))
                    {
                        return;
                    }

                    var newName = await _dialogs.PromptAsync("Rename branch", "New branch name");
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        return;
                    }

                    await RunAsync("Rename branch", () => _repository.Advanced.RenameBranchAsync(oldName, newName));
                }),
                Button("Delete remote branch...", async () =>
                {
                    var remote = await _dialogs.PromptAsync("Delete remote branch", "Remote", "origin");
                    if (string.IsNullOrWhiteSpace(remote))
                    {
                        return;
                    }

                    var branch = await _dialogs.PromptAsync("Delete remote branch", "Branch");
                    if (string.IsNullOrWhiteSpace(branch))
                    {
                        return;
                    }

                    if (!await _dialogs.ConfirmAsync("Delete remote branch",
                            $"Delete '{branch}' from '{remote}'?"))
                    {
                        return;
                    }

                    await RunAsync("Delete remote branch", () => _repository.Advanced.DeleteRemoteBranchAsync(remote, branch));
                }),
                Button("External diff...", async () =>
                {
                    ChangedFile? file = _repository.SelectedUnstagedFile ?? _repository.SelectedStagedFile ?? _repository.SelectedCommitFile;
                    if (file is null) { await _dialogs.ShowMessageAsync("External diff", "Select a file first."); return; }
                    var command = _repository.Settings.Settings.ExternalDiffCommand;
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        await _dialogs.ShowMessageAsync("External diff", "Configure External diff in Settings first.");
                        return;
                    }
                    var full = System.IO.Path.Combine(_repository.Path, file.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    command = command.Replace("{repo}", _repository.Path, StringComparison.OrdinalIgnoreCase)
                        .Replace("{file}", full, StringComparison.OrdinalIgnoreCase)
                        .Replace("{sha}", _repository.SelectedCommit?.Row.CommitId ?? "", StringComparison.OrdinalIgnoreCase);
                    await RunAsync("External diff", () => RunShellAsync(command));
                }),
                Button("External merge...", async () =>
                {
                    ChangedFile? file = _repository.SelectedUnstagedFile ?? _repository.SelectedStagedFile;
                    if (file is null) { await _dialogs.ShowMessageAsync("External merge", "Select a conflicted file first."); return; }
                    var command = _repository.Settings.Settings.ExternalMergeCommand;
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        await _dialogs.ShowMessageAsync("External merge", "Configure External merge in Settings first.");
                        return;
                    }
                    var full = System.IO.Path.Combine(_repository.Path, file.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    IReadOnlyDictionary<string, string> stages = await _repository.Advanced.GetConflictStagesAsync(file.RelativePath);
                    if (stages.Count < 3)
                    {
                        await _dialogs.ShowMessageAsync("External merge",
                            "This conflict does not have base, local and remote stages (for example add/add or delete/modify). Use the built-in diff instead.");
                        return;
                    }

                    command = command.Replace("{repo}", _repository.Path, StringComparison.OrdinalIgnoreCase)
                        .Replace("{file}", full, StringComparison.OrdinalIgnoreCase)
                        .Replace("{base}", stages["base"], StringComparison.OrdinalIgnoreCase)
                        .Replace("{local}", stages["local"], StringComparison.OrdinalIgnoreCase)
                        .Replace("{remote}", stages["remote"], StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        await RunAsync("External merge", () => RunShellAsync(command));
                    }
                    finally
                    {
                        foreach (var path in stages.Values)
                        {
                            try { File.Delete(path); } catch { }
                        }
                    }
                }),
                Button("Open hooks folder", async () =>
                {
                    try
                    {
                        var hooks = await _repository.Advanced.GetHooksPathAsync();
                        if (hooks is not null && Directory.Exists(hooks))
                        {
                            Process.Start(new ProcessStartInfo { FileName = hooks, UseShellExecute = true });
                        }
                        else
                        {
                            _status.Text = "Hooks folder not found.";
                        }
                    }
                    catch (Exception ex)
                    {
                        _status.Text = ex.Message;
                    }
                }),
                Button("Edit hook...", async () =>
                {
                    try
                    {
                        var hooks = await _repository.Advanced.GetHooksPathAsync();
                        if (hooks is null || !Directory.Exists(hooks))
                        {
                            _status.Text = "Hooks folder not found.";
                            return;
                        }

                        var name = await _dialogs.PromptAsync("Edit hook", "Hook file name", "pre-commit");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            return;
                        }

                        if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
                        {
                            _status.Text = "Enter a hook file name without a path.";
                            return;
                        }

                        var path = System.IO.Path.Combine(hooks, name);
                        var initial = File.Exists(path)
                            ? await File.ReadAllTextAsync(path)
                            : "#!/bin/sh\n\n";
                        var edited = await _dialogs.EditTextAsync("Edit hook " + name, path, initial);
                        if (edited is null)
                        {
                            return;
                        }

                        await File.WriteAllTextAsync(path, edited);
                        if (!OperatingSystem.IsWindows())
                        {
                            File.SetUnixFileMode(path,
                                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                        }
                        _status.Text = "Saved hook " + name + ".";
                    }
                    catch (Exception ex)
                    {
                        _status.Text = ex.Message;
                    }
                }),
                Button("Open terminal", () =>
                {
                    _repository.OpenTerminalCommand.Execute(null);
                    _status.Text = _repository.StatusMessage;
                    return Task.CompletedTask;
                })));
    }

    private Control BuildExtras()
    {
        return Page(
            new TextBlock { Text = "Git notes", FontWeight = FontWeight.SemiBold },
            Row(
                Button("Show note...", async () =>
                {
                    var revision = await _dialogs.PromptAsync("Show note", "Revision",
                        _repository.SelectedCommit?.Row.CommitId ?? "HEAD");
                    if (string.IsNullOrWhiteSpace(revision))
                    {
                        return;
                    }

                    ProcessResult result = await _repository.Advanced.NotesShowAsync(revision);
                    if (!result.Success)
                    {
                        await _dialogs.ShowMessageAsync("Show note", result.Combined);
                        return;
                    }
                    await _dialogs.ShowTextAsync("Note for " + revision, result.Output);
                }),
                Button("Add note...", async () =>
                {
                    var revision = await _dialogs.PromptAsync("Add note", "Revision",
                        _repository.SelectedCommit?.Row.CommitId ?? "HEAD");
                    if (string.IsNullOrWhiteSpace(revision))
                    {
                        return;
                    }

                    var message = await _dialogs.PromptAsync("Add note", "Note text");
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        return;
                    }

                    await RunAsync("Add note", () => _repository.Advanced.NotesAddAsync(revision, message));
                }),
                Button("Remove note...", async () =>
                {
                    var revision = await _dialogs.PromptAsync("Remove note", "Revision",
                        _repository.SelectedCommit?.Row.CommitId ?? "HEAD");
                    if (string.IsNullOrWhiteSpace(revision))
                    {
                        return;
                    }

                    if (!await _dialogs.ConfirmAsync("Remove note", $"Remove the note on {revision}?"))
                    {
                        return;
                    }

                    await RunAsync("Remove note", () => _repository.Advanced.NotesRemoveAsync(revision));
                })),
            new TextBlock
            {
                Text = "Sparse checkout",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            },
            Row(
                Button("Enable", async () =>
                {
                    if (!await _dialogs.ConfirmAsync("Sparse checkout",
                            "Enable cone-mode sparse checkout in this repository?"))
                    {
                        return;
                    }

                    await RunAsync("Enable sparse checkout", _repository.Advanced.SparseCheckoutInitAsync);
                }),
                Button("Set patterns...", async () =>
                {
                    var text = await _dialogs.PromptAsync("Sparse checkout",
                        "Folders/files separated by commas or new lines");
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return;
                    }

                    var patterns = text.Split([',', '\n', '\r', ';'],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (patterns.Length == 0)
                    {
                        return;
                    }

                    await RunAsync("Set sparse patterns", () => _repository.Advanced.SparseCheckoutSetAsync(patterns));
                }),
                Button("Disable", async () =>
                {
                    if (!await _dialogs.ConfirmAsync("Sparse checkout",
                            "Disable sparse checkout and restore the full working tree?"))
                    {
                        return;
                    }

                    await RunAsync("Disable sparse checkout", _repository.Advanced.SparseCheckoutDisableAsync);
                })));
    }

    private Control BuildCustom()
    {
        RefreshCustomActions();
        return Page(
            new TextBlock { Text = "Saved custom actions", FontWeight = FontWeight.SemiBold },
            new TextBlock
            {
                Text = "Commands run in the repository. Placeholders: {repo}, {sha}, {file}.",
                Classes = { "muted" },
                FontSize = 11
            },
            _customActions,
            Row(
                Button("Run selected", async () =>
                {
                    if (_customActions.SelectedItem is not CustomActionSetting action)
                    {
                        return;
                    }

                    await RunCustomCommandAsync(action.Command);
                }),
                Button("Add...", async () =>
                {
                    var name = await _dialogs.PromptAsync("Custom action", "Name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return;
                    }

                    var command = await _dialogs.PromptAsync("Custom action", "Command", "git status");
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        return;
                    }

                    _repository.Settings.Settings.CustomActions.Add(new CustomActionSetting
                    {
                        Name = name.Trim(),
                        Command = command.Trim()
                    });
                    _repository.Settings.Save();
                    RefreshCustomActions();
                }),
                Button("Delete", async () =>
                {
                    if (_customActions.SelectedItem is not CustomActionSetting action)
                    {
                        return;
                    }

                    if (!await _dialogs.ConfirmAsync("Delete custom action", $"Delete '{action.Name}'?"))
                    {
                        return;
                    }

                    _repository.Settings.Settings.CustomActions.Remove(action);
                    _repository.Settings.Save();
                    RefreshCustomActions();
                }),
                Button("Run command...", async () =>
                {
                    var command = await _dialogs.PromptAsync("Custom action", "Command", "git status");
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        return;
                    }

                    await RunCustomCommandAsync(command);
                })));
    }

    private void RefreshCustomActions()
    {
        _customActions.ItemsSource = null;
        _customActions.ItemsSource = _repository.Settings.Settings.CustomActions.ToList();
    }

    private async Task RunCustomCommandAsync(string command)
    {
        command = command
            .Replace("{repo}", _repository.Path, StringComparison.OrdinalIgnoreCase)
            .Replace("{sha}", _repository.SelectedCommit?.Row.CommitId ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{file}", _repository.SelectedCommitFile?.RelativePath
                               ?? _repository.SelectedUnstagedFile?.RelativePath
                               ?? _repository.SelectedStagedFile?.RelativePath ?? "", StringComparison.OrdinalIgnoreCase);
        await RunAsync("Custom action", () => RunShellAsync(command));
    }
}



