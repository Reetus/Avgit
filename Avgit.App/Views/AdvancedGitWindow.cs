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
        Title = string.Format(Strings.Advanced_WindowTitle, repository.Name);
        Width = 860;
        Height = 640;
        MinWidth = 700;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
        _status = new TextBlock
        {
            Text = Strings.Advanced_ChooseOperation,
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
        tabs.Items.Add(Tab(Strings.Advanced_Submodules, BuildSubmodules()));
        tabs.Items.Add(Tab(Strings.Advanced_TabLfs, BuildLfs()));
        tabs.Items.Add(Tab(Strings.Advanced_TabFlow, BuildFlow()));
        tabs.Items.Add(Tab(Strings.Advanced_TabSubtree, BuildSubtree()));
        tabs.Items.Add(Tab(Strings.Advanced_TabSvn, BuildSvn()));
        tabs.Items.Add(Tab(Strings.Advanced_Bisect, BuildBisect()));
        tabs.Items.Add(Tab(Strings.Advanced_TabWorktrees, BuildWorktrees()));
        tabs.Items.Add(Tab(Strings.Advanced_TabPatches, BuildPatches()));
        tabs.Items.Add(Tab(Strings.Advanced_Diagnostics, BuildDiagnostics()));
        tabs.Items.Add(Tab(Strings.Advanced_TabExtras, BuildExtras()));
        tabs.Items.Add(Tab(Strings.Advanced_TabCustom, BuildCustom()));
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
        _status.Text = string.Format(Strings.Advanced_OperationRunning, label);
        try
        {
            ProcessResult result = await action();
            _lastOutput = result.Combined;
            if (!result.Success)
            {
                _status.Text = string.Format(Strings.Advanced_OperationFailedStatus, label);
                await _dialogs.ShowMessageAsync(string.Format(Strings.Advanced_OperationFailedTitle, label), result.Combined);
            }
            else
            {
                _status.Text = string.IsNullOrWhiteSpace(result.Output) ? string.Format(Strings.Advanced_OperationCompleted, label) : result.Output.Trim();
            }
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            await _dialogs.ShowMessageAsync(string.Format(Strings.Advanced_OperationFailedTitle, label), ex.Message);
        }
    }

    private async Task RunSimpleAsync(string label, Func<Task> action)
    {
        _status.Text = string.Format(Strings.Advanced_OperationRunning, label);
        try
        {
            await action();
            _status.Text = string.Format(Strings.Advanced_OperationCompleted, label);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            await _dialogs.ShowMessageAsync(string.Format(Strings.Advanced_OperationFailedTitle, label), ex.Message);
        }
    }

    private Control BuildSubmodules()
    {
        Button add = Button(Strings.Advanced_AddEllipsis, async () =>
        {
            var url = await _dialogs.PromptAsync(Strings.Advanced_AddSubmodule, Strings.Advanced_RepositoryURL);
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var path = await _dialogs.PromptAsync(Strings.Advanced_AddSubmodule, Strings.Advanced_LocalPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await RunAsync(Strings.Advanced_AddSubmodule, () => _repository.Advanced.SubmoduleAddAsync(url, path));
            await RefreshSubmodulesAsync();
        });
        return Page(
            new TextBlock { Text = Strings.Advanced_Submodules, FontWeight = FontWeight.SemiBold },
            _submodules,
            Row(
                Button(Strings.Common_Refresh, RefreshSubmodulesAsync),
                Button(Strings.Advanced_Init, () => RunAsync(Strings.Advanced_InitSubmodules, _repository.Advanced.SubmoduleInitAsync)),
                Button(Strings.Advanced_Update, async () => { await RunAsync(Strings.Advanced_UpdateSubmodules, _repository.Advanced.SubmoduleUpdateAsync); await RefreshSubmodulesAsync(); }),
                Button(Strings.Advanced_Sync, () => RunAsync(Strings.Advanced_SyncSubmodules, _repository.Advanced.SubmoduleSyncAsync)),
                Button(Strings.Advanced_Deinit, async () =>
                {
                    var selected = _submodules.SelectedIndex;
                    if (selected < 0 || selected >= _submoduleItems.Count)
                    {
                        return;
                    }

                    SubmoduleInfo item = _submoduleItems[selected];
                    if (!await _dialogs.ConfirmAsync(Strings.Advanced_DeinitSubmodule,
                            string.Format(Strings.Advanced_DeinitSubmoduleConfirm, item.Path)))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_DeinitSubmodule,
                        () => _repository.Advanced.SubmoduleDeinitAsync(item.Path));
                    await RefreshSubmodulesAsync();
                }),
                Button(Strings.Advanced_Remove, async () =>
                {
                    var selected = _submodules.SelectedIndex;
                    if (selected < 0 || selected >= _submoduleItems.Count)
                    {
                        return;
                    }

                    SubmoduleInfo item = _submoduleItems[selected];
                    if (!await _dialogs.ConfirmAsync(Strings.Advanced_RemoveSubmodule,
                            string.Format(Strings.Advanced_RemoveSubmoduleConfirm, item.Path)))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_RemoveSubmodule,
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
            _status.Text = string.Format(Strings.Advanced_SubmoduleCount, list.Count);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private Control BuildLfs()
    {
        return Page(
            new TextBlock { Text = Strings.Advanced_GitLFS, FontWeight = FontWeight.SemiBold },
            _lfsPatterns,
            new TextBlock { Text = Strings.Advanced_Locks, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
            _lfsLocks,
            Row(
                Button(Strings.Advanced_RefreshLocks, RefreshLfsLocksAsync),
                Button(Strings.Advanced_LockEllipsis, async () =>
                {
                    var file = await _dialogs.PromptAsync(Strings.Advanced_LockWithGitLFS, Strings.Advanced_FilePath);
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        return;
                    }

                    await RunAsync(string.Format(Strings.Advanced_LockFile, file), () => _repository.Advanced.LfsLockAsync(file));
                    await RefreshLfsLocksAsync();
                }),
                Button(Strings.Advanced_UnlockEllipsis, async () =>
                {
                    var file = await _dialogs.PromptAsync(Strings.Advanced_UnlockWithGitLFS, Strings.Advanced_FilePath);
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        return;
                    }

                    await RunAsync(string.Format(Strings.Advanced_UnlockFile, file), () => _repository.Advanced.LfsUnlockAsync(file));
                    await RefreshLfsLocksAsync();
                })),
            Row(
                Button(Strings.Advanced_RefreshPatterns, RefreshLfsAsync),
                Button(Strings.Advanced_Init, () => RunAsync(Strings.Advanced_GitLFSInit, _repository.Advanced.LfsInstallAsync)),
                Button(Strings.Advanced_TrackEllipsis, async () =>
                {
                    var pattern = await _dialogs.PromptAsync(Strings.Advanced_TrackWithGitLFS, Strings.Advanced_Pattern, "*.bin");
                    if (string.IsNullOrWhiteSpace(pattern))
                    {
                        return;
                    }

                    await RunAsync(string.Format(Strings.Advanced_TrackPattern, pattern), () => _repository.Advanced.LfsTrackAsync(pattern));
                    await RefreshLfsAsync();
                }),
                Button(Strings.Advanced_UntrackEllipsis, async () =>
                {
                    var pattern = await _dialogs.PromptAsync(Strings.Advanced_UntrackGitLFS, Strings.Advanced_Pattern);
                    if (string.IsNullOrWhiteSpace(pattern))
                    {
                        return;
                    }

                    await RunAsync(string.Format(Strings.Advanced_UntrackPattern, pattern), () => _repository.Advanced.LfsUntrackAsync(pattern));
                    await RefreshLfsAsync();
                }),
                Button(Strings.Advanced_Fetch, () => RunAsync(Strings.Advanced_GitLFSFetch, _repository.Advanced.LfsFetchAsync)),
                Button(Strings.Advanced_Pull, () => RunAsync(Strings.Advanced_GitLFSPull, _repository.Advanced.LfsPullAsync))));
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
            _status.Text = string.Format(Strings.Advanced_LfsPatternCount, patterns.Count);
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
            _status.Text = string.Format(Strings.Advanced_LfsLockCount, locks.Count);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private Control BuildFlow()
    {
        return Page(
            new TextBlock { Text = Strings.Advanced_GitFlow, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = Strings.Advanced_FlowRequirement, Classes = { "muted" }, FontSize = 11 },
            Row(
                Button(Strings.Advanced_Init, () => RunAsync(Strings.Advanced_GitFlowInit, _repository.Advanced.FlowInitAsync)),
                Button(Strings.Advanced_StartEllipsis, async () =>
                {
                    var kind = await _dialogs.PromptAsync(Strings.Advanced_GitFlow, Strings.Advanced_FlowBranchTypeWithKinds, "feature");
                    if (string.IsNullOrWhiteSpace(kind))
                    {
                        return;
                    }

                    var name = await _dialogs.PromptAsync(Strings.Advanced_GitFlow, Strings.Advanced_Name);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return;
                    }

                    await RunAsync(string.Format(Strings.Advanced_FlowStart, kind, name), () => _repository.Advanced.FlowStartAsync(kind, name));
                }),
                Button(Strings.Advanced_FinishEllipsis, async () =>
                {
                    var kind = await _dialogs.PromptAsync(Strings.Advanced_GitFlow, Strings.Advanced_FlowBranchTypeWithKinds, "feature");
                    if (string.IsNullOrWhiteSpace(kind))
                    {
                        return;
                    }

                    kind = kind.Trim().ToLowerInvariant();
                    var name = await _dialogs.PromptAsync(Strings.Advanced_GitFlow, Strings.Advanced_Name);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return;
                    }

                    string? tagMessage = null;
                    if (kind is "release" or "hotfix")
                    {
                        tagMessage = await _dialogs.PromptAsync(Strings.Advanced_GitFlow, Strings.Advanced_TagMessage, $"Finish {kind} {name}");
                        if (tagMessage is null)
                        {
                            return;
                        }
                    }
                    await RunAsync(string.Format(Strings.Advanced_FlowFinish, kind, name), () => _repository.Advanced.FlowFinishAsync(kind, name, tagMessage));
                }),
                Button(Strings.Advanced_ListEllipsis, async () =>
                {
                    var kind = await _dialogs.PromptAsync(Strings.Advanced_GitFlow, Strings.Advanced_BranchType, "feature");
                    if (string.IsNullOrWhiteSpace(kind))
                    {
                        return;
                    }

                    await RunAsync(string.Format(Strings.Advanced_FlowList, kind), () => _repository.Advanced.FlowListAsync(kind));
                })));
    }

    private Control BuildSubtree()
    {
        return Page(
            new TextBlock { Text = Strings.Advanced_GitSubtree, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = Strings.Advanced_SubtreeExamples, Classes = { "muted" }, FontSize = 11 },
            Row(
                Button(Strings.Advanced_AddEllipsis, async () =>
                {
                    var prefix = await _dialogs.PromptAsync(Strings.Advanced_SubtreeAdd, Strings.Advanced_Prefix);
                    if (string.IsNullOrWhiteSpace(prefix))
                    {
                        return;
                    }

                    var url = await _dialogs.PromptAsync(Strings.Advanced_SubtreeAdd, Strings.Advanced_RepositoryURL);
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return;
                    }

                    var reference = await _dialogs.PromptAsync(Strings.Advanced_SubtreeAdd, Strings.Advanced_Reference, "main");
                    if (string.IsNullOrWhiteSpace(reference))
                    {
                        return;
                    }

                    (string? Value, bool Flag)? squash = await _dialogs.PromptWithCheckBoxAsync(Strings.Advanced_SubtreeAdd, Strings.Advanced_SquashHistoryPrompt, "", Strings.Advanced_SquashHistory, true);
                    if (squash is null)
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_SubtreeAdd, () => _repository.Advanced.SubtreeAddAsync(prefix, url, reference, squash.Value.Flag));
                }),
                Button(Strings.Advanced_PullEllipsis, async () =>
                {
                    var prefix = await _dialogs.PromptAsync(Strings.Advanced_SubtreePull, Strings.Advanced_Prefix);
                    if (string.IsNullOrWhiteSpace(prefix))
                    {
                        return;
                    }

                    var remote = await _dialogs.PromptAsync(Strings.Advanced_SubtreePull, Strings.Advanced_RemoteOrURL, "origin");
                    if (string.IsNullOrWhiteSpace(remote))
                    {
                        return;
                    }

                    var reference = await _dialogs.PromptAsync(Strings.Advanced_SubtreePull, Strings.Advanced_Reference, "main");
                    if (string.IsNullOrWhiteSpace(reference))
                    {
                        return;
                    }

                    (string? Value, bool Flag)? squash = await _dialogs.PromptWithCheckBoxAsync(Strings.Advanced_SubtreePull, Strings.Advanced_SquashHistoryPrompt, "", Strings.Advanced_SquashHistory, true);
                    if (squash is null)
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_SubtreePull, () => _repository.Advanced.SubtreePullAsync(prefix, remote, reference, squash.Value.Flag));
                }),
                Button(Strings.Advanced_PushEllipsis, async () =>
                {
                    var prefix = await _dialogs.PromptAsync(Strings.Advanced_SubtreePush, Strings.Advanced_Prefix);
                    if (string.IsNullOrWhiteSpace(prefix))
                    {
                        return;
                    }

                    var remote = await _dialogs.PromptAsync(Strings.Advanced_SubtreePush, Strings.Advanced_RemoteOrURL, "origin");
                    if (string.IsNullOrWhiteSpace(remote))
                    {
                        return;
                    }

                    var reference = await _dialogs.PromptAsync(Strings.Advanced_SubtreePush, Strings.Advanced_Reference, "main");
                    if (string.IsNullOrWhiteSpace(reference))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_SubtreePush, () => _repository.Advanced.SubtreePushAsync(prefix, remote, reference));
                })));
    }

    private Control BuildSvn()
    {
        return Page(
            new TextBlock { Text = Strings.Advanced_GitSVNBridge, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = Strings.Advanced_SvnRequirement, Classes = { "muted" }, FontSize = 11 },
            Row(
                Button(Strings.Advanced_Fetch, () => RunAsync("git svn fetch", _repository.Advanced.SvnFetchAsync)),
                Button(Strings.Advanced_Rebase, () => RunAsync("git svn rebase", _repository.Advanced.SvnRebaseAsync)),
                Button(Strings.Advanced_Dcommit, () => RunAsync("git svn dcommit", _repository.Advanced.SvnDcommitAsync)),
                Button(Strings.Advanced_Info, () => RunAsync("git svn info", _repository.Advanced.SvnInfoAsync))));
    }

    private Control BuildBisect()
    {
        return Page(
            new TextBlock { Text = Strings.Advanced_Bisect, FontWeight = FontWeight.SemiBold },
            Row(
                Button(Strings.Advanced_StartEllipsis, async () =>
                {
                    var bad = await _dialogs.PromptAsync(Strings.Advanced_Bisect, Strings.Advanced_BadRevision, "HEAD");
                    if (bad is null)
                    {
                        return;
                    }

                    var good = await _dialogs.PromptAsync(Strings.Advanced_Bisect, Strings.Advanced_GoodRevisionOptional, "");
                    if (good is null)
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_StartBisect, () => _repository.Advanced.BisectStartAsync(bad, string.IsNullOrWhiteSpace(good) ? null : good));
                }),
                Button(Strings.Advanced_Good, () => RunAsync(Strings.Advanced_BisectGood, () => _repository.Advanced.BisectGoodAsync(_repository.SelectedCommit?.Row.CommitId))),
                Button(Strings.Advanced_Bad, () => RunAsync(Strings.Advanced_BisectBad, () => _repository.Advanced.BisectBadAsync(_repository.SelectedCommit?.Row.CommitId))),
                Button(Strings.Advanced_Reset, () => RunAsync(Strings.Advanced_BisectReset, _repository.Advanced.BisectResetAsync)),
                Button(Strings.Advanced_Log, async () =>
                {
                    ProcessResult result = await _repository.Advanced.BisectLogAsync();
                    await _dialogs.ShowTextAsync(Strings.Advanced_BisectLog, result.Combined);
                })));
    }

    private Control BuildWorktrees()
    {
        return Page(
            new TextBlock { Text = Strings.Advanced_LinkedWorktrees, FontWeight = FontWeight.SemiBold },
            _worktrees,
            Row(
                Button(Strings.Common_Refresh, RefreshWorktreesAsync),
                Button(Strings.Advanced_AddEllipsis, async () =>
                {
                    var path = await _dialogs.PromptAsync(Strings.Advanced_AddWorktree, Strings.Advanced_Path);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return;
                    }
                    // An empty answer means "none"; Cancel (null) aborts.
                    var branch = await _dialogs.PromptAsync(Strings.Advanced_AddWorktree, Strings.Advanced_NewBranchOptional, "");
                    if (branch is null)
                    {
                        return;
                    }

                    var start = await _dialogs.PromptAsync(Strings.Advanced_AddWorktree, Strings.Advanced_StartPointOptional, "");
                    if (start is null)
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_AddWorktree, () => _repository.Advanced.WorktreeAddAsync(path,
                        string.IsNullOrWhiteSpace(branch) ? null : branch,
                        string.IsNullOrWhiteSpace(start) ? null : start));
                    await RefreshWorktreesAsync();
                }),
                Button(Strings.Advanced_RemoveEllipsis, async () =>
                {
                    var selected = _worktrees.SelectedIndex;
                    if (selected < 0 || selected >= _worktreeItems.Count)
                    {
                        return;
                    }

                    WorktreeInfo item = _worktreeItems[selected];
                    if (!await _dialogs.ConfirmAsync(Strings.Advanced_RemoveWorktree, string.Format(Strings.Advanced_RemoveWorktreeConfirm, item.Path)))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_RemoveWorktree, () => _repository.Advanced.WorktreeRemoveAsync(item.Path, true));
                    await RefreshWorktreesAsync();
                }),
                Button(Strings.Advanced_OpenFolder, () =>
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
                .Select(item => item.IsDetached ? string.Format(Strings.Advanced_WorktreeItemDetached, item.Path, item.Branch) : $"{item.Path}  [{item.Branch}]")
                .ToList();
            _status.Text = string.Format(Strings.Advanced_WorktreeCount, _worktreeItems.Count);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private Control BuildPatches()
    {
        return Page(
            new TextBlock { Text = Strings.Advanced_PatchesAndArchives, FontWeight = FontWeight.SemiBold },
            Row(
                Button(Strings.Advanced_CreatePatchForSelectedCommitEllipsis, async () =>
                {
                    var sha = _repository.SelectedCommit?.Row.CommitId;
                    if (sha is null) { await _dialogs.ShowMessageAsync(Strings.Advanced_Patch, Strings.Advanced_SelectACommitFirst); return; }
                    var file = await _dialogs.PickSaveFileAsync(Strings.Advanced_SavePatch, "commit.patch");
                    if (file is null)
                    {
                        return;
                    }

                    await RunSimpleAsync(Strings.Advanced_CreatePatch, async () =>
                        await File.WriteAllTextAsync(file, await _repository.Advanced.CreatePatchAsync(sha)));
                }),
                Button(Strings.Advanced_ApplyPatchEllipsis, async () =>
                {
                    var file = await _dialogs.PickFileAsync(Strings.Advanced_ApplyPatch);
                    if (file is null)
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_ApplyPatch, () => _repository.Advanced.ApplyPatchAsync(file));
                }),
                Button(Strings.Advanced_ExportArchiveEllipsis, async () =>
                {
                    var sha = _repository.SelectedCommit?.Row.CommitId;
                    if (sha is null) { await _dialogs.ShowMessageAsync(Strings.Advanced_Archive, Strings.Advanced_SelectACommitFirst); return; }
                    var file = await _dialogs.PickSaveFileAsync(Strings.Advanced_ExportArchive, "repo.zip");
                    if (file is null)
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_ExportArchive, () => _repository.Advanced.CreateArchiveAsync(sha, file));
                }),
                Button(Strings.Advanced_PushTagEllipsis, async () =>
                {
                    var tag = await _dialogs.PromptAsync(Strings.Advanced_PushTag, Strings.Advanced_TagName);
                    if (string.IsNullOrWhiteSpace(tag))
                    {
                        return;
                    }

                    var remote = await _dialogs.PromptAsync(Strings.Advanced_PushTag, Strings.Advanced_Remote, "origin");
                    if (remote is null)
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_PushTag, () => _repository.Advanced.TagPushAsync(tag,
                        string.IsNullOrWhiteSpace(remote) ? null : remote));
                }),
                Button(Strings.Advanced_RebaseKeepingMergesEllipsis, async () =>
                {
                    var onto = await _dialogs.PromptAsync(Strings.Advanced_RebasePreservingMerges, Strings.Advanced_Onto, "HEAD~1");
                    if (string.IsNullOrWhiteSpace(onto))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_RebasePreservingMerges, () => _repository.Advanced.RebasePreservingMergesAsync(onto));
                }),
                Button(Strings.Advanced_RebaseRootKeepingMerges, async () =>
                {
                    if (!await _dialogs.ConfirmAsync(Strings.Advanced_RebaseFromRoot,
                            Strings.Advanced_RebaseFromRootConfirm))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_RebaseFromRoot, _repository.Advanced.RebaseRootPreservingMergesAsync);
                })));
    }

    private Control BuildDiagnostics()
    {
        return Page(
            new TextBlock { Text = Strings.Advanced_Diagnostics, FontWeight = FontWeight.SemiBold },
            Row(
                Button(Strings.Advanced_Reflog, async () =>
                {
                    IReadOnlyList<ReflogEntry> entries = await _repository.Advanced.GetReflogAsync();
                    if (!string.IsNullOrEmpty(_repository.Advanced.LastError))
                    {
                        await _dialogs.ShowMessageAsync(Strings.Advanced_Reflog, _repository.Advanced.LastError);
                        return;
                    }
                    var text = string.Join("\n", entries.Select(e => $"{e.ShortSha}  {e.Selector}  {e.Date}  {e.Subject}"));
                    await _dialogs.ShowTextAsync(Strings.Advanced_Reflog, text);
                }),
                Button(Strings.Advanced_FileHistoryEllipsis, async () =>
                {
                    var file = await _dialogs.PromptAsync(Strings.Advanced_FileHistory, Strings.Advanced_RepositoryRelativeFile, _repository.SelectedCommitFile?.RelativePath ?? "");
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
                    await _dialogs.ShowTextAsync(string.Format(Strings.Advanced_FileHistoryTitle, file), text);
                }),
                Button(Strings.Advanced_BlameEllipsis, async () =>
                {
                    var file = await _dialogs.PromptAsync(Strings.Advanced_Blame, Strings.Advanced_RepositoryRelativeFile, _repository.SelectedCommitFile?.RelativePath ?? "");
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        return;
                    }

                    IReadOnlyList<BlameLine> lines = await _repository.Advanced.GetBlameAsync(file);
                    if (!string.IsNullOrEmpty(_repository.Advanced.LastError))
                    {
                        await _dialogs.ShowMessageAsync(Strings.Advanced_Blame, _repository.Advanced.LastError);
                        return;
                    }
                    var text = string.Join("\n", lines.Select(l => $"{l.LineNumber,6} {l.Sha[..Math.Min(8, l.Sha.Length)]} {l.Author,-22} {l.Date}  {l.Text}"));
                    await _dialogs.ShowTextAsync(string.Format(Strings.Advanced_BlameTitle, file), text);
                }),
                Button(Strings.Advanced_SearchLogEllipsis, async () =>
                {
                    var text = await _dialogs.PromptAsync(Strings.Advanced_SearchLog, Strings.Advanced_MessageText);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return;
                    }

                    ProcessResult result = await _repository.Advanced.SearchLogAsync(text);
                    await _dialogs.ShowTextAsync(Strings.Advanced_SearchResults, result.Combined);
                }),
                Button(Strings.Advanced_SearchWorkingTreeEllipsis, async () =>
                {
                    var text = await _dialogs.PromptAsync(Strings.Advanced_SearchWorkingTree, Strings.Advanced_Text);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return;
                    }

                    ProcessResult result = await _repository.Advanced.GrepAsync(text);
                    await _dialogs.ShowTextAsync(Strings.Advanced_WorkingTreeMatches, result.Combined);
                }),
                Button(Strings.Advanced_PreviewStashEllipsis, async () =>
                {
                    var name = await _dialogs.PromptAsync(Strings.Advanced_StashPreview, Strings.Advanced_Stash, "stash@{0}");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return;
                    }

                    ProcessResult result = await _repository.Advanced.StashShowAsync(name);
                    await _dialogs.ShowTextAsync(string.Format(Strings.Advanced_StashTitle, name), result.Combined);
                }),
                Button(Strings.Advanced_RenameBranchEllipsis, async () =>
                {
                    var oldName = await _dialogs.PromptAsync(Strings.Advanced_RenameBranch, Strings.Advanced_CurrentBranchName, _repository.CurrentBranch);
                    if (string.IsNullOrWhiteSpace(oldName))
                    {
                        return;
                    }

                    var newName = await _dialogs.PromptAsync(Strings.Advanced_RenameBranch, Strings.Advanced_NewBranchName);
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_RenameBranch, () => _repository.Advanced.RenameBranchAsync(oldName, newName));
                }),
                Button(Strings.Advanced_DeleteRemoteBranchEllipsis, async () =>
                {
                    var remote = await _dialogs.PromptAsync(Strings.Advanced_DeleteRemoteBranch, Strings.Advanced_Remote, "origin");
                    if (string.IsNullOrWhiteSpace(remote))
                    {
                        return;
                    }

                    var branch = await _dialogs.PromptAsync(Strings.Advanced_DeleteRemoteBranch, Strings.Advanced_Branch);
                    if (string.IsNullOrWhiteSpace(branch))
                    {
                        return;
                    }

                    if (!await _dialogs.ConfirmAsync(Strings.Advanced_DeleteRemoteBranch,
                            string.Format(Strings.Advanced_DeleteRemoteBranchConfirm, branch, remote)))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_DeleteRemoteBranch, () => _repository.Advanced.DeleteRemoteBranchAsync(remote, branch));
                }),
                Button(Strings.Advanced_ExternalDiffEllipsis, async () =>
                {
                    ChangedFile? file = _repository.SelectedUnstagedFile ?? _repository.SelectedStagedFile ?? _repository.SelectedCommitFile;
                    if (file is null) { await _dialogs.ShowMessageAsync(Strings.Advanced_ExternalDiff, Strings.Advanced_SelectAFileFirst); return; }
                    var command = _repository.Settings.Settings.ExternalDiffCommand;
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        await _dialogs.ShowMessageAsync(Strings.Advanced_ExternalDiff, Strings.Advanced_ConfigureExternalDiffFirst);
                        return;
                    }
                    var full = System.IO.Path.Combine(_repository.Path, file.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    command = command.Replace("{repo}", _repository.Path, StringComparison.OrdinalIgnoreCase)
                        .Replace("{file}", full, StringComparison.OrdinalIgnoreCase)
                        .Replace("{sha}", _repository.SelectedCommit?.Row.CommitId ?? "", StringComparison.OrdinalIgnoreCase);
                    await RunAsync(Strings.Advanced_ExternalDiff, () => RunShellAsync(command));
                }),
                Button(Strings.Advanced_ExternalMergeEllipsis, async () =>
                {
                    ChangedFile? file = _repository.SelectedUnstagedFile ?? _repository.SelectedStagedFile;
                    if (file is null) { await _dialogs.ShowMessageAsync(Strings.Advanced_ExternalMerge, Strings.Advanced_SelectAConflictedFileFirst); return; }
                    var command = _repository.Settings.Settings.ExternalMergeCommand;
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        await _dialogs.ShowMessageAsync(Strings.Advanced_ExternalMerge, Strings.Advanced_ConfigureExternalMergeFirst);
                        return;
                    }
                    var full = System.IO.Path.Combine(_repository.Path, file.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    IReadOnlyDictionary<string, string> stages = await _repository.Advanced.GetConflictStagesAsync(file.RelativePath);
                    if (stages.Count < 3)
                    {
                        await _dialogs.ShowMessageAsync(Strings.Advanced_ExternalMerge,
                            Strings.Advanced_ExternalMergeMissingStages);
                        return;
                    }

                    command = command.Replace("{repo}", _repository.Path, StringComparison.OrdinalIgnoreCase)
                        .Replace("{file}", full, StringComparison.OrdinalIgnoreCase)
                        .Replace("{base}", stages["base"], StringComparison.OrdinalIgnoreCase)
                        .Replace("{local}", stages["local"], StringComparison.OrdinalIgnoreCase)
                        .Replace("{remote}", stages["remote"], StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        await RunAsync(Strings.Advanced_ExternalMerge, () => RunShellAsync(command));
                    }
                    finally
                    {
                        foreach (var path in stages.Values)
                        {
                            try { File.Delete(path); } catch { }
                        }
                    }
                }),
                Button(Strings.Advanced_OpenHooksFolder, async () =>
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
                            _status.Text = Strings.Advanced_HooksFolderNotFound;
                        }
                    }
                    catch (Exception ex)
                    {
                        _status.Text = ex.Message;
                    }
                }),
                Button(Strings.Advanced_EditHookEllipsis, async () =>
                {
                    try
                    {
                        var hooks = await _repository.Advanced.GetHooksPathAsync();
                        if (hooks is null || !Directory.Exists(hooks))
                        {
                            _status.Text = Strings.Advanced_HooksFolderNotFound;
                            return;
                        }

                        var name = await _dialogs.PromptAsync(Strings.Advanced_EditHook, Strings.Advanced_HookFileName, "pre-commit");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            return;
                        }

                        if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
                        {
                            _status.Text = Strings.Advanced_HookNameWithoutPath;
                            return;
                        }

                        var path = System.IO.Path.Combine(hooks, name);
                        var initial = File.Exists(path)
                            ? await File.ReadAllTextAsync(path)
                            : "#!/bin/sh\n\n";
                        var edited = await _dialogs.EditTextAsync(string.Format(Strings.Advanced_EditHookTitle, name), path, initial);
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
                        _status.Text = string.Format(Strings.Advanced_SavedHook, name);
                    }
                    catch (Exception ex)
                    {
                        _status.Text = ex.Message;
                    }
                }),
                Button(Strings.Advanced_OpenTerminal, () =>
                {
                    _repository.OpenTerminalCommand.Execute(null);
                    _status.Text = _repository.StatusMessage;
                    return Task.CompletedTask;
                })));
    }

    private Control BuildExtras()
    {
        return Page(
            new TextBlock { Text = Strings.Advanced_GitNotes, FontWeight = FontWeight.SemiBold },
            Row(
                Button(Strings.Advanced_ShowNoteEllipsis, async () =>
                {
                    var revision = await _dialogs.PromptAsync(Strings.Advanced_ShowNote, Strings.Advanced_Revision,
                        _repository.SelectedCommit?.Row.CommitId ?? "HEAD");
                    if (string.IsNullOrWhiteSpace(revision))
                    {
                        return;
                    }

                    ProcessResult result = await _repository.Advanced.NotesShowAsync(revision);
                    if (!result.Success)
                    {
                        await _dialogs.ShowMessageAsync(Strings.Advanced_ShowNote, result.Combined);
                        return;
                    }
                    await _dialogs.ShowTextAsync(string.Format(Strings.Advanced_NoteForTitle, revision), result.Output);
                }),
                Button(Strings.Advanced_AddNoteEllipsis, async () =>
                {
                    var revision = await _dialogs.PromptAsync(Strings.Advanced_AddNote, Strings.Advanced_Revision,
                        _repository.SelectedCommit?.Row.CommitId ?? "HEAD");
                    if (string.IsNullOrWhiteSpace(revision))
                    {
                        return;
                    }

                    var message = await _dialogs.PromptAsync(Strings.Advanced_AddNote, Strings.Advanced_NoteText);
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_AddNote, () => _repository.Advanced.NotesAddAsync(revision, message));
                }),
                Button(Strings.Advanced_RemoveNoteEllipsis, async () =>
                {
                    var revision = await _dialogs.PromptAsync(Strings.Advanced_RemoveNote, Strings.Advanced_Revision,
                        _repository.SelectedCommit?.Row.CommitId ?? "HEAD");
                    if (string.IsNullOrWhiteSpace(revision))
                    {
                        return;
                    }

                    if (!await _dialogs.ConfirmAsync(Strings.Advanced_RemoveNote, string.Format(Strings.Advanced_RemoveNoteConfirm, revision)))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_RemoveNote, () => _repository.Advanced.NotesRemoveAsync(revision));
                })),
            new TextBlock
            {
                Text = Strings.Advanced_SparseCheckout,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            },
            Row(
                Button(Strings.Advanced_Enable, async () =>
                {
                    if (!await _dialogs.ConfirmAsync(Strings.Advanced_SparseCheckout,
                            Strings.Advanced_EnableSparseCheckoutConfirm))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_EnableSparseCheckout, _repository.Advanced.SparseCheckoutInitAsync);
                }),
                Button(Strings.Advanced_SetPatternsEllipsis, async () =>
                {
                    var text = await _dialogs.PromptAsync(Strings.Advanced_SparseCheckout,
                        Strings.Advanced_SparsePatternsPrompt);
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

                    await RunAsync(Strings.Advanced_SetSparsePatterns, () => _repository.Advanced.SparseCheckoutSetAsync(patterns));
                }),
                Button(Strings.Advanced_Disable, async () =>
                {
                    if (!await _dialogs.ConfirmAsync(Strings.Advanced_SparseCheckout,
                            Strings.Advanced_DisableSparseCheckoutConfirm))
                    {
                        return;
                    }

                    await RunAsync(Strings.Advanced_DisableSparseCheckout, _repository.Advanced.SparseCheckoutDisableAsync);
                })));
    }

    private Control BuildCustom()
    {
        RefreshCustomActions();
        return Page(
            new TextBlock { Text = Strings.Advanced_SavedCustomActions, FontWeight = FontWeight.SemiBold },
            new TextBlock
            {
                Text = Strings.Advanced_CustomActionsHint,
                Classes = { "muted" },
                FontSize = 11
            },
            _customActions,
            Row(
                Button(Strings.Advanced_RunSelected, async () =>
                {
                    if (_customActions.SelectedItem is not CustomActionSetting action)
                    {
                        return;
                    }

                    await RunCustomCommandAsync(action.Command);
                }),
                Button(Strings.Advanced_AddEllipsis, async () =>
                {
                    var name = await _dialogs.PromptAsync(Strings.Advanced_CustomAction, Strings.Advanced_Name);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return;
                    }

                    var command = await _dialogs.PromptAsync(Strings.Advanced_CustomAction, Strings.Advanced_Command, "git status");
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
                Button(Strings.Advanced_Delete, async () =>
                {
                    if (_customActions.SelectedItem is not CustomActionSetting action)
                    {
                        return;
                    }

                    if (!await _dialogs.ConfirmAsync(Strings.Advanced_DeleteCustomAction, string.Format(Strings.Advanced_DeleteCustomActionConfirm, action.Name)))
                    {
                        return;
                    }

                    _repository.Settings.Settings.CustomActions.Remove(action);
                    _repository.Settings.Save();
                    RefreshCustomActions();
                }),
                Button(Strings.Advanced_RunCommandEllipsis, async () =>
                {
                    var command = await _dialogs.PromptAsync(Strings.Advanced_CustomAction, Strings.Advanced_Command, "git status");
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
        await RunAsync(Strings.Advanced_CustomAction, () => RunShellAsync(command));
    }
}



