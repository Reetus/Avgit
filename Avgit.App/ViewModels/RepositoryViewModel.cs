using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Threading;
using Avgit.App.Services;
using Avgit.Core.Diff;
using Avgit.Core.Graph;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.App.ViewModels;

/// <summary>
/// Per-repository view model. It combines the log, commit-detail and file-status
/// state for one open repository and keeps the UI free of framework-specific
/// dependency graphs.
/// </summary>
public sealed partial class RepositoryViewModel : ObservableObject, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _loadErrors = [];
    private bool _captureLoadErrors;
    private FileSystemWatcher? _watcher;
    private int _watchGeneration;
    private int _refsDirty;
    private bool _disposed;

    private CommitRowViewModel? _selectedCommit;
    private readonly List<CommitRowViewModel> _selectedCommits = [];
    private ChangedFile? _selectedCommitFile;
    private ChangedFile? _selectedStagedFile;
    private ChangedFile? _selectedUnstagedFile;
    private string? _commitTemplate;
    // Message box contents before "Amend" filled in the last commit's message,
    // and the message it filled in (to tell whether the user edited it).
    private string? _messageBeforeAmend;
    private string? _amendMessage;
    private int _amendGeneration;
    private bool _signOff;
    private bool _gpgSign;
    private bool _pushAfterCommit;
    private string _selectedBranchFilter = Strings.Repo_AllBranchesFilter;
    private int _diffContext;
    private bool _ignoreWhitespace;
    private bool _selectTipAfterLog;
    private int _logLimit = 500;
    private int _commitDiffGeneration;
    // Separate from _commitDiffGeneration: reloading a file diff must not make an
    // in-flight commit selection think it was superseded and drop its result.
    private int _commitSelectionGeneration;
    // SHA whose details, file list and diff are currently shown.
    private string? _displayedCommitSha;
    // SHA of the most recent commit load (possibly still running).
    private string? _requestedCommitSha;
    // Two commits selected in the history show their comparison in the right-hand
    // panes. The compare base is the one stored for the context-menu workflow.
    private CommitPair? _comparePair;
    private CommitSide? _compareBase;
    private CommitPair? _requestedComparePair;
    private CommitPair? _displayedComparePair;
    private bool _restoringCommitSelection;
    private Task? _selectionTask;
    private int _workingDiffGeneration;
    // What the working diff currently shows; a refresh producing the same text
    // is skipped so the view does not flash or lose scroll and line selection.
    private string? _workingDiffSignature;
    private int _logGeneration;
    // Graph layout after the last row in LogRows; the next page continues from it.
    private CommitGraphBuilder? _historyGraph;
    private int _statusGeneration;
    private bool _isLoadingCommitDetails;
    private bool _isLoadingCommitDiff;
    // Superseded git processes are cancelled (and killed) instead of running to completion.
    private CancellationTokenSource? _commitSelectionCts;
    private CancellationTokenSource? _commitDiffCts;
    private CancellationTokenSource? _workingDiffCts;
    // File watcher: inactive tabs only note that something changed; events are
    // coalesced per burst; folders git ignores are skipped.
    private bool _isActive = true;
    private int _changedWhileInactive;
    private long _burstStartTicks;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _ignoredDirectories =
        new(AppSettingsService.PathComparer);

    public RepositoryViewModel(IGitEngine handler, string path, AppSettingsService settings, IDialogService dialogs)
    {
        Handler = handler;
        Settings = settings;
        Dialogs = dialogs;
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = path;
        }

        // Rows are added in several places (cache, first page, more pages); mark worktree branches on all.
        LogRows.CollectionChanged += (_, e) =>
        {
            foreach (CommitRowViewModel row in e.NewItems?.OfType<CommitRowViewModel>() ?? [])
            {
                row.SetWorktreeBranches(_worktreeBranches);
            }
        };
        IsLinkedWorktree = !string.Equals(GitRepoHandler.GetGitDir(path), GitRepoHandler.GetCommonGitDir(path),
            AppSettingsService.PathComparison);
        RepoTypeText = IsLinkedWorktree ? Strings.Repo_RepoTypeLinkedWorktree : "Git";
        // Tests may wrap the handler; Advanced Git then gets its own instance.
        Advanced = new AdvancedGitService(handler as GitRepoHandler ?? new GitRepoHandler(handler.ExecutablePath), path);
        if (handler is GitRepoHandler git)
        {
            git.SshCommand = settings.Settings.GitSshCommand;
            git.UntrackedFilesMode = settings.Settings.ShowFilesInUntrackedFolders ? "all" : "normal";
            git.CredentialHelper = settings.Settings.GitCredentialHelper;
        }
        _diffContext = settings.Settings.DiffContext;
        _ignoreWhitespace = settings.Settings.IgnoreWhitespace;
        _signOff = settings.Settings.CommitSignOff;
        _gpgSign = settings.Settings.CommitGpgSign;
        _pushAfterCommit = settings.Settings.CommitPushAfter;

        CommitDiff = new DiffViewModel();
        WorkingDiff = new DiffViewModel();
        WireDiffActions();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        FetchCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpFetch, ct => Handler.FetchAsync(Path, null, ct)));
        PullCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpPull, ct => Handler.PullAsync(Path, null, ct)));
        PushCommand = new AsyncRelayCommand(PushAsync);
        OpenRemoteCommand = new RelayCommand(OpenRemote);
        PushToRemoteCommand = new AsyncRelayCommand(PushToRemoteAsync);
        ForcePushCommand = new AsyncRelayCommand(ForcePushAsync);
        PullRebaseCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpPullWithRebase, ct => Handler.PullAsync(Path, true, ct)));
        FetchAllCommand = new AsyncRelayCommand(FetchAllAsync);
        CommitCommand = new AsyncRelayCommand(CommitAsync);
        StageAllCommand = new AsyncRelayCommand(() => StageFilesAsync(UnstagedFiles));
        UnstageAllCommand = new AsyncRelayCommand(() => UnstageFilesAsync(StagedFiles));
        DiscardAllCommand = new AsyncRelayCommand(() => DiscardFilesAsync(UnstagedFiles, confirm: true));
        StageSelectedCommand = new AsyncRelayCommand(() => StageFilesAsync(Affected(UnstagedFiles, SelectedUnstagedFile)));
        UnstageSelectedCommand = new AsyncRelayCommand(() => UnstageFilesAsync(Affected(StagedFiles, SelectedStagedFile)));
        DiscardSelectedCommand = new AsyncRelayCommand(() => DiscardFilesAsync(Affected(UnstagedFiles, SelectedUnstagedFile), confirm: true));
        CreateBranchCommand = new AsyncRelayCommand(CreateBranchFromHeadAsync);
        CreateTagCommand = new AsyncRelayCommand(CreateTagAsync);
        StashCommand = new AsyncRelayCommand(StashAsync);
        CheckoutBranchCommand = new AsyncRelayCommand(p => CheckoutBranchAsync(p as BranchRef));
        CheckoutReferenceCommand = new AsyncRelayCommand(p => CheckoutReferenceAsync(p as string));
        DeleteBranchCommand = new AsyncRelayCommand(p => DeleteBranchAsync(p as BranchRef));
        AddWorktreeCommand = new AsyncRelayCommand(() => AddWorktreeAsync(null));
        AddWorktreeForBranchCommand = new AsyncRelayCommand(p => p is BranchRef branch ? AddWorktreeAsync(branch) : Task.CompletedTask);
        OpenWorktreeCommand = new AsyncRelayCommand(p => OpenWorktreeAsync(p as WorktreeEntry));
        OpenBranchWorktreeCommand = new AsyncRelayCommand(p => p is BranchRef { WorktreePath: { } worktree } ? OpenWorktreePathAsync(worktree) : Task.CompletedTask);
        RemoveWorktreeCommand = new AsyncRelayCommand(p => RemoveWorktreeAsync(p as WorktreeEntry));
        PruneWorktreesCommand = new AsyncRelayCommand(PruneWorktreesAsync);
        DeleteTagCommand = new AsyncRelayCommand(p => DeleteTagAsync(p as string));
        CheckoutCommitCommand = new AsyncRelayCommand(CheckoutCommitAsync);
        CreateBranchAtCommitCommand = new AsyncRelayCommand(CreateBranchAsync);
        CreateTagAtCommitCommand = new AsyncRelayCommand(CreateTagAsync);
        MergeCommitCommand = new AsyncRelayCommand(MergeCommitAsync);
        CherryPickCommand = new AsyncRelayCommand(CherryPickSelectedAsync);
        RevertCommitCommand = new AsyncRelayCommand(RevertSelectedAsync);
        SetCompareBaseCommand = new RelayCommand(() =>
        {
            _compareBase = SelectedCommit is { } row ? CommitSide.FromRow(row) : null;
            // Only records the base: the panes keep matching the list's selection.
            StatusMessage = _compareBase is null ? Strings.Repo_StatusSelectCommitToCompare : string.Format(Strings.Repo_StatusCompareBase, _compareBase.ShortSha);
        });
        CompareWithBaseCommand = new AsyncRelayCommand(CompareWithBaseAsync);
        SwapCompareCommand = new AsyncRelayCommand(SwapCompareAsync);
        RebaseCommitCommand = new AsyncRelayCommand(RebaseCommitAsync);
        ResetCommitCommand = new AsyncRelayCommand(ResetCommitAsync);
        ApplyStashCommand = new AsyncRelayCommand(p => ApplyStashAsync(p as StashEntry));
        PopStashCommand = new AsyncRelayCommand(p => PopStashAsync(p as StashEntry));
        DropStashCommand = new AsyncRelayCommand(p => DropStashAsync(p as StashEntry));
        OpenInExplorerCommand = new RelayCommand(OpenInExplorer);
        OpenTerminalCommand = new RelayCommand(OpenTerminal);
        OpenSelectedFileCommand = new RelayCommand(OpenSelectedFile);
        CopyCommitShaCommand = new RelayCommand(() =>
        {
            if (SelectedCommit is not null)
            {
                CopyText?.Invoke(SelectedCommit.Row.CommitId);
                StatusMessage = Strings.Repo_StatusCommitShaCopied;
            }
        });
        AbortMergeCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpAbortMerge, ct => Handler.AbortMergeAsync(Path, ct)));
        AbortRebaseCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpAbortRebase, ct => Handler.AbortRebaseAsync(Path, ct)));
        ContinueRebaseCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpContinueRebase, ct => Handler.ContinueRebaseAsync(Path, ct)));
        SkipRebaseCommitCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpSkipCommit, ct => Handler.SkipRebaseCommitAsync(Path, ct)));
        CherryPickContinueCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpContinueCherryPick, ct => Handler.CherryPickContinueAsync(Path, ct)));
        CherryPickAbortCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpAbortCherryPick, ct => Handler.CherryPickAbortAsync(Path, ct)));
        CherryPickSkipCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpSkipCherryPick, ct => Handler.CherryPickSkipAsync(Path, ct)));
        RevertContinueCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpContinueRevert, ct => Handler.RevertContinueAsync(Path, ct)));
        RevertAbortCommand = new AsyncRelayCommand(() => RunOperationAsync(Strings.Repo_OpAbortRevert, ct => Handler.RevertAbortAsync(Path, ct)));
        InteractiveRebaseCommand = new AsyncRelayCommand(InteractiveRebaseAsync);
        AddRemoteCommand = new AsyncRelayCommand(AddRemoteAsync);
        DeleteRemoteCommand = new AsyncRelayCommand(p => DeleteRemoteAsync(p as RemoteEntry));
        EditRemoteCommand = new AsyncRelayCommand(p => EditRemoteAsync(p as RemoteEntry));
        ApplyLogFilterCommand = new AsyncRelayCommand(LoadLogCoreAsync);
        LoadMoreLogCommand = new AsyncRelayCommand(LoadMoreLogAsync);
        ClearLogFilterCommand = new AsyncRelayCommand(() =>
        {
            AuthorFilter = string.Empty;
            _selectedBranchFilter = Strings.Repo_AllBranchesFilter;
            OnPropertyChanged(nameof(SelectedBranchFilter));
            return LoadLogCoreAsync();
        });
    }

    public string Path { get; }
    public string Name { get; }
    public string RepoTypeText { get; }

    /// <summary>This tab is a linked worktree (created by <c>git worktree add</c>), not the main one.</summary>
    public bool IsLinkedWorktree { get; }

    /// <summary>The main worktree's folder, shown when this tab is a linked worktree.</summary>
    [ObservableProperty]
    public partial string MainWorktreePath { get; private set; } = string.Empty;

    /// <summary>Opens a folder as a repository tab (or switches to it when already open).</summary>
    public Func<string, Task>? OpenRepositoryAction { get; set; }

    /// <summary>Called with a worktree's folder after it is removed, so its tab can close.</summary>
    public Action<string>? WorktreeRemoved { get; set; }
    public AdvancedGitService Advanced { get; }
    public IDialogService Dialogs { get; }
    public AppSettingsService Settings { get; }

    public void SetGitExecutable(string executablePath)
    {
        if (Handler is GitRepoHandler git)
        {
            git.ExecutablePath = executablePath;
        }

        SetGitEnvironment();
    }

    public void SetGitEnvironment()
    {
        if (Handler is GitRepoHandler git)
        {
            git.SshCommand = Settings.Settings.GitSshCommand;
            git.UntrackedFilesMode = Settings.Settings.ShowFilesInUntrackedFolders ? "all" : "normal";
            git.CredentialHelper = Settings.Settings.GitCredentialHelper;
        }
    }
    public bool HasRemotes => Remotes.Count > 0;
    public bool HasRemoteBranches => RemoteBranches.Count > 0;
    public bool HasTags => Tags.Count > 0;
    public bool HasStashes => Stashes.Count > 0;
    public IGitEngine Handler { get; }

    public ObservableCollection<CommitRowViewModel> LogRows { get; } = [];
    public ObservableCollection<ChangedFile> CommitFiles { get; } = [];
    public ObservableCollection<ChangedFile> StagedFiles { get; } = [];
    public ObservableCollection<ChangedFile> UnstagedFiles { get; } = [];
    public ObservableCollection<BranchRef> Branches { get; } = [];
    public ObservableCollection<BranchRef> RemoteBranches { get; } = [];
    public ObservableCollection<WorktreeEntry> Worktrees { get; } = [];

    // Local branches checked out in other worktrees, for the history's branch pills.
    private IReadOnlySet<string> _worktreeBranches = new HashSet<string>();
    public bool HasLinkedWorktrees => Worktrees.Count > 1;
    public bool HasPrunableWorktrees => Worktrees.Any(w => w.IsPrunable);
    public ObservableCollection<RemoteEntry> Remotes { get; } = [];
    public ObservableCollection<string> Tags { get; } = [];
    public ObservableCollection<StashEntry> Stashes { get; } = [];
    public ObservableCollection<string> BranchFilterOptions { get; } = [Strings.Repo_AllBranchesFilter];
    public IReadOnlyList<int> ContextOptions { get; } = [1, 3, 6, 12, 25, 50, 100];

    public DiffViewModel CommitDiff { get; }
    public DiffViewModel WorkingDiff { get; }

    public Action<string>? CopyText { get; set; }

    public CommitRowViewModel? SelectedCommit
    {
        get => _selectedCommit;
        set
        {
            if (SetProperty(ref _selectedCommit, value))
            {
                // An explicit single-commit selection replaces the list's
                // multi-selection (e.g. jumping to a branch's tip).
                _selectedCommits.Clear();
                if (value is not null)
                {
                    _selectedCommits.Add(value);
                }

                _selectionTask = SelectCommitAsync(value);
            }
        }
    }

    public void SetSelectedCommits(IEnumerable<CommitRowViewModel> commits)
    {
        _selectedCommits.Clear();
        _selectedCommits.AddRange(commits);

        // A refresh restores the list's selection; that is not a user change and
        // must not replace the panes the refresh is keeping.
        if (_restoringCommitSelection)
        {
            return;
        }

        switch (_selectedCommits.Count)
        {
            case 1 when _comparePair is not null:
                // Back from a comparison to one commit: show that commit again.
                SetContextCommit(_selectedCommits[0]);
                break;
            case 2:
                ShowCompareForSelection();
                break;
            case >= 3 when _comparePair is not null:
                // Three or more commits keep the normal details of the active
                // (last-clicked) commit.
                CommitRowViewModel active = _selectedCommit is not null && _selectedCommits.Contains(_selectedCommit)
                    ? _selectedCommit
                    : _selectedCommits[0];
                SetContextCommit(active);
                break;
        }
    }

    /// <summary>
    /// Applies a selection restored by the history list without treating it as a
    /// user selection change (which would start loading other panes).
    /// </summary>
    public void RestoreCommitSelection(Action apply)
    {
        _restoringCommitSelection = true;
        try
        {
            apply();
        }
        finally
        {
            _restoringCommitSelection = false;
        }
    }

    /// <summary>The rows the history list currently shows as selected.</summary>
    public IReadOnlyList<CommitRowViewModel> SelectedCommits => _selectedCommits;

    private void ShowCompareForSelection()
    {
        int first = LogRows.IndexOf(_selectedCommits[0]);
        int second = LogRows.IndexOf(_selectedCommits[1]);
        if (first < 0 || second < 0 || first == second)
        {
            return;
        }

        // LogRows is newest-first, so the row further down is the older side.
        CommitRowViewModel baseRow = first > second ? _selectedCommits[0] : _selectedCommits[1];
        CommitRowViewModel targetRow = first > second ? _selectedCommits[1] : _selectedCommits[0];
        var pair = new CommitPair(CommitSide.FromRow(baseRow), CommitSide.FromRow(targetRow));
        if (Equals(pair, _requestedComparePair))
        {
            return;
        }

        _selectionTask = ShowCompareAsync(pair);
    }

    /// <summary>
    /// Sets the context/active commit without forcing the multi-selection to a
    /// single row. Used by the history context menu.
    /// </summary>
    public void SetContextCommit(CommitRowViewModel? commit)
    {
        if (_restoringCommitSelection)
        {
            return;
        }

        // With exactly two commits selected the panes show their comparison;
        // the active commit only drives the commands that act on one commit.
        if (_selectedCommits.Count == 2)
        {
            if (!ReferenceEquals(_selectedCommit, commit))
            {
                _selectedCommit = commit;
                OnPropertyChanged(nameof(SelectedCommit));
            }
            return;
        }

        // Skip only when this commit is both selected and already displayed; a
        // selected row whose details were never shown (e.g. after a refresh
        // raced with a click) must still load when clicked.
        // A load already in flight for it counts too: the list echoes the selection
        // back right after history loads, and must not restart that load.
        if (ReferenceEquals(_selectedCommit, commit)
            && (string.Equals(commit?.Row.CommitId, _displayedCommitSha, StringComparison.Ordinal)
                || string.Equals(commit?.Row.CommitId, _requestedCommitSha, StringComparison.Ordinal)))
        {
            return;
        }

        _selectedCommit = commit;
        OnPropertyChanged(nameof(SelectedCommit));
        _selectionTask = SelectCommitAsync(commit);
    }

    public ChangedFile? SelectedCommitFile
    {
        get => _selectedCommitFile;
        set
        {
            if (SetProperty(ref _selectedCommitFile, value))
            {
                _ = LoadCommitDiffAsync();
            }
        }
    }

    public ChangedFile? SelectedStagedFile
    {
        get => _selectedStagedFile;
        set
        {
            if (SetProperty(ref _selectedStagedFile, value) && value is not null)
            {
                _selectedUnstagedFile = null;
                OnPropertyChanged(nameof(SelectedUnstagedFile));
                _ = LoadWorkingDiffAsync(value, staged: true);
            }
        }
    }

    public ChangedFile? SelectedUnstagedFile
    {
        get => _selectedUnstagedFile;
        set
        {
            if (SetProperty(ref _selectedUnstagedFile, value) && value is not null)
            {
                _selectedStagedFile = null;
                OnPropertyChanged(nameof(SelectedStagedFile));
                _ = LoadWorkingDiffAsync(value, staged: false);
            }
        }
    }

    [ObservableProperty]
    public partial CommitInfoViewModel? CommitInfo { get; private set; }

    /// <summary>Two commits are selected, so the right-hand panes compare them.</summary>
    public bool IsComparingCommits => _comparePair is not null;

    public string CompareBaseSha => _comparePair?.Base.ShortSha ?? string.Empty;
    public string CompareBaseSubject => _comparePair?.Base.Title ?? string.Empty;
    public string CompareTargetSha => _comparePair?.Target.ShortSha ?? string.Empty;
    public string CompareTargetSubject => _comparePair?.Target.Title ?? string.Empty;

    private void SetComparePair(CommitPair? pair)
    {
        if (Equals(_comparePair, pair))
        {
            return;
        }

        _comparePair = pair;
        OnPropertyChanged(nameof(IsComparingCommits));
        OnPropertyChanged(nameof(CompareBaseSha));
        OnPropertyChanged(nameof(CompareBaseSubject));
        OnPropertyChanged(nameof(CompareTargetSha));
        OnPropertyChanged(nameof(CompareTargetSubject));
    }

    public string CommitMessage
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CanCommit));
            }
        }
    } = string.Empty;

    public bool Amend
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CanCommit));
                if (value)
                {
                    _ = LoadAmendMessageAsync();
                }
                else
                {
                    RestoreMessageAfterAmend();
                }
            }
        }
    }

    /// <summary>
    /// Fills the message box with the last commit's full message when amending.
    /// An empty box or the untouched commit template is replaced directly; a
    /// message the user typed is only replaced after confirmation.
    /// </summary>
    private async Task LoadAmendMessageAsync()
    {
        var generation = ++_amendGeneration;
        try
        {
            CommitRecord? details = await Handler.GetCommitDetailsAsync(Path, "HEAD").ConfigureAwait(true);
            // An unborn branch has no HEAD; that is reported below, not as a load error.
            Handler.ClearLastError();
            if (generation != _amendGeneration || !Amend)
            {
                return;
            }

            var message = details?.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                StatusMessage = Strings.Repo_StatusNoCommitToAmend;
                return;
            }

            var current = CommitMessage;
            var isPlaceholder = string.IsNullOrWhiteSpace(current)
                                || string.Equals(current, _commitTemplate, StringComparison.Ordinal);
            if (!isPlaceholder && !string.Equals(current, message, StringComparison.Ordinal))
            {
                if (!await Dialogs.ConfirmAsync(Strings.Repo_AmendLastCommitTitle,
                        Strings.Repo_AmendReplaceMessageConfirm).ConfigureAwait(true))
                {
                    return;
                }

                if (generation != _amendGeneration || !Amend)
                {
                    return;
                }
            }

            _messageBeforeAmend = current;
            _amendMessage = message;
            CommitMessage = message;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Unchecking amend restores the earlier draft unless the amended message was edited.</summary>
    private void RestoreMessageAfterAmend()
    {
        ++_amendGeneration;
        if (_amendMessage is not null && string.Equals(CommitMessage, _amendMessage, StringComparison.Ordinal))
        {
            CommitMessage = _messageBeforeAmend ?? string.Empty;
        }

        _amendMessage = null;
        _messageBeforeAmend = null;
    }

    public bool SignOff
    {
        get => _signOff;
        set
        {
            if (SetProperty(ref _signOff, value))
            {
                Settings.Settings.CommitSignOff = value;
                Settings.Save();
            }
        }
    }

    public bool GpgSign
    {
        get => _gpgSign;
        set
        {
            if (SetProperty(ref _gpgSign, value))
            {
                Settings.Settings.CommitGpgSign = value;
                Settings.Save();
            }
        }
    }

    /// <summary>Push the branch after each successful commit (remembered across sessions).</summary>
    public bool PushAfterCommit
    {
        get => _pushAfterCommit;
        set
        {
            if (SetProperty(ref _pushAfterCommit, value))
            {
                Settings.Settings.CommitPushAfter = value;
                Settings.Save();
                OnPropertyChanged(nameof(CommitButtonText));
            }
        }
    }

    public string CommitButtonText => PushAfterCommit ? Strings.Repo_CommitAndPushButton : Strings.Repo_CommitButton;

    /// <summary>The web page of origin (or the only remote) when its URL is http(s); otherwise null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoteWebUrl))]
    [NotifyPropertyChangedFor(nameof(RemoteWebUrlToolTip))]
    public partial string? RemoteWebUrl { get; private set; }

    public bool HasRemoteWebUrl => RemoteWebUrl is not null;
    public string RemoteWebUrlToolTip => string.Format(Strings.Repo_OpenRemoteWebUrlToolTip, RemoteWebUrl);

    /// <summary>
    /// The browser address for a remote URL: http(s) only, without credentials or a
    /// trailing <c>.git</c>. SSH URLs return null; their web host cannot be inferred reliably.
    /// </summary>
    internal static string? WebUrlFor(string? remoteUrl)
    {
        if (!Uri.TryCreate(remoteUrl?.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
        return $"{uri.Scheme}://{uri.Host}{port}{path}";
    }

    private void OpenRemote()
    {
        if (RemoteWebUrl is not { } url)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusMessage = string.Format(Strings.Repo_StatusCouldNotOpen, url, ex.Message);
        }
    }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = Strings.Repo_StatusReady;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public string CurrentBranch
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(BranchAndRevisionText));
            }
        }
    } = string.Empty;

    public string CurrentRevision
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CurrentRevisionShort));
                OnPropertyChanged(nameof(BranchAndRevisionText));
            }
        }
    } = string.Empty;

    public string CurrentRevisionShort => CurrentRevision.Length > 8 ? CurrentRevision[..8] : CurrentRevision;
    public string BranchAndRevisionText => string.IsNullOrEmpty(CurrentRevision)
        ? CurrentBranch
        : $"{CurrentBranch}  @  {CurrentRevisionShort}";
    public string LogCountText => IsShowingCachedHistory
        ? string.Format(Strings.Repo_LogCountCached, LogRows.Count)
        : string.Format(Strings.Repo_LogCount, LogRows.Count);

    public bool IsMergeInProgress
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasConflict));
                OnPropertyChanged(nameof(ConflictText));
            }
        }
    }

    public bool IsRebaseInProgress
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasConflict));
                OnPropertyChanged(nameof(ConflictText));
            }
        }
    }

    public BranchRef? SelectedBranch
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                _ = JumpToBranchAsync(value);
            }
        }
    }

    public BranchRef? SelectedRemoteBranch
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                _ = JumpToBranchAsync(value);
            }
        }
    }

    public event Action<string>? ScrollToCommitRequested;

    private async Task JumpToBranchAsync(BranchRef? branch)
    {
        if (branch is null || branch.Target.Length == 0)
        {
            return;
        }

        CommitRowViewModel? row = LogRows.FirstOrDefault(r =>
            string.Equals(r.Row.CommitId, branch.Target, StringComparison.Ordinal));

        if (row is null)
        {
            if (!string.Equals(_selectedBranchFilter, branch.Name, StringComparison.Ordinal))
            {
                _selectedBranchFilter = branch.Name;
                OnPropertyChanged(nameof(SelectedBranchFilter));
            }

            await LoadLogCoreAsync().ConfigureAwait(true);
            row = LogRows.FirstOrDefault(r =>
                string.Equals(r.Row.CommitId, branch.Target, StringComparison.Ordinal));
        }

        if (row is not null)
        {
            SelectedCommit = row;
            ScrollToCommitRequested?.Invoke(row.Row.CommitId);
        }
    }

    public bool HasConflict => IsMergeInProgress || IsRebaseInProgress
                               || IsCherryPickInProgress || IsRevertInProgress;

    public bool IsCherryPickInProgress
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasConflict));
                OnPropertyChanged(nameof(ConflictText));
            }
        }
    }

    public bool IsRevertInProgress
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasConflict));
                OnPropertyChanged(nameof(ConflictText));
            }
        }
    }

    public string ConflictText => IsRebaseInProgress
        ? Strings.Repo_ConflictRebase
        : IsCherryPickInProgress
            ? Strings.Repo_ConflictCherryPick
            : IsRevertInProgress
                ? Strings.Repo_ConflictRevert
                : IsMergeInProgress
                    ? Strings.Repo_ConflictMerge
                    : string.Empty;

    [ObservableProperty]
    public partial string AuthorFilter { get; set; } = string.Empty;

    public string SelectedBranchFilter
    {
        get => _selectedBranchFilter;
        set
        {
            // Each refresh rebuilds BranchFilterOptions; the ComboBox then clears its
            // selection and writes null back. That is not a choice: taking it as one
            // reloaded the history and moved the selection to the tip.
            if (value is null)
            {
                return;
            }

            if (SetProperty(ref _selectedBranchFilter, value) && IsLoaded)
            {
                _selectTipAfterLog = value != Strings.Repo_AllBranchesFilter;
                _ = LoadLogCoreAsync();
            }
        }
    }

    public int DiffContext
    {
        get => _diffContext;
        set
        {
            if (SetProperty(ref _diffContext, value))
            {
                Settings.Settings.DiffContext = value;
                Settings.Save();
                _ = ReloadAllDiffsAsync();
            }
        }
    }

    public bool IgnoreWhitespace
    {
        get => _ignoreWhitespace;
        set
        {
            if (SetProperty(ref _ignoreWhitespace, value))
            {
                Settings.Settings.IgnoreWhitespace = value;
                Settings.Save();
                _ = ReloadAllDiffsAsync();
            }
        }
    }

    public bool CanCommit => !string.IsNullOrWhiteSpace(CommitMessage)
                             && (StagedFiles.Count > 0 || Amend)
                             && !HasUnresolvedConflicts;

    public bool HasUnresolvedConflicts => StagedFiles.Concat(UnstagedFiles)
        .Any(f => f.Status == PathState.Conflicted);

    public string StagedHeader => string.Format(Strings.Repo_StagedHeader, StagedFiles.Count);
    public string UnstagedHeader => string.Format(Strings.Repo_UnstagedHeader, UnstagedFiles.Count);

    public ICommand RefreshCommand { get; }
    public ICommand FetchCommand { get; }
    public ICommand PullCommand { get; }
    public ICommand PushCommand { get; }
    public ICommand OpenRemoteCommand { get; }
    public ICommand PushToRemoteCommand { get; }
    public ICommand ForcePushCommand { get; }
    public ICommand PullRebaseCommand { get; }
    public ICommand FetchAllCommand { get; }
    public ICommand CommitCommand { get; }
    public ICommand StageAllCommand { get; }
    public ICommand UnstageAllCommand { get; }
    public ICommand DiscardAllCommand { get; }
    public ICommand StageSelectedCommand { get; }
    public ICommand UnstageSelectedCommand { get; }
    public ICommand DiscardSelectedCommand { get; }
    public ICommand CreateBranchCommand { get; }
    public ICommand CreateTagCommand { get; }
    public ICommand StashCommand { get; }
    public ICommand CheckoutBranchCommand { get; }
    public ICommand CheckoutReferenceCommand { get; }
    public ICommand DeleteBranchCommand { get; }
    public ICommand AddWorktreeCommand { get; }
    public ICommand AddWorktreeForBranchCommand { get; }
    public ICommand OpenWorktreeCommand { get; }
    public ICommand OpenBranchWorktreeCommand { get; }
    public ICommand RemoveWorktreeCommand { get; }
    public ICommand PruneWorktreesCommand { get; }
    public ICommand DeleteTagCommand { get; }
    public ICommand CheckoutCommitCommand { get; }
    public ICommand CreateBranchAtCommitCommand { get; }
    public ICommand CreateTagAtCommitCommand { get; }
    public ICommand MergeCommitCommand { get; }
    public ICommand CherryPickCommand { get; }
    public ICommand RevertCommitCommand { get; }
    public ICommand SetCompareBaseCommand { get; }
    public ICommand CompareWithBaseCommand { get; }
    public ICommand SwapCompareCommand { get; }
    public ICommand RebaseCommitCommand { get; }
    public ICommand ResetCommitCommand { get; }
    public ICommand ApplyStashCommand { get; }
    public ICommand PopStashCommand { get; }
    public ICommand DropStashCommand { get; }
    public ICommand OpenInExplorerCommand { get; }
    public ICommand OpenTerminalCommand { get; }
    public ICommand OpenSelectedFileCommand { get; }
    public ICommand CopyCommitShaCommand { get; }
    public ICommand AbortMergeCommand { get; }
    public ICommand AbortRebaseCommand { get; }
    public ICommand ContinueRebaseCommand { get; }
    public ICommand SkipRebaseCommitCommand { get; }
    public ICommand CherryPickContinueCommand { get; }
    public ICommand CherryPickAbortCommand { get; }
    public ICommand CherryPickSkipCommand { get; }
    public ICommand RevertContinueCommand { get; }
    public ICommand RevertAbortCommand { get; }
    public ICommand InteractiveRebaseCommand { get; }
    public ICommand AddRemoteCommand { get; }
    public ICommand DeleteRemoteCommand { get; }
    public ICommand EditRemoteCommand { get; }
    public ICommand ApplyLogFilterCommand { get; }
    public ICommand LoadMoreLogCommand { get; }
    public ICommand ClearLogFilterCommand { get; }

    private const int HistoryPageSize = 500;
    private Task? _loadTask;

    private HistoryCache HistoryCacheStore => field ??= HistoryCache.ForSettings(Settings);

    /// <summary>True once the first full load has completed.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Tabs that have not loaded yet are drawn dimmed.</summary>
    public double TabOpacity => IsLoaded ? 1.0 : 0.6;

    [ObservableProperty]
    public partial bool IsLoadingHistory { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingStatus { get; private set; }

    /// <summary>Commit details, file list or commit diff are loading.</summary>
    public bool IsLoadingCommit => _isLoadingCommitDetails || _isLoadingCommitDiff;

    [ObservableProperty]
    public partial bool IsLoadingWorkingDiff { get; private set; }

    /// <summary>History shows the cached copy from the last session until git returns the live one.</summary>
    public bool IsShowingCachedHistory
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(LogCountText));
            }
        }
    }

    private void SetLoadingCommitDetails(bool value)
    {
        if (_isLoadingCommitDetails == value)
        {
            return;
        }

        _isLoadingCommitDetails = value;
        OnPropertyChanged(nameof(IsLoadingCommit));
    }

    private void SetLoadingCommitDiff(bool value)
    {
        if (_isLoadingCommitDiff == value)
        {
            return;
        }

        _isLoadingCommitDiff = value;
        OnPropertyChanged(nameof(IsLoadingCommit));
    }

    /// <summary>
    /// Loads the repository the first time it is called; later calls return the
    /// same task. Tabs restored at startup call this only when first shown.
    /// </summary>
    public Task LoadAsync() => _loadTask ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            IsBusy = true;
            StatusMessage = string.Format(Strings.Repo_StatusLoading, Name);
            // Last session's history appears immediately; git replaces it when ready.
            ShowCachedHistory();
            Task<string?>? templateTask = string.IsNullOrWhiteSpace(CommitMessage)
                ? Handler.GetCommitTemplateAsync(Path)
                : null;
            await ReloadAllCoreAsync().ConfigureAwait(true);
            IsLoaded = true;
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(TabOpacity));
            if (templateTask is not null && string.IsNullOrWhiteSpace(CommitMessage))
            {
                var template = await templateTask.ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(template))
                {
                    _commitTemplate = template;
                    CommitMessage = template;
                }
            }
            StatusMessage = _loadErrors.Count > 0 ? string.Join(" | ", _loadErrors) : SummaryText;
            if (Settings.Settings.AutoRefresh)
            {
                StartWatcher();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await Dialogs.ShowMessageAsync(Strings.Repo_CouldNotLoadRepositoryTitle, ex.ToString()).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }

        if (Settings.Settings.MaintainCommitGraph && !_disposed)
        {
            _ = MaintainCommitGraphAsync();
        }
    }

    /// <summary>
    /// Without a commit-graph, `git log --topo-order` walks the whole history
    /// before printing anything; writing one (as git gc would) once makes every
    /// later history load several times faster.
    /// </summary>
    private async Task MaintainCommitGraphAsync()
    {
        try
        {
            await Handler.EnsureCommitGraphAsync(Path).ConfigureAwait(false);
        }
        catch
        {
            // Purely an optimisation.
        }
    }

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            IsBusy = true;
            StatusMessage = Strings.Repo_StatusRefreshing;
            await ReloadAllCoreAsync().ConfigureAwait(true);
            StatusMessage = _loadErrors.Count > 0 ? string.Join(" | ", _loadErrors) : SummaryText;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }

    private async Task ReloadAllCoreAsync()
    {
        _loadErrors.Clear();
        _captureLoadErrors = true;
        try
        {
            // Status, refs and history are independent: run them together and let
            // each pane fill in as soon as its own data arrives.
            await Task.WhenAll(LoadStatusCoreAsync(), LoadRefsCoreAsync(), LoadLogCoreAsync()).ConfigureAwait(true);

            if (_loadErrors.Count > 0)
            {
                StatusMessage = string.Join(" | ", _loadErrors);
            }
        }
        finally
        {
            _captureLoadErrors = false;
        }
    }

    private async Task LoadRefsCoreAsync()
    {
        RefsSnapshot refs = await Handler.GetRefsSnapshotAsync(Path).ConfigureAwait(true);
        if (refs.Error is not null)
        {
            RecordError(Strings.Repo_ErrorReadRefs, refs.Error);
            return;
        }

        Branches.Clear();
        RemoteBranches.Clear();
        foreach (BranchRef branch in refs.Branches)
        {
            if (branch.Kind == RefKind.RemoteBranch)
            {
                RemoteBranches.Add(branch);
            }
            else
            {
                Branches.Add(branch);
            }
        }

        Tags.Clear();
        foreach (var tag in refs.Tags)
        {
            Tags.Add(tag);
        }

        Remotes.Clear();
        foreach (KeyValuePair<string, string> remote in refs.Remotes)
        {
            Remotes.Add(new RemoteEntry { Name = remote.Key, Url = remote.Value });
        }

        RemoteEntry? webRemote = Remotes.FirstOrDefault(r => r.Name == "origin") ?? (Remotes.Count == 1 ? Remotes[0] : null);
        RemoteWebUrl = WebUrlFor(webRemote?.Url);

        Stashes.Clear();
        foreach (StashRecord stash in refs.Stashes)
        {
            Stashes.Add(new StashEntry(stash));
        }

        Worktrees.Clear();
        for (var i = 0; i < refs.Worktrees.Count; i++)
        {
            WorktreeInfo worktree = refs.Worktrees[i];
            Worktrees.Add(new WorktreeEntry(worktree, IsSamePath(worktree.Path, Path), isMain: i == 0));
        }

        MainWorktreePath = IsLinkedWorktree && Worktrees.Count > 0 ? Worktrees[0].Path : string.Empty;
        var worktreeBranches = Branches.Where(b => b.IsInOtherWorktree).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        if (!worktreeBranches.SetEquals(_worktreeBranches))
        {
            _worktreeBranches = worktreeBranches;
            foreach (CommitRowViewModel row in LogRows)
            {
                row.SetWorktreeBranches(worktreeBranches);
            }
        }
        OnPropertyChanged(nameof(HasLinkedWorktrees));
        OnPropertyChanged(nameof(HasPrunableWorktrees));
        OnPropertyChanged(nameof(HasRemotes));
        OnPropertyChanged(nameof(HasRemoteBranches));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasStashes));

        var previousFilter = SelectedBranchFilter;
        List<string> options = [Strings.Repo_AllBranchesFilter, .. Branches.Select(b => b.Name), .. RemoteBranches.Select(b => b.Name)];
        UpdateInPlace(BranchFilterOptions, options);
        _selectedBranchFilter = BranchFilterOptions.Contains(previousFilter) ? previousFilter : Strings.Repo_AllBranchesFilter;
        OnPropertyChanged(nameof(SelectedBranchFilter));
    }

    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="items"/> by moving, inserting and
    /// removing entries. Clearing it instead made the branch filter ComboBox drop its selected
    /// item on every refresh and show nothing.
    /// </summary>
    private static void UpdateInPlace(ObservableCollection<string> target, IReadOnlyList<string> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count && string.Equals(target[i], items[i], StringComparison.Ordinal))
            {
                continue;
            }

            var existing = target.IndexOf(items[i]);
            if (existing > i)
            {
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, items[i]);
            }
        }

        while (target.Count > items.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private bool ReportHandlerError(string context)
    {
        var error = Handler.LastError;
        if (string.IsNullOrEmpty(error))
        {
            return false;
        }

        Handler.ClearLastError();
        RecordError(context, error);
        return true;
    }

    private void RecordError(string context, string error)
    {
        var message = $"{context}: {error}";
        if (_captureLoadErrors)
        {
            _loadErrors.Add(message);
        }

        StatusMessage = message;
        SummaryText = _loadErrors.Count > 0 ? string.Join(" | ", _loadErrors) : message;
    }

    private bool IsUnfilteredHistory => SelectedBranchFilter == Strings.Repo_AllBranchesFilter && string.IsNullOrWhiteSpace(AuthorFilter);

    private HistoryFilter BuildLogQuery(int skip, int limit) => new()
    {
        Offset = (uint)skip,
        Count = (uint)limit,
        StartRef = SelectedBranchFilter == Strings.Repo_AllBranchesFilter ? null : SelectedBranchFilter,
        Author = string.IsNullOrWhiteSpace(AuthorFilter) ? null : AuthorFilter,
        Flags = HistoryFlags.Merges | HistoryFlags.Remotes | HistoryFlags.Topological
    };

    /// <summary>
    /// Lays out graph lanes and creates row view models. Runs off the UI thread:
    /// for thousands of rows this kept scrolling responsive during refreshes.
    /// </summary>
    /// <param name="rows">The rows to show, newest first.</param>
    /// <param name="continueFrom">
    /// Layout state after the last loaded row when appending a page; null for a fresh
    /// load. It is not modified: the returned layout is a copy to keep once the rows are shown.
    /// </param>
    /// <param name="parentsMayBeMissing">True for filtered histories, whose parents are mostly hidden.</param>
    private static (List<CommitRowViewModel> Rows, double GraphWidth, CommitGraphBuilder Graph) BuildHistoryRows(
        IReadOnlyList<HistoryRow> rows, CommitGraphBuilder? continueFrom, bool parentsMayBeMissing = false)
    {
        CommitGraphBuilder graph = continueFrom?.Clone() ?? new CommitGraphBuilder(parentsMayBeMissing);
        graph.Append(rows);

        var list = new List<CommitRowViewModel>(rows.Count);
        foreach (HistoryRow row in rows)
        {
            list.Add(new CommitRowViewModel(row));
        }

        return (list, CommitRowViewModel.GraphWidthFor(rows), graph);
    }

    /// <summary>
    /// Author filters hide commits without rewriting parents, so their parents are
    /// mostly absent from the list and are drawn as stubs rather than open lanes.
    /// </summary>
    private bool HistoryHidesParents => !string.IsNullOrWhiteSpace(AuthorFilter);

    private void ShowCachedHistory()
    {
        if (LogRows.Count > 0 || !IsUnfilteredHistory)
        {
            return;
        }

        IReadOnlyList<HistoryRow>? cached = HistoryCacheStore.TryLoad(Path);
        if (cached is null || cached.Count == 0)
        {
            return;
        }

        (List<CommitRowViewModel>? rows, var width, CommitGraphBuilder graph) = BuildHistoryRows(cached, continueFrom: null);
        _historyGraph = graph;
        foreach (CommitRowViewModel row in rows)
        {
            row.GraphWidth = width;
            LogRows.Add(row);
        }

        IsShowingCachedHistory = true;
        OnPropertyChanged(nameof(LogCountText));
    }

    private async Task LoadLogCoreAsync()
    {
        var generation = ++_logGeneration;
        IsLoadingHistory = true;
        CommitRowViewModel? toSelect;
        bool sameSelection;
        CommitPair? compareToShow = null;
        try
        {
            LogPage page = await Handler.GetLogPageAsync(Path, BuildLogQuery(0, _logLimit)).ConfigureAwait(true);
            if (generation != _logGeneration)
            {
                return;
            }

            if (page.Error is not null)
            {
                RecordError(Strings.Repo_ErrorReadHistory, page.Error);
                return;
            }

            var hidesParents = HistoryHidesParents;
            (List<CommitRowViewModel>? rows, var width, CommitGraphBuilder graph) =
                await Task.Run(() => BuildHistoryRows(page.Rows, continueFrom: null, hidesParents)).ConfigureAwait(true);
            if (generation != _logGeneration)
            {
                return;
            }

            // Read the selection only now: the user may have clicked another commit
            // while git log was running, and that click must win over the old one.
            var selectedSha = SelectedCommit?.Row.CommitId;
            CommitPair? compare = _comparePair;
            LogRows.Clear();
            _historyGraph = graph;
            foreach (CommitRowViewModel? row in rows)
            {
                row.GraphWidth = width;
                LogRows.Add(row);
            }
            IsShowingCachedHistory = false;
            if (IsUnfilteredHistory)
            {
                HistoryCacheStore.Save(Path, page.Rows, HistoryPageSize);
            }

            var selectTip = _selectTipAfterLog;
            _selectTipAfterLog = false;
            CommitRowViewModel? reselect = selectedSha is null
                ? null
                : LogRows.FirstOrDefault(r => r.Row.CommitId == selectedSha);
            CommitRowViewModel? compareBaseRow = compare is null
                ? null
                : LogRows.FirstOrDefault(r => r.Row.CommitId == compare.Base.Sha);
            CommitRowViewModel? compareTargetRow = compare is null
                ? null
                : LogRows.FirstOrDefault(r => r.Row.CommitId == compare.Target.Sha);
            if (!selectTip && compareBaseRow is not null && compareTargetRow is not null)
            {
                // Keep the comparison across a refresh, re-resolved to the new rows.
                var refreshed = new CommitPair(CommitSide.FromRow(compareBaseRow), CommitSide.FromRow(compareTargetRow));
                SetComparePair(refreshed);
                toSelect = reselect is not null
                           && (ReferenceEquals(reselect, compareBaseRow) || ReferenceEquals(reselect, compareTargetRow))
                    ? reselect
                    : compareTargetRow;
                _selectedCommits.Clear();
                _selectedCommits.Add(compareBaseRow);
                _selectedCommits.Add(compareTargetRow);
                // Commit contents are immutable for a given SHA, so reloading the
                // comparison is unnecessary when it is the one already displayed.
                sameSelection = Equals(refreshed, _displayedComparePair);
                if (!sameSelection)
                {
                    compareToShow = refreshed;
                }
            }
            else
            {
                if (compare is not null)
                {
                    // The compared commits left the (filtered) history.
                    _requestedComparePair = null;
                    _displayedComparePair = null;
                    SetComparePair(null);
                }

                // Commit contents are immutable for a given SHA, so reloading the
                // details/diff is unnecessary when the reselected commit is the one
                // already displayed.
                sameSelection = !selectTip
                                && reselect is not null
                                && string.Equals(_displayedCommitSha, reselect.Row.CommitId, StringComparison.Ordinal);
                toSelect = selectTip ? LogRows.FirstOrDefault() : (reselect ?? LogRows.FirstOrDefault());
                _selectedCommits.Clear();
                if (toSelect is not null)
                {
                    _selectedCommits.Add(toSelect);
                }
            }

            _selectedCommit = toSelect;
            OnPropertyChanged(nameof(SelectedCommit));
            OnPropertyChanged(nameof(LogCountText));
            if (toSelect is not null)
            {
                ScrollToCommitRequested?.Invoke(toSelect.Row.CommitId);
            }
        }
        finally
        {
            if (generation == _logGeneration)
            {
                IsLoadingHistory = false;
            }
        }

        // A watcher refresh must not clear and rebuild the commit diff when the
        // selected commit is unchanged; that caused a visible flash.
        if (compareToShow is not null)
        {
            await ShowCompareAsync(compareToShow).ConfigureAwait(true);
        }
        else if (!sameSelection)
        {
            await SelectCommitAsync(toSelect).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Appends the next page of history instead of reloading everything from the
    /// first commit; lanes continue from the last loaded row.
    /// </summary>
    private async Task LoadMoreLogAsync()
    {
        if (LogRows.Count == 0 || IsShowingCachedHistory)
        {
            await LoadLogCoreAsync().ConfigureAwait(true);
            return;
        }

        var generation = _logGeneration;
        var skip = LogRows.Count;
        CommitGraphBuilder? continueFrom = _historyGraph;
        IsLoadingHistory = true;
        try
        {
            LogPage page = await Handler.GetLogPageAsync(Path, BuildLogQuery(skip, HistoryPageSize)).ConfigureAwait(true);
            // A full reload that started meanwhile owns the list.
            if (generation != _logGeneration || LogRows.Count != skip)
            {
                return;
            }

            if (page.Error is not null)
            {
                RecordError(Strings.Repo_ErrorReadHistory, page.Error);
                return;
            }
            if (page.Rows.Count == 0)
            {
                StatusMessage = Strings.Repo_StatusNoMoreHistory;
                return;
            }

            var hidesParents = HistoryHidesParents;
            (List<CommitRowViewModel>? rows, var width, CommitGraphBuilder graph) =
                await Task.Run(() => BuildHistoryRows(page.Rows, continueFrom, hidesParents)).ConfigureAwait(true);
            if (generation != _logGeneration || LogRows.Count != skip)
            {
                return;
            }

            _historyGraph = graph;

            var currentWidth = LogRows[0].GraphWidth;
            if (width > currentWidth)
            {
                foreach (CommitRowViewModel existing in LogRows)
                {
                    existing.GraphWidth = width;
                }
            }
            foreach (CommitRowViewModel? row in rows)
            {
                row.GraphWidth = Math.Max(width, currentWidth);
                LogRows.Add(row);
            }

            // Later full reloads (watcher, refresh) keep showing this many commits.
            _logLimit = LogRows.Count;
            OnPropertyChanged(nameof(LogCountText));
        }
        finally
        {
            if (generation == _logGeneration)
            {
                IsLoadingHistory = false;
            }
        }
    }

    private async Task SelectCommitAsync(CommitRowViewModel? row)
    {
        var generation = ++_commitSelectionGeneration;
        // A newer click makes the previous lookup pointless; stop its git process.
        _commitSelectionCts?.Cancel();
        _requestedCommitSha = row?.Row.CommitId;
        _requestedComparePair = null;
        _displayedComparePair = null;
        SetComparePair(null);
        if (row is null)
        {
            _displayedCommitSha = null;
            CommitInfo = null;
            CommitFiles.Clear();
            CommitDiff.Clear();
            SetLoadingCommitDetails(false);
            return;
        }

        CancellationTokenSource cts = _commitSelectionCts = new CancellationTokenSource();
        SetLoadingCommitDetails(true);
        try
        {
            // One git process returns details, refs and changed files.
            CommitResult result = await Handler.GetCommitAsync(Path, row.Row.CommitId, cts.Token).ConfigureAwait(true);
            // A newer selection owns the panes; drop this result (including errors).
            if (generation != _commitSelectionGeneration)
            {
                return;
            }

            if (result.Error is not null || result.Commit is null)
            {
                if (result.Error is not null)
                {
                    RecordError(Strings.Repo_ErrorReadCommit, result.Error);
                }
                // Allow clicking the row again to retry.
                _requestedCommitSha = null;
                _displayedCommitSha = null;
                CommitInfo = null;
                CommitFiles.Clear();
                CommitDiff.Clear();
                return;
            }

            _displayedCommitSha = row.Row.CommitId;
            CommitInfo = new CommitInfoViewModel(result.Commit.Details);
            CommitFiles.Clear();
            foreach (ChangedFile change in result.Commit.Files)
            {
                CommitFiles.Add(change);
            }

            _selectedCommitFile = CommitFiles.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedCommitFile));
            SetLoadingCommitDetails(false);
            await LoadCommitDiffAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            if (generation == _commitSelectionGeneration)
            {
                SetLoadingCommitDetails(false);
            }
        }
    }

    private async Task LoadCommitDiffAsync()
    {
        ChangedFile? file = SelectedCommitFile;
        CommitPair? pair = _comparePair;
        CommitRowViewModel? commit = SelectedCommit;
        if (file is null || (pair is null && commit is null))
        {
            CommitDiff.Clear(Strings.Repo_DiffSelectCommitFile);
            return;
        }

        var generation = ++_commitDiffGeneration;
        _commitDiffCts?.Cancel();
        CancellationTokenSource cts = _commitDiffCts = new CancellationTokenSource();
        var revision = pair?.Target.Sha ?? commit!.Row.CommitId;
        var baseSha = pair?.Base.Sha;
        var request = new DiffSpec
        {
            Kind = pair is not null ? DiffRequestKind.CommitRange : DiffRequestKind.Commit,
            BaseRevision = baseSha,
            Revision = revision,
            // Parents are known from history, so the diff needs no rev-list lookup.
            FirstParent = pair is not null ? null : (commit!.Row.ParentIds.Length > 0 ? commit.Row.ParentIds[0] : string.Empty),
            Files = BuildDiffPaths(file),
            Context = DiffContext,
            IgnoreWhitespace = IgnoreWhitespace
        };

        SetLoadingCommitDiff(true);
        try
        {
            var raw = await Handler.GetDiffAsync(Path, request, cts.Token).ConfigureAwait(true);
            // Drop the result if a newer diff was requested or the selection moved to
            // another commit meanwhile (rows are re-created on refresh, so compare SHAs).
            if (generation != _commitDiffGeneration
                || !Equals(_comparePair, pair)
                || (pair is null && !string.Equals(SelectedCommit?.Row.CommitId, commit!.Row.CommitId, StringComparison.Ordinal)))
            {
                return;
            }

            if (ReportHandlerError(Strings.Repo_ErrorLoadCommitDiff))
            {
                return;
            }

            var env = new DiffSetup
            {
                Subject = pair is not null ? DiffSubject.Range : DiffSubject.SingleCommit,
                Scope = DiffScope.WorkTreeVsHead
            };
            List<FileDiff> files = await Task.Run(() => ParseDiff(raw, env)).ConfigureAwait(true);
            if (generation != _commitDiffGeneration)
            {
                return;
            }

            if (files.Count == 0 && file.Status != PathState.Removed)
            {
                var abs = file.FullPath;
                if (File.Exists(abs))
                {
                    FileDiff content = SyntheticDiff.CreateContentFile(file.RelativePath, abs, env);
                    content.State = file.Status;
                    files.Add(content);
                }
            }

            var title = pair is not null
                ? $"{pair.Base.ShortSha}..{pair.Target.ShortSha}  {file.RelativePath}"
                : $"{commit!.Row.ShortId}  {file.RelativePath}";
            var oldRevision = pair is not null
                ? pair.Base.Sha
                : (commit!.Row.ParentIds.Length > 0 ? commit.Row.ParentIds[0] : null);
            CommitDiff.SetFiles(files, title, canStage: false, canUnstage: false, canDiscard: false,
                imageSides: f => DiffImages.SidesFor(f,
                    oldRevision is null ? null : p => ImageOrigin.Object($"{oldRevision}:{p}"),
                    p => ImageOrigin.Object($"{revision}:{p}")));
        }
        finally
        {
            if (generation == _commitDiffGeneration)
            {
                SetLoadingCommitDiff(false);
            }
        }
    }

    /// <summary>
    /// Shows the changed files and diff between two commits in the right-hand
    /// panes. <see cref="CommitPair.Base"/> is normally the older commit; swapping
    /// reverses the pair.
    /// </summary>
    private async Task ShowCompareAsync(CommitPair pair, bool keepSelectedFile = false)
    {
        var generation = ++_commitSelectionGeneration;
        _commitSelectionCts?.Cancel();
        CancellationTokenSource cts = _commitSelectionCts = new CancellationTokenSource();
        var keepPath = keepSelectedFile ? SelectedCommitFile?.RelativePath : null;

        // The panes no longer show one commit's details.
        _requestedCommitSha = null;
        _displayedCommitSha = null;
        _requestedComparePair = pair;
        SetComparePair(pair);
        SetLoadingCommitDetails(true);
        try
        {
            Handler.ClearLastError();
            IReadOnlyList<ChangedFile> changes =
                await Handler.GetRangeChangesAsync(Path, pair.Base.Sha, pair.Target.Sha, cts.Token).ConfigureAwait(true);
            if (generation != _commitSelectionGeneration)
            {
                return;
            }

            if (ReportHandlerError(Strings.Repo_ErrorCompareCommits))
            {
                _requestedComparePair = null;
                _displayedComparePair = null;
                CommitFiles.Clear();
                CommitDiff.Clear(Strings.Repo_DiffCouldNotCompareCommits);
                return;
            }

            CommitFiles.Clear();
            foreach (ChangedFile change in changes)
            {
                CommitFiles.Add(change);
            }

            _selectedCommitFile = keepPath is null
                ? null
                : CommitFiles.FirstOrDefault(f => string.Equals(f.RelativePath, keepPath, StringComparison.Ordinal));
            _selectedCommitFile ??= CommitFiles.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedCommitFile));
            _displayedComparePair = pair;
            if (_selectedCommitFile is not null)
            {
                await LoadCommitDiffAsync().ConfigureAwait(true);
            }
            else
            {
                CommitDiff.Clear(Strings.Repo_DiffNoChangesInRange);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this comparison.
        }
        catch (Exception ex)
        {
            _requestedComparePair = null;
            StatusMessage = ex.Message;
        }
        finally
        {
            if (generation == _commitSelectionGeneration)
            {
                SetLoadingCommitDetails(false);
            }
        }
    }

    private async Task SwapCompareAsync()
    {
        if (_comparePair is not { } pair)
        {
            return;
        }

        await ShowCompareAsync(new CommitPair(pair.Target, pair.Base), keepSelectedFile: true).ConfigureAwait(true);
    }

    private async Task CompareWithBaseAsync()
    {
        if (_selectionTask is not null)
        {
            await _selectionTask.ConfigureAwait(true);
        }

        if (_compareBase is null || SelectedCommit is null)
        {
            StatusMessage = Strings.Repo_StatusSetCompareBaseFirst;
            return;
        }

        IsBusy = true;
        try
        {
            await ShowCompareAsync(new CommitPair(_compareBase, CommitSide.FromRow(SelectedCommit))).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadStatusCoreAsync()
    {
        var generation = ++_statusGeneration;
        var keepStaged = SelectedStagedFile?.RelativePath;
        var keepUnstaged = SelectedUnstagedFile?.RelativePath;
        var keepStagedIndex = SelectedStagedFile is null ? -1 : StagedFiles.IndexOf(SelectedStagedFile);
        var keepUnstagedIndex = SelectedUnstagedFile is null ? -1 : UnstagedFiles.IndexOf(SelectedUnstagedFile);

        IsLoadingStatus = true;
        StatusSnapshot snapshot;
        try
        {
            // One `git status` yields files, branch and HEAD (previously status ran
            // twice, plus symbolic-ref and rev-parse).
            snapshot = await Handler.GetStatusSnapshotAsync(Path).ConfigureAwait(true);
        }
        finally
        {
            if (generation == _statusGeneration)
            {
                IsLoadingStatus = false;
            }
        }

        // A newer status load (watcher, staging) supersedes this one.
        if (generation != _statusGeneration)
        {
            return;
        }

        if (snapshot.Error is not null)
        {
            RecordError(Strings.Repo_ErrorReadWorkingCopy, snapshot.Error);
            StagedFiles.Clear();
            UnstagedFiles.Clear();
            OnPropertyChanged(nameof(StagedHeader));
            OnPropertyChanged(nameof(UnstagedHeader));
            OnPropertyChanged(nameof(CanCommit));
            return;
        }

        SyncFileList(StagedFiles, snapshot.Staged);
        SyncFileList(UnstagedFiles, snapshot.Unstaged);

        RepoSummary summary = snapshot.Summary;
        CurrentBranch = snapshot.Branch;
        CurrentRevision = snapshot.Revision;
        IsMergeInProgress = summary.MergeInProgress;
        IsRebaseInProgress = summary.RebaseInProgress;
        IsCherryPickInProgress = summary.CherryPickInProgress;
        IsRevertInProgress = summary.RevertInProgress;
        if (summary.IsClean)
        {
            SummaryText = string.Format(Strings.Repo_SummaryClean, CurrentBranch);
        }
        else
        {
            var parts = new List<string>();
            void Add(int count, string format)
            {
                if (count > 0)
                {
                    parts.Add(string.Format(format, count));
                }
            }

            Add(summary.Modified, Strings.Repo_SummaryModified);
            Add(summary.Added, Strings.Repo_SummaryAdded);
            Add(summary.Removed, Strings.Repo_SummaryRemoved);
            Add(summary.Renamed, Strings.Repo_SummaryRenamed);
            Add(summary.Conflicted, Strings.Repo_SummaryConflicted);
            Add(summary.Untracked, Strings.Repo_SummaryUntracked);
            SummaryText = parts.Count > 0
                ? string.Format(Strings.Repo_SummaryCountsOnBranch, string.Join(", ", parts), CurrentBranch)
                : string.Format(Strings.Repo_SummaryChangesOnBranch, CurrentBranch);
        }

        // Keep the selection in the list the user was working in. If the selected
        // file left that list (e.g. it was just staged), select its neighbour there
        // rather than jumping to the top of the other list.
        static ChangedFile? Find(IList<ChangedFile> list, string? path)
        {
            return path is null ? null : list.FirstOrDefault(f => string.Equals(f.RelativePath, path, StringComparison.Ordinal));
        }

        static ChangedFile? Neighbour(IList<ChangedFile> list, int index)
        {
            return index < 0 || list.Count == 0 ? null : list[Math.Min(index, list.Count - 1)];
        }

        ChangedFile? newStaged = null;
        ChangedFile? newUnstaged = null;
        if (keepUnstaged is not null)
        {
            newUnstaged = Find(UnstagedFiles, keepUnstaged) ?? Neighbour(UnstagedFiles, keepUnstagedIndex);
        }
        else if (keepStaged is not null)
        {
            newStaged = Find(StagedFiles, keepStaged) ?? Neighbour(StagedFiles, keepStagedIndex);
        }

        if (newStaged is null && newUnstaged is null)
        {
            // Nothing was selected, or the user's list is now empty (everything was
            // staged or unstaged): fall back to whichever list has files.
            newStaged = StagedFiles.FirstOrDefault();
            newUnstaged = newStaged is null ? UnstagedFiles.FirstOrDefault() : null;
        }

        // Raise only on change: re-announcing the same item makes the list re-select it.
        if (!ReferenceEquals(_selectedStagedFile, newStaged))
        {
            _selectedStagedFile = newStaged;
            OnPropertyChanged(nameof(SelectedStagedFile));
        }
        if (!ReferenceEquals(_selectedUnstagedFile, newUnstaged))
        {
            _selectedUnstagedFile = newUnstaged;
            OnPropertyChanged(nameof(SelectedUnstagedFile));
        }
        OnPropertyChanged(nameof(StagedHeader));
        OnPropertyChanged(nameof(UnstagedHeader));
        OnPropertyChanged(nameof(HasUnresolvedConflicts));
        OnPropertyChanged(nameof(CanCommit));

        await ReloadCurrentDiffAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Brings <paramref name="target"/> in line with <paramref name="fresh"/> while
    /// reusing existing records for unchanged paths. Clearing and re-adding made
    /// the list drop its selection and scroll to the top on every watcher refresh.
    /// </summary>
    private static void SyncFileList(ObservableCollection<ChangedFile> target, IReadOnlyList<ChangedFile> fresh)
    {
        var freshPaths = new HashSet<string>(fresh.Select(f => f.RelativePath), StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!freshPaths.Contains(target[i].RelativePath))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < fresh.Count; i++)
        {
            ChangedFile incoming = fresh[i];
            var existingIndex = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (string.Equals(target[j].RelativePath, incoming.RelativePath, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                target.Insert(i, incoming);
                continue;
            }

            if (existingIndex != i)
            {
                target.Move(existingIndex, i);
            }

            target[i].CopyStatusFrom(incoming);
        }
    }

    private async Task ReloadAllDiffsAsync()
    {
        await LoadCommitDiffAsync().ConfigureAwait(true);
        await ReloadCurrentDiffAsync().ConfigureAwait(true);
    }

    private async Task ReloadCurrentDiffAsync()
    {
        if (SelectedStagedFile is not null)
        {
            await LoadWorkingDiffAsync(SelectedStagedFile, staged: true).ConfigureAwait(true);
        }
        else if (SelectedUnstagedFile is not null)
        {
            await LoadWorkingDiffAsync(SelectedUnstagedFile, staged: false).ConfigureAwait(true);
        }
        else
        {
            _workingDiffSignature = null;
            WorkingDiff.Clear(Strings.Repo_DiffSelectFile);
        }
    }

    private static bool IsUntrackedFolder(ChangedFile file)
        => file.Status == PathState.Untracked && (file.RelativePath.EndsWith('/') || Directory.Exists(file.FullPath));

    private async Task LoadWorkingDiffAsync(ChangedFile file, bool staged)
    {
        var generation = ++_workingDiffGeneration;
        _workingDiffCts?.Cancel();
        CancellationTokenSource cts = _workingDiffCts = new CancellationTokenSource();
        var env = new DiffSetup
        {
            Subject = DiffSubject.PendingFiles,
            Scope = staged ? DiffScope.IndexVsHead : DiffScope.WorkTreeVsIndex
        };

        IsLoadingWorkingDiff = true;
        try
        {
            if (!staged && IsUntrackedFolder(file))
            {
                // With "show files inside untracked folders" off, git reports a new
                // folder as one entry; summarise it rather than diffing a directory.
                var count = await Task.Run(() => CountFiles(file.FullPath, 10_000)).ConfigureAwait(true);
                if (generation != _workingDiffGeneration)
                {
                    return;
                }

                _workingDiffSignature = null;
                WorkingDiff.Clear(string.Format(Strings.Repo_DiffUntrackedFolder, count >= 10_000 ? "10,000+" : count.ToString("N0")));
                return;
            }

            List<FileDiff> files;
            string? signature = null;
            if (file.Status == PathState.Unchanged)
            {
                files = [SyntheticDiff.CreateContentFile(file.RelativePath, file.FullPath, env)];
            }
            else
            {
                var request = new DiffSpec
                {
                    Kind = staged ? DiffRequestKind.Staged : DiffRequestKind.WorkingCopy,
                    Files = BuildDiffPaths(file),
                    Context = DiffContext,
                    IgnoreWhitespace = IgnoreWhitespace
                };
                var raw = await Handler.GetDiffAsync(Path, request, cts.Token).ConfigureAwait(true);
                if (generation != _workingDiffGeneration)
                {
                    return;
                }

                if (ReportHandlerError(Strings.Repo_ErrorLoadDiff))
                {
                    return;
                }

                signature = $"{staged}|{file.RelativePath}|{file.Status}|{DiffContext}|{IgnoreWhitespace}\n{raw}";
                if (signature == _workingDiffSignature && WorkingDiff.HasContent)
                {
                    return;
                }

                files = await Task.Run(() => ParseDiff(raw, env)).ConfigureAwait(true);
            }

            if (generation != _workingDiffGeneration)
            {
                return;
            }

            if (files.Count == 0 && file.Status != PathState.Removed && file.Status != PathState.Untracked && File.Exists(file.FullPath))
            {
                FileDiff content = SyntheticDiff.CreateContentFile(file.RelativePath, file.FullPath, env);
                content.State = file.Status;
                files.Add(content);
            }

            var conflicted = file.Status == PathState.Conflicted;
            _workingDiffSignature = signature;
            WorkingDiff.SetFiles(files, string.Format(staged ? Strings.Repo_DiffTitleStaged : Strings.Repo_DiffTitleWorkingCopy, file.RelativePath),
                canStage: !staged && !conflicted,
                canUnstage: staged && !conflicted,
                canDiscard: !staged && !conflicted,
                imageSides: staged
                    ? f => DiffImages.SidesFor(f, p => ImageOrigin.Object($"HEAD:{p}"), p => ImageOrigin.Object($":{p}"))
                    : f => DiffImages.SidesFor(f, p => ImageOrigin.Object($":{p}"), p => ImageOrigin.File(FullPathOf(p))));
        }
        finally
        {
            if (generation == _workingDiffGeneration)
            {
                IsLoadingWorkingDiff = false;
            }
        }
    }

    private static int CountFiles(string directory, int cap)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(cap).Count();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static List<FileDiff> ParseDiff(string raw, DiffSetup env)
    {
        return UnifiedDiffParser.Parse(raw, new DiffParseOptions
        {
            MaxFiles = 50,
            MaxLines = 20000,
            MaxLinesPerHunk = 4000,
            Setup = env,
            ConvertTabsToSpaces = true,
            TabSize = 4
        });
    }

    private string FullPathOf(string relativePath)
        => System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private async Task<ImageBytes> ReadImageAsync(ImageOrigin origin, CancellationToken cancellationToken)
    {
        if (origin.FilePath is { } file)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists)
                    {
                        return new ImageBytes(null, Strings.Repo_ImageFileNoLongerExists);
                    }

                    return info.Length > DiffImages.MaxBytes
                        ? new ImageBytes(null, string.Format(Strings.Repo_ImageTooLargeToPreview, DiffImages.FormatSize(info.Length)))
                        : new ImageBytes(File.ReadAllBytes(file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new ImageBytes(null, ex.Message);
                }
            }, cancellationToken).ConfigureAwait(true);
        }

        BinaryProcessResult result = await Handler.GetFileBytesAsync(Path, origin.ObjectSpec!, DiffImages.MaxBytes, cancellationToken)
            .ConfigureAwait(true);
        if (result.TooLarge)
        {
            return new ImageBytes(null, string.Format(Strings.Repo_ImageTooLargeToPreviewOver, DiffImages.FormatSize(DiffImages.MaxBytes)));
        }

        return result.Success ? new ImageBytes(result.Output) : new ImageBytes(null, Strings.Repo_ImageCouldNotRead);
    }

    private void WireDiffActions()
    {
        CommitDiff.ReadImageAction = ReadImageAsync;
        WorkingDiff.ReadImageAction = ReadImageAsync;
        // CopyText is assigned after this repository is constructed, so read it when copying.
        CommitDiff.CopyText = text => CopyText?.Invoke(text);
        WorkingDiff.CopyText = text => CopyText?.Invoke(text);
        WorkingDiff.ConfirmAction = Dialogs.ConfirmAsync;
        WorkingDiff.DiscardConfirmedLinesAction = hunk => ApplyHunkAsync(hunk, stage: false, unstage: false, discard: true,
            selectedOnly: true, confirmed: true);
        WorkingDiff.StageFileAction = file => StageFilesAsync([FindRecord(file.FileName, staged: false) ?? MakeRecord(file.FileName, staged: false)]);
        WorkingDiff.UnstageFileAction = file => UnstageFilesAsync([FindRecord(file.FileName, staged: true) ?? MakeRecord(file.FileName, staged: true)]);
        WorkingDiff.DiscardFileAction = file => DiscardFilesAsync([FindRecord(file.FileName, staged: false) ?? MakeRecord(file.FileName, staged: false)], confirm: true);
        WorkingDiff.StageHunkAction = hunk => ApplyHunkAsync(hunk, stage: true, unstage: false, discard: false);
        WorkingDiff.UnstageHunkAction = hunk => ApplyHunkAsync(hunk, stage: false, unstage: true, discard: false);
        WorkingDiff.DiscardHunkAction = hunk => ApplyHunkAsync(hunk, stage: false, unstage: false, discard: true);
        WorkingDiff.StageSelectedLinesAction = hunk => ApplyHunkAsync(hunk, stage: true, unstage: false, discard: false, selectedOnly: true);
        WorkingDiff.UnstageSelectedLinesAction = hunk => ApplyHunkAsync(hunk, stage: false, unstage: true, discard: false, selectedOnly: true);
        WorkingDiff.DiscardSelectedLinesAction = hunk => ApplyHunkAsync(hunk, stage: false, unstage: false, discard: true, selectedOnly: true);
    }

    private ChangedFile? FindRecord(string filePath, bool staged)
    {
        ObservableCollection<ChangedFile> source = staged ? StagedFiles : UnstagedFiles;
        ChangedFile? exact = source.FirstOrDefault(f => string.Equals(f.RelativePath, filePath, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        // Only fall back to a case-insensitive match when it is unambiguous;
        // otherwise a case-sensitive filesystem could discard the wrong file.
        var loose = source.Where(f => string.Equals(f.RelativePath, filePath, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return loose.Count == 1 ? loose[0] : null;
    }

    private static string[] BuildDiffPaths(ChangedFile file)
    {
        if (!string.IsNullOrEmpty(file.OldPath)
            && !string.Equals(file.OldPath, file.RelativePath, StringComparison.Ordinal))
        {
            return [file.OldPath!, file.RelativePath];
        }

        return [file.RelativePath];
    }

    private ChangedFile MakeRecord(string filePath, bool staged) => new()
    {
        RelativePath = filePath,
        FileName = System.IO.Path.GetFileName(filePath),
        FullPath = System.IO.Path.Combine(Path, filePath.Replace('/', System.IO.Path.DirectorySeparatorChar)),
        Area = staged ? ChangeArea.Index : ChangeArea.WorkTree,
        Status = PathState.Edited,
        Included = true
    };

    private static List<ChangedFile> Affected(IEnumerable<ChangedFile> source, ChangedFile? selected)
    {
        var list = source.Where(f => f.Included).ToList();
        if (list.Count == 0 && selected is not null)
        {
            list.Add(selected);
        }

        return list;
    }

    private async Task StageFilesAsync(IEnumerable<ChangedFile> files)
    {
        var list = files.Where(f => f is not null).ToList();
        if (list.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ProcessResult result = await Handler.StageAsync(Path, list).ConfigureAwait(true);
            if (!result.Success)
            {
                await Dialogs.ShowMessageAsync(Strings.Repo_StageFailedTitle, result.Combined).ConfigureAwait(true);
            }
            else
            {
                StatusMessage = string.Format(Strings.Repo_StatusStagedFiles, list.Count);
            }

            await LoadStatusCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UnstageFilesAsync(IEnumerable<ChangedFile> files)
    {
        var list = files.Where(f => f is not null).ToList();
        if (list.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ProcessResult result = await Handler.UnstageAsync(Path, list).ConfigureAwait(true);
            if (!result.Success)
            {
                await Dialogs.ShowMessageAsync(Strings.Repo_UnstageFailedTitle, result.Combined).ConfigureAwait(true);
            }
            else
            {
                StatusMessage = string.Format(Strings.Repo_StatusUnstagedFiles, list.Count);
            }

            await LoadStatusCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string DescribeDiscard(IReadOnlyCollection<ChangedFile> files)
    {
        // An untracked folder entry deletes everything inside it; say so.
        var folders = files.Count(IsUntrackedFolder);
        return folders == 0
            ? string.Format(Strings.Repo_DiscardFilesConfirm, files.Count)
            : string.Format(Strings.Repo_DiscardItemsWithFoldersConfirm, files.Count, folders);
    }

    private async Task DiscardFilesAsync(IEnumerable<ChangedFile> files, bool confirm)
    {
        var all = files.Where(f => f is not null).ToList();
        if (all.Count == 0)
        {
            return;
        }

        var conflicts = all.Where(f => f.Status == PathState.Conflicted).ToList();
        var list = all.Where(f => f.Status != PathState.Conflicted).ToList();
        if (list.Count == 0)
        {
            StatusMessage = Strings.Repo_StatusConflictedCannotDiscard;
            return;
        }

        if (confirm && !await Dialogs.ConfirmAsync(Strings.Repo_DiscardChangesTitle,
                DescribeDiscard(list)).ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            ProcessResult result = await Handler.DiscardAsync(Path, list).ConfigureAwait(true);
            if (!result.Success)
            {
                await Dialogs.ShowMessageAsync(Strings.Repo_DiscardFailedTitle, result.Combined).ConfigureAwait(true);
            }
            else
            {
                StatusMessage = conflicts.Count > 0
                    ? string.Format(Strings.Repo_StatusDiscardedFilesSkippedConflicts, list.Count, conflicts.Count)
                    : string.Format(Strings.Repo_StatusDiscardedFiles, list.Count);
            }

            await LoadStatusCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <param name="confirmed">The caller already asked before discarding (one question for several hunks).</param>
    private async Task ApplyHunkAsync(Hunk hunk, bool stage, bool unstage, bool discard, bool selectedOnly = false,
        bool confirmed = false)
    {
        // A new/untracked/deleted file has no counterpart tree entry to patch, so
        // operate on the whole file instead of trying to apply a hunk patch.
        var isNewFile = hunk.Owner.State is PathState.New or PathState.Untracked;
        if (selectedOnly && isNewFile)
        {
            await ApplyNewFileSelectionAsync(hunk, stage, unstage).ConfigureAwait(true);
            return;
        }

        var wholeFile = isNewFile || hunk.Owner.State is PathState.Removed
                        || (hunk.Owner.State is PathState.Moved or PathState.Duplicated
                            && !string.IsNullOrEmpty(hunk.Owner.OldPath));
        if (wholeFile)
        {
            var useStagedList = unstage;
            ChangedFile record = FindRecord(hunk.Owner.FileName, staged: useStagedList)
                         ?? MakeRecord(hunk.Owner.FileName, staged: useStagedList);

            if (discard)
            {
                await DiscardFilesAsync([record], confirm: true).ConfigureAwait(true);
            }
            else if (stage)
            {
                await StageFilesAsync([record]).ConfigureAwait(true);
            }
            else
            {
                await UnstageFilesAsync([record]).ConfigureAwait(true);
            }

            return;
        }

        if (discard && !confirmed
            && !await Dialogs.ConfirmAsync(Strings.Repo_DiscardHunkTitle, Strings.Repo_DiscardHunkConfirm).ConfigureAwait(true))
        {
            return;
        }

        List<int> selection;
        if (selectedOnly)
        {
            selection = hunk.LineItems
                .Select((line, index) => (line, index))
                .Where(x => x.line.Selected)
                .Select(x => x.index)
                .ToList();
            if (selection.Count == 0)
            {
                StatusMessage = Strings.Repo_StatusSelectChangedLinesFirst;
                return;
            }
        }
        else
        {
            selection = [.. Enumerable.Range(0, hunk.LineItems.Count)];
        }

        // One patch for the whole selection: applying several patches in turn would
        // let the first shift the line numbers the later ones rely on.
        var patch = PatchBuilder.CreateSelectionPatch(hunk, selection, isStaging: stage);
        if (patch is null)
        {
            StatusMessage = Strings.Repo_StatusNoStageableChanges;
            return;
        }

        IsBusy = true;
        try
        {
            ProcessResult result = await Handler.ApplyPatchAsync(Path, patch,
                cached: stage || unstage, reverse: unstage || discard).ConfigureAwait(true);
            if (!result.Success)
            {
                await Dialogs.ShowMessageAsync(Strings.Repo_PatchFailedTitle, result.Combined).ConfigureAwait(true);
            }

            StatusMessage = !result.Success
                ? Strings.Repo_StatusPatchFailed
                : discard ? Strings.Repo_StatusHunkDiscarded : stage ? Strings.Repo_StatusHunkStaged : Strings.Repo_StatusHunkUnstaged;
            await LoadStatusCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Stages or unstages only the selected lines of a new file (its diff is one
    /// all-added hunk), matching line staging for modified files.
    /// </summary>
    private async Task ApplyNewFileSelectionAsync(Hunk hunk, bool stage, bool unstage)
    {
        if (!stage && !unstage)
        {
            // Rewriting an untracked file from the decoded diff text could change its
            // encoding or byte-order mark, so only whole-file discard is offered.
            StatusMessage = Strings.Repo_StatusCannotDiscardNewFileLines;
            return;
        }

        var patch = PatchBuilder.CreateNewFileSelectionPatch(hunk, forUnstage: unstage);
        if (patch is null)
        {
            StatusMessage = Strings.Repo_StatusSelectAddedLinesFirst;
            return;
        }

        IsBusy = true;
        try
        {
            ProcessResult result = await Handler.ApplyPatchAsync(Path, patch, cached: true, reverse: unstage,
                allowFuzzy: false).ConfigureAwait(true);
            if (!result.Success)
            {
                await Dialogs.ShowMessageAsync(Strings.Repo_PatchFailedTitle, result.Combined).ConfigureAwait(true);
            }

            StatusMessage = !result.Success ? Strings.Repo_StatusPatchFailed : stage ? Strings.Repo_StatusSelectedLinesStaged : Strings.Repo_StatusSelectedLinesUnstaged;
            await LoadStatusCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CommitAsync()
    {
        if (!CanCommit)
        {
            StatusMessage = HasUnresolvedConflicts
                ? Strings.Repo_StatusResolveConflictsBeforeCommit
                : Strings.Repo_StatusEnterMessageAndStage;
            return;
        }

        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            StatusMessage = Strings.Repo_StatusEnterCommitMessage;
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = Strings.Repo_StatusCommitting;
            ProcessResult result = await Handler.CommitAsync(Path, CommitMessage, Amend, SignOff, GpgSign).ConfigureAwait(true);
            if (!result.Success)
            {
                await Dialogs.ShowMessageAsync(Strings.Repo_CommitFailedTitle, result.Combined).ConfigureAwait(true);
                StatusMessage = Strings.Repo_StatusCommitFailed;
            }
            else
            {
                CommitMessage = string.Empty;
                // The draft saved when amend was ticked belongs to this commit; do not restore it.
                _amendMessage = null;
                _messageBeforeAmend = null;
                Amend = false;
                StatusMessage = Strings.Repo_StatusCommitCreated;
                await ReloadAllCoreAsync().ConfigureAwait(true);
                if (PushAfterCommit)
                {
                    if (Remotes.Count == 0)
                    {
                        StatusMessage = Strings.Repo_StatusCommitCreatedNoRemotes;
                    }
                    else
                    {
                        await PushAsync().ConfigureAwait(true);
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Recovery options offered when a push is rejected.
    internal static string PullRebaseThenPush => Strings.Repo_PushRecoveryPullRebase;
    internal static string PullMergeThenPush => Strings.Repo_PushRecoveryPullMerge;
    internal static string ForcePushWithLease => Strings.Repo_PushRecoveryForceWithLease;
    internal static string FetchOnly => Strings.Repo_PushRecoveryFetchOnly;
    internal static string FetchToReview => Strings.Repo_PushRecoveryFetchToReview;

    /// <summary>What to push: Branch is a local branch; DetachedTarget pushes HEAD to that remote branch.</summary>
    private sealed record PushRequest(string? Remote, string? Branch, string? DetachedTarget, bool SetUpstream, bool Force);

    private async Task PushToRemoteAsync()
    {
        IReadOnlyDictionary<string, string> remotes = await Handler.GetRemotesAsync(Path).ConfigureAwait(true);
        if (remotes.Count == 0)
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_OpPush, Strings.Repo_NoRemotesConfigured).ConfigureAwait(true);
            return;
        }

        var remote = await Dialogs.PromptAsync(Strings.Repo_PushToRemoteTitle, Strings.Repo_RemoteLabel, remotes.ContainsKey("origin") ? "origin" : remotes.Keys.First()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(remote))
        {
            return;
        }

        var branch = await Dialogs.PromptAsync(Strings.Repo_PushToRemoteTitle, Strings.Repo_BranchLabel, CurrentBranch).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(branch))
        {
            return;
        }

        await PushWithRecoveryAsync(Strings.Repo_OpPush, new PushRequest(remote, branch, null, SetUpstream: true, Force: false),
            canPull: string.Equals(branch, CurrentBranch, StringComparison.Ordinal)).ConfigureAwait(true);
    }

    private async Task FetchAllAsync()
    {
        IReadOnlyDictionary<string, string> remotes = await Handler.GetRemotesAsync(Path).ConfigureAwait(true);
        if (remotes.Count == 0)
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_OpFetch, Strings.Repo_NoRemotesConfigured).ConfigureAwait(true);
            return;
        }

        IsBusy = true;
        try
        {
            var failures = new List<string>();
            foreach (var remote in remotes.Keys)
            {
                ProcessResult result = await Handler.FetchAsync(Path, remote).ConfigureAwait(true);
                if (!result.Success)
                {
                    failures.Add($"{remote}: {result.Error.Trim()}");
                }
            }

            if (failures.Count > 0)
            {
                await Dialogs.ShowMessageAsync(Strings.Repo_FetchAllRemotesTitle, string.Join("\n\n", failures)).ConfigureAwait(true);
            }
            else
            {
                StatusMessage = string.Format(Strings.Repo_StatusFetchedRemotes, remotes.Count);
            }

            await ReloadAllCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ForcePushAsync()
    {
        var branch = CurrentBranch == RepoSummary.DetachedHeadLabel ? null : CurrentBranch;
        if (branch is null)
        {
            (string Remote, string Target)? prompt = await PromptDetachedTargetAsync(Strings.Repo_OpForcePush).ConfigureAwait(true);
            if (prompt is null)
            {
                return;
            }

            await PushWithRecoveryAsync(Strings.Repo_OpForcePush,
                new PushRequest(prompt.Value.Remote, null, prompt.Value.Target, SetUpstream: false, Force: true),
                canPull: false).ConfigureAwait(true);
            return;
        }

        await PushWithRecoveryAsync(Strings.Repo_OpForcePush, new PushRequest(null, branch, null, SetUpstream: true, Force: true),
            canPull: true).ConfigureAwait(true);
    }

    private async Task<(string Remote, string Target)?> PromptDetachedTargetAsync(string title)
    {
        IReadOnlyDictionary<string, string> remotes = await Handler.GetRemotesAsync(Path).ConfigureAwait(true);
        if (remotes.Count == 0)
        {
            await Dialogs.ShowMessageAsync(title, Strings.Repo_NoRemotesConfigured).ConfigureAwait(true);
            return null;
        }

        var remote = await Dialogs.PromptAsync(title, Strings.Repo_RemoteLabel,
            remotes.ContainsKey("origin") ? "origin" : remotes.Keys.First()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(remote))
        {
            return null;
        }

        var target = await Dialogs.PromptAsync(title, Strings.Repo_RemoteBranchLabel, string.Empty).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        return (remote, target);
    }

    private async Task PushAsync()
    {
        var branch = CurrentBranch == RepoSummary.DetachedHeadLabel ? null : CurrentBranch;
        if (branch is null)
        {
            (string Remote, string Target)? prompt = await PromptDetachedTargetAsync(Strings.Repo_OpPush).ConfigureAwait(true);
            if (prompt is null)
            {
                return;
            }

            await PushWithRecoveryAsync(Strings.Repo_OpPush,
                new PushRequest(prompt.Value.Remote, null, prompt.Value.Target, SetUpstream: false, Force: false),
                canPull: false).ConfigureAwait(true);
            return;
        }

        await PushWithRecoveryAsync(Strings.Repo_OpPush, new PushRequest(null, branch, null, SetUpstream: false, Force: false),
            canPull: true).ConfigureAwait(true);
    }

    private Task<ProcessResult> RunPushAsync(PushRequest request)
        => Handler.PushAsync(Path, request.Remote, request.Branch, request.SetUpstream, request.Force, request.DetachedTarget);

    /// <summary>
    /// Pushes and, when the remote refuses, explains why and offers the actions
    /// that fit: pull then push for a branch that is behind, force with lease only
    /// when the remote's extra commits have been fetched (so the user has seen
    /// what would be discarded), fetch when a lease is stale, and an explanation
    /// for server-side and credential failures.
    /// </summary>
    /// <param name="canPull">Pull options apply only to the checked-out branch, whose upstream `git pull` integrates.</param>
    private async Task PushWithRecoveryAsync(string name, PushRequest request, bool canPull)
    {
        IsBusy = true;
        StatusMessage = $"{name}...";
        try
        {
            ProcessResult result = await RunPushAsync(request).ConfigureAwait(true);
            // Bounded: each round needs a user decision, and a pull followed by
            // another rejection (someone pushed again) simply asks again.
            for (var round = 0; round < 6; round++)
            {
                if (result.Success)
                {
                    StatusMessage = string.Format(Strings.Repo_StatusOperationCompleted, name);
                    await ReloadAllCoreAsync().ConfigureAwait(true);
                    return;
                }

                var outcome = PushOutcome.Parse(result);
                switch (outcome.Rejection)
                {
                    case PushRejection.NoUpstream when request.Branch is not null && !request.SetUpstream:
                        request = request with { SetUpstream = true };
                        result = await RunPushAsync(request).ConfigureAwait(true);
                        continue;

                    case PushRejection.FetchFirst or PushRejection.NonFastForward or PushRejection.StaleLease:
                        {
                            var choice = await ChoosePushRecoveryAsync(name, outcome, request, canPull).ConfigureAwait(true);
                            if (choice is null)
                            {
                                StatusMessage = string.Format(Strings.Repo_StatusPushRejectedBehind, name);
                                return;
                            }

                            if (choice == PullRebaseThenPush || choice == PullMergeThenPush)
                            {
                                StatusMessage = choice == PullRebaseThenPush ? Strings.Repo_StatusPullingWithRebase : Strings.Repo_StatusPulling;
                                ProcessResult pull = await Handler.PullAsync(Path, rebase: choice == PullRebaseThenPush).ConfigureAwait(true);
                                if (!pull.Success)
                                {
                                    await Dialogs.ShowMessageAsync(Strings.Repo_PullFailedTitle,
                                        string.Format(Strings.Repo_PushPullFailedMessage, pull.Combined)).ConfigureAwait(true);
                                    StatusMessage = Strings.Repo_StatusPullFailedNothingPushed;
                                    await ReloadAllCoreAsync().ConfigureAwait(true);
                                    return;
                                }

                                StatusMessage = $"{name}...";
                                request = request with { Force = false };
                                result = await RunPushAsync(request).ConfigureAwait(true);
                                continue;
                            }

                            if (choice == ForcePushWithLease)
                            {
                                StatusMessage = Strings.Repo_StatusForcePushWithLease;
                                request = request with { Force = true };
                                result = await RunPushAsync(request).ConfigureAwait(true);
                                continue;
                            }

                            // Fetch only / fetch to review: bring the remote commits in and stop.
                            ProcessResult fetch = await Handler.FetchAsync(Path, request.Remote).ConfigureAwait(true);
                            if (!fetch.Success)
                            {
                                await Dialogs.ShowMessageAsync(Strings.Repo_FetchFailedTitle, fetch.Combined).ConfigureAwait(true);
                            }

                            StatusMessage = fetch.Success
                                ? Strings.Repo_StatusFetchedReviewThenPush
                                : Strings.Repo_StatusFetchFailed;
                            await ReloadAllCoreAsync().ConfigureAwait(true);
                            return;
                        }

                    case PushRejection.RemoteRejected:
                        {
                            var reason = outcome.RejectedRef?.Reason ?? Strings.Repo_PushRejectedReasonFallback;
                            await Dialogs.ShowMessageAsync(string.Format(Strings.Repo_PushRejectedByServerTitle, name),
                                outcome.RemoteMessage is null
                                    ? string.Format(Strings.Repo_PushRemoteRejectedMessage, reason)
                                    : string.Format(Strings.Repo_PushRemoteRejectedMessageWithServer, reason, outcome.RemoteMessage))
                                .ConfigureAwait(true);
                            break;
                        }

                    case PushRejection.Authentication:
                        await Dialogs.ShowMessageAsync(string.Format(Strings.Repo_PushAuthFailedTitle, name),
                            string.Format(Strings.Repo_PushAuthFailedMessage, outcome.RawOutput))
                            .ConfigureAwait(true);
                        break;

                    default:
                        await Dialogs.ShowMessageAsync(string.Format(Strings.Repo_OperationFailedTitle, name), outcome.RawOutput).ConfigureAwait(true);
                        break;
                }

                StatusMessage = string.Format(Strings.Repo_StatusOperationFailed, name);
                await LoadStatusCoreAsync().ConfigureAwait(true);
                return;
            }

            StatusMessage = string.Format(Strings.Repo_StatusPushStoppedAfterAttempts, name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<string?> ChoosePushRecoveryAsync(string name, PushOutcome outcome, PushRequest request, bool canPull)
    {
        var destination = outcome.RejectedRef?.Destination;
        var target = destination is not null && destination.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? destination["refs/heads/".Length..]
            : request.Branch ?? request.DetachedTarget ?? Strings.Repo_PushTargetFallback;

        string message;
        var options = new List<string>();
        switch (outcome.Rejection)
        {
            case PushRejection.FetchFirst:
                // The remote's commits have not been fetched, so the user has never
                // seen them: forcing would discard unseen work. No force option here.
                message = string.Format(Strings.Repo_PushFetchFirstMessage, target);
                if (canPull)
                {
                    options.Add(PullRebaseThenPush);
                    options.Add(PullMergeThenPush);
                }
                options.Add(FetchOnly);
                break;

            case PushRejection.NonFastForward:
                {
                    var count = await CountRemoteOnlyCommitsAsync(request, target).ConfigureAwait(true);
                    var what = count is { } n
                        ? string.Format(n == 1 ? Strings.Repo_PushCommitCountOne : Strings.Repo_PushCommitCountMany, n)
                        : Strings.Repo_PushCommitsUnknown;
                    message = string.Format(canPull ? Strings.Repo_PushNonFastForwardMessage : Strings.Repo_PushNonFastForwardNoPullMessage,
                        target, what);
                    if (canPull)
                    {
                        options.Add(PullRebaseThenPush);
                        options.Add(PullMergeThenPush);
                    }
                    options.Add(ForcePushWithLease);
                    break;
                }

            default: // StaleLease
                message = string.Format(Strings.Repo_PushStaleLeaseMessage, target);
                options.Add(FetchToReview);
                break;
        }

        return await Dialogs.ChooseAsync(string.Format(Strings.Repo_PushRejectedTitle, name), message, options).ConfigureAwait(true);
    }

    /// <summary>Commits on the remote-tracking branch that the pushed branch lacks (null when unknown).</summary>
    private async Task<int?> CountRemoteOnlyCommitsAsync(PushRequest request, string target)
    {
        var local = request.Branch ?? "HEAD";
        var remoteRef = request.Remote is not null
            ? $"refs/remotes/{request.Remote}/{target}"
            : $"{local}@{{push}}";
        ProcessResult result = await Handler.RunAsync(Path, ["rev-list", "--count", $"{local}..{remoteRef}", "--"]).ConfigureAwait(true);
        return result.Success && int.TryParse(result.Output.Trim(), out var count) ? count : null;
    }

    private async Task RunOperationAsync(string name, Func<CancellationToken, Task<ProcessResult>> operation)
    {
        IsBusy = true;
        StatusMessage = $"{name}...";
        try
        {
            ProcessResult result = await operation(CancellationToken.None).ConfigureAwait(true);
            if (!result.Success)
            {
                await Dialogs.ShowMessageAsync(string.Format(Strings.Repo_OperationFailedTitle, name), result.Combined).ConfigureAwait(true);
                StatusMessage = string.Format(Strings.Repo_StatusOperationFailed, name);
                // A pull/merge/rebase can fail because of conflicts; refresh so the
                // conflict banner and abort/continue controls become visible.
                await LoadStatusCoreAsync().ConfigureAwait(true);
            }
            else
            {
                StatusMessage = string.Format(Strings.Repo_StatusOperationCompleted, name);
                await ReloadAllCoreAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await Dialogs.ShowMessageAsync(string.Format(Strings.Repo_OperationFailedTitle, name), ex.Message).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task CreateBranchFromHeadAsync() => CreateBranchCoreAsync("HEAD");

    private async Task CreateBranchAsync()
    {
        var start = SelectedCommit?.Row.CommitId ?? "HEAD";
        await CreateBranchCoreAsync(start).ConfigureAwait(true);
    }

    private async Task CreateBranchCoreAsync(string start)
    {
        (string? Value, bool Flag)? prompt = await Dialogs.PromptWithCheckBoxAsync(Strings.Repo_CreateBranchTitle, Strings.Repo_BranchNameLabel, string.Empty, Strings.Repo_CheckoutAfterCreating, true).ConfigureAwait(true);
        if (prompt is null || string.IsNullOrWhiteSpace(prompt.Value.Value))
        {
            return;
        }

        ProcessResult result = await Handler.CreateBranchAsync(Path, prompt.Value.Value, start, prompt.Value.Flag).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_CreateBranchTitle, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task CreateTagAsync()
    {
        (string? Value, bool Flag)? prompt = await Dialogs.PromptWithCheckBoxAsync(Strings.Repo_CreateTagTitle, Strings.Repo_TagNameLabel, string.Empty, Strings.Repo_AnnotatedTag, false).ConfigureAwait(true);
        if (prompt is null || string.IsNullOrWhiteSpace(prompt.Value.Value))
        {
            return;
        }

        string? message = null;
        if (prompt.Value.Flag)
        {
            message = await Dialogs.PromptAsync(Strings.Repo_TagMessageTitle, Strings.Repo_MessageLabel, string.Empty).ConfigureAwait(true);
            if (message is null)
            {
                return;
            }
        }

        var revision = SelectedCommit?.Row.CommitId ?? "HEAD";
        ProcessResult result = await Handler.CreateTagAsync(Path, prompt.Value.Value, revision, message).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_CreateTagTitle, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task StashAsync()
    {
        (string? Value, bool Flag)? prompt = await Dialogs.PromptWithCheckBoxAsync(Strings.Repo_StashChangesTitle, Strings.Repo_StashMessageLabel, string.Empty, Strings.Repo_IncludeUntrackedFiles, true).ConfigureAwait(true);
        if (prompt is null)
        {
            return;
        }

        ProcessResult result = await Handler.StashSaveAsync(Path, prompt.Value.Value ?? string.Empty, prompt.Value.Flag).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_OpStash, result, reloadAll: false).ConfigureAwait(true);
        await LoadRefsCoreAsync().ConfigureAwait(true);
    }

    private async Task CheckoutBranchAsync(BranchRef? branch)
    {
        if (branch is null)
        {
            return;
        }

        // git refuses to check out a branch that another worktree has; offer that worktree instead.
        if (branch is { IsInOtherWorktree: true, WorktreePath: { } worktree })
        {
            if (await Dialogs.ConfirmAsync(Strings.Repo_BranchInOtherWorktreeTitle,
                    string.Format(Strings.Repo_BranchInOtherWorktreeConfirm, branch.Name, worktree)).ConfigureAwait(true))
            {
                await OpenWorktreePathAsync(worktree).ConfigureAwait(true);
            }

            return;
        }

        ProcessResult result = await Handler.CheckoutAsync(Path, branch.Name).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_OpCheckout, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task CheckoutReferenceAsync(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Strings.Repo_OpCheckout, string.Format(Strings.Repo_CheckoutReferenceConfirm, reference)).ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.CheckoutAsync(Path, reference).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_OpCheckout, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task DeleteBranchAsync(BranchRef? branch)
    {
        if (branch is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Strings.Repo_DeleteBranchTitle, string.Format(Strings.Repo_DeleteBranchConfirm, branch.Name)).ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.DeleteBranchAsync(Path, branch.Name, force: false).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_DeleteBranchTitle, result, reloadAll: true).ConfigureAwait(true);
    }

    private static bool IsSamePath(string a, string b)
    {
        static string Normalize(string path)
            => System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        try
        {
            return string.Equals(Normalize(a), Normalize(b), AppSettingsService.PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// A sibling of the main worktree named after the repository and branch,
    /// e.g. <c>D:\Code\Avgit-feature-login</c> for branch <c>feature/login</c>.
    /// </summary>
    internal string SuggestWorktreePath(string branch)
    {
        var main = Worktrees.FirstOrDefault()?.Path ?? Path;
        main = main.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var parent = System.IO.Path.GetDirectoryName(main) ?? main;
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string([.. branch.Select(c => c == '/' || c == '\\' || invalid.Contains(c) ? '-' : c)]);
        var baseName = $"{System.IO.Path.GetFileName(main)}-{safe}";
        var candidate = System.IO.Path.Combine(parent, baseName);
        for (var n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
        {
            candidate = System.IO.Path.Combine(parent, $"{baseName}-{n}");
        }

        return candidate;
    }

    /// <summary>
    /// Creates a worktree. For <paramref name="branch"/> it checks that branch out there;
    /// otherwise it asks for a new branch name and starts it at HEAD.
    /// </summary>
    private async Task AddWorktreeAsync(BranchRef? branch)
    {
        if (branch is { IsCheckedOut: true } or { IsInOtherWorktree: true })
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_CreateWorktreeTitle,
                branch.WorktreePath is { } checkedOutAt
                    ? string.Format(Strings.Repo_BranchAlreadyCheckedOutAt, branch.Name, checkedOutAt)
                    : string.Format(Strings.Repo_BranchAlreadyCheckedOutHere, branch.Name)).ConfigureAwait(true);
            return;
        }

        var branchName = branch?.Name;
        if (branchName is null)
        {
            branchName = (await Dialogs.PromptAsync(Strings.Repo_NewWorktreeTitle, Strings.Repo_NewWorktreeBranchLabel).ConfigureAwait(true))?.Trim();
            if (string.IsNullOrEmpty(branchName))
            {
                return;
            }
        }

        var folder = (await Dialogs.PromptAsync(Strings.Repo_NewWorktreeTitle, string.Format(Strings.Repo_NewWorktreeFolderLabel, branchName), SuggestWorktreePath(branchName))
            .ConfigureAwait(true))?.Trim();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        ProcessResult result = await Handler.AddWorktreeAsync(Path, folder, branchName, createBranch: branch is null, startPoint: null)
            .ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_CreateWorktreeTitle, result, reloadAll: true).ConfigureAwait(true);
        if (result.Success
            && await Dialogs.ConfirmAsync(Strings.Repo_WorktreeCreatedTitle, string.Format(Strings.Repo_WorktreeCreatedConfirm, branchName, folder))
                .ConfigureAwait(true))
        {
            await OpenWorktreePathAsync(folder).ConfigureAwait(true);
        }
    }

    private Task OpenWorktreeAsync(WorktreeEntry? worktree)
        => worktree is { CanOpen: true } ? OpenWorktreePathAsync(worktree.Path) : Task.CompletedTask;

    private Task OpenWorktreePathAsync(string folder)
        => OpenRepositoryAction?.Invoke(folder) ?? Task.CompletedTask;

    private async Task RemoveWorktreeAsync(WorktreeEntry? worktree)
    {
        if (worktree is not { CanRemove: true })
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Strings.Repo_RemoveWorktreeTitle,
                string.Format(Strings.Repo_RemoveWorktreeConfirm, worktree.Path, worktree.BranchText)).ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.RemoveWorktreeAsync(Path, worktree.Path, force: false).ConfigureAwait(true);
        // git refuses to delete uncommitted work without --force; ask before discarding it.
        if (!result.Success && result.Error.Contains("modified or untracked files", StringComparison.OrdinalIgnoreCase))
        {
            if (!await Dialogs.ConfirmAsync(Strings.Repo_RemoveWorktreeTitle,
                    Strings.Repo_RemoveWorktreeForceConfirm).ConfigureAwait(true))
            {
                await LoadRefsCoreAsync().ConfigureAwait(true);
                return;
            }

            result = await Handler.RemoveWorktreeAsync(Path, worktree.Path, force: true).ConfigureAwait(true);
        }

        if (result.Success)
        {
            WorktreeRemoved?.Invoke(worktree.Path);
        }

        await HandleOperationResultAsync(Strings.Repo_RemoveWorktreeTitle, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task PruneWorktreesAsync()
    {
        ProcessResult result = await Handler.PruneWorktreesAsync(Path).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_OpPruneWorktrees, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task DeleteTagAsync(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Strings.Repo_DeleteTagTitle, string.Format(Strings.Repo_DeleteTagConfirm, tag)).ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.DeleteTagAsync(Path, tag).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_DeleteTagTitle, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task CheckoutCommitAsync()
    {
        if (SelectedCommit is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Strings.Repo_CheckoutCommitTitle, string.Format(Strings.Repo_CheckoutCommitConfirm, SelectedCommit.Row.ShortId)).ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.CheckoutAsync(Path, SelectedCommit.Row.CommitId).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_OpCheckout, result, reloadAll: true).ConfigureAwait(true);
    }

    private IReadOnlyList<CommitRowViewModel> SelectedCommitRange(bool newestFirst)
    {
        List<CommitRowViewModel> commits = _selectedCommits.Count > 0
            ? [.. _selectedCommits.Distinct()]
            : SelectedCommit is not null
                ? [SelectedCommit]
                : [];

        // LogRows is newest-first. Cherry-pick applies oldest-first; revert
        // applies newest-first so the resulting history stays consistent.
        var ordered = commits
            .OrderByDescending(LogRows.IndexOf)
            .ToList();
        if (newestFirst)
        {
            ordered.Reverse();
        }

        return ordered;
    }

    private async Task CherryPickSelectedAsync()
    {
        IReadOnlyList<CommitRowViewModel> commits = SelectedCommitRange(newestFirst: false);
        if (commits.Count == 0)
        {
            return;
        }

        if (commits.Any(c => c.Row.ParentIds.Length > 1))
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_CherryPickTitle,
                Strings.Repo_CherryPickMergeCommitsMessage).ConfigureAwait(true);
            return;
        }
        if (commits.Count > 1
            && !await Dialogs.ConfirmAsync(Strings.Repo_CherryPickTitle,
                string.Format(Strings.Repo_CherryPickCommitsConfirm, commits.Count)).ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = commits.Count > 1 ? string.Format(Strings.Repo_StatusCherryPickingCommits, commits.Count) : Strings.Repo_StatusCherryPicking;
        try
        {
            for (var index = 0; index < commits.Count; index++)
            {
                CommitRowViewModel commit = commits[index];
                ProcessResult result = await Handler.CherryPickAsync(Path, commit.Row.CommitId).ConfigureAwait(true);
                if (!result.Success)
                {
                    var applied = index;
                    var detail = commits.Count > 1
                        ? string.Format(Strings.Repo_CherryPickPartialMessage, applied, commits.Count, result.Combined)
                        : result.Combined;
                    await Dialogs.ShowMessageAsync(Strings.Repo_CherryPickFailedTitle, detail).ConfigureAwait(true);
                    StatusMessage = commits.Count > 1
                        ? string.Format(Strings.Repo_StatusCherryPickStopped, applied, commits.Count)
                        : Strings.Repo_StatusCherryPickFailed;
                    await LoadStatusCoreAsync().ConfigureAwait(true);
                    return;
                }
            }

            StatusMessage = commits.Count == 1 ? Strings.Repo_StatusCherryPickCompleted : string.Format(Strings.Repo_StatusCherryPickedCommits, commits.Count);
            await ReloadAllCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RevertSelectedAsync()
    {
        IReadOnlyList<CommitRowViewModel> commits = SelectedCommitRange(newestFirst: true);
        if (commits.Count == 0)
        {
            return;
        }

        if (commits.Any(c => c.Row.ParentIds.Length > 1))
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_RevertTitle,
                Strings.Repo_RevertMergeCommitsMessage).ConfigureAwait(true);
            return;
        }
        if (commits.Count > 1
            && !await Dialogs.ConfirmAsync(Strings.Repo_RevertTitle,
                string.Format(Strings.Repo_RevertCommitsConfirm, commits.Count)).ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = commits.Count > 1 ? string.Format(Strings.Repo_StatusRevertingCommits, commits.Count) : Strings.Repo_StatusReverting;
        try
        {
            for (var index = 0; index < commits.Count; index++)
            {
                CommitRowViewModel commit = commits[index];
                ProcessResult result = await Handler.RevertAsync(Path, commit.Row.CommitId).ConfigureAwait(true);
                if (!result.Success)
                {
                    var applied = index;
                    var detail = commits.Count > 1
                        ? string.Format(Strings.Repo_RevertPartialMessage, applied, commits.Count, result.Combined)
                        : result.Combined;
                    await Dialogs.ShowMessageAsync(Strings.Repo_RevertFailedTitle, detail).ConfigureAwait(true);
                    StatusMessage = commits.Count > 1
                        ? string.Format(Strings.Repo_StatusRevertStopped, applied, commits.Count)
                        : Strings.Repo_StatusRevertFailed;
                    await LoadStatusCoreAsync().ConfigureAwait(true);
                    return;
                }
            }

            StatusMessage = commits.Count == 1 ? Strings.Repo_StatusRevertCompleted : string.Format(Strings.Repo_StatusRevertedCommits, commits.Count);
            await ReloadAllCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task MergeCommitAsync()
    {
        if (SelectedCommit is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Strings.Repo_OpMerge, string.Format(Strings.Repo_MergeCommitConfirm, SelectedCommit.Row.ShortId)).ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.MergeAsync(Path, SelectedCommit.Row.CommitId).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_OpMerge, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task RebaseCommitAsync()
    {
        if (SelectedCommit is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Strings.Repo_OpRebase, string.Format(Strings.Repo_RebaseOntoCommitConfirm, SelectedCommit.Row.ShortId)).ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.RebaseAsync(Path, SelectedCommit.Row.CommitId).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_OpRebase, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task ResetCommitAsync()
    {
        if (SelectedCommit is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Strings.Repo_OpReset, string.Format(Strings.Repo_HardResetConfirm, SelectedCommit.Row.ShortId)).ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.ResetAsync(Path, SelectedCommit.Row.CommitId, hard: true).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_OpReset, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task ApplyStashAsync(StashEntry? stash)
    {
        if (stash is null)
        {
            return;
        }

        ProcessResult result = await Handler.StashApplyAsync(Path, stash.Name, pop: false).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_OpApplyStash, result, reloadAll: false).ConfigureAwait(true);
        await LoadStatusCoreAsync().ConfigureAwait(true);
    }

    private async Task PopStashAsync(StashEntry? stash)
    {
        if (stash is null)
        {
            return;
        }

        ProcessResult result = await Handler.StashApplyAsync(Path, stash.Name, pop: true).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_OpPopStash, result, reloadAll: true).ConfigureAwait(true);
        await LoadStatusCoreAsync().ConfigureAwait(true);
    }

    private async Task DropStashAsync(StashEntry? stash)
    {
        if (stash is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Strings.Repo_DropStashTitle, string.Format(Strings.Repo_DropStashConfirm, stash.Name)).ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.StashDropAsync(Path, stash.Name).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_DropStashTitle, result, reloadAll: false).ConfigureAwait(true);
        await LoadRefsCoreAsync().ConfigureAwait(true);
    }

    private async Task HandleOperationResultAsync(string name, ProcessResult result, bool reloadAll)
    {
        if (!result.Success)
        {
            await Dialogs.ShowMessageAsync(string.Format(Strings.Repo_OperationFailedTitle, name), result.Combined).ConfigureAwait(true);
            StatusMessage = string.Format(Strings.Repo_StatusOperationFailed, name);
            await LoadStatusCoreAsync().ConfigureAwait(true);
            return;
        }

        StatusMessage = string.Format(Strings.Repo_StatusOperationCompleted, name);
        if (reloadAll)
        {
            await ReloadAllCoreAsync().ConfigureAwait(true);
        }
        else
        {
            await LoadStatusCoreAsync().ConfigureAwait(true);
        }
    }

    private async Task AddRemoteAsync()
    {
        IReadOnlyDictionary<string, string> existing = await Handler.GetRemotesAsync(Path).ConfigureAwait(true);
        var defaultName = existing.ContainsKey("origin") ? string.Empty : "origin";
        var name = await Dialogs.PromptAsync(Strings.Repo_AddRemoteTitle, Strings.Repo_RemoteNameLabel, defaultName).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (existing.ContainsKey(name))
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_AddRemoteTitle, string.Format(Strings.Repo_RemoteAlreadyExists, name)).ConfigureAwait(true);
            return;
        }

        var url = await Dialogs.PromptAsync(Strings.Repo_AddRemoteTitle, Strings.Repo_RemoteUrlLabel, string.Empty).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!LooksLikeRemoteUrl(url))
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_AddRemoteTitle,
                Strings.Repo_InvalidRemoteUrl).ConfigureAwait(true);
            return;
        }

        ProcessResult result = await Handler.AddRemoteAsync(Path, name, url).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_AddRemoteTitle, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task DeleteRemoteAsync(RemoteEntry? remote)
    {
        if (remote is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Strings.Repo_RemoveRemoteTitle,
                string.Format(Strings.Repo_RemoveRemoteConfirm, remote.Name)).ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.DeleteRemoteAsync(Path, remote.Name).ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_RemoveRemoteTitle, result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task EditRemoteAsync(RemoteEntry? remote)
    {
        if (remote is null)
        {
            return;
        }

        var name = await Dialogs.PromptAsync(Strings.Repo_EditRemoteTitle, Strings.Repo_RemoteNameLabel, remote.Name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        name = name.Trim();
        if (name != remote.Name && Remotes.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_EditRemoteTitle, string.Format(Strings.Repo_RemoteAlreadyExists, name)).ConfigureAwait(true);
            return;
        }

        var url = await Dialogs.PromptAsync(Strings.Repo_EditRemoteTitle, Strings.Repo_RemoteUrlLabel, remote.Url).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        url = url.Trim();
        if (!LooksLikeRemoteUrl(url))
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_EditRemoteTitle,
                Strings.Repo_InvalidRemoteUrl).ConfigureAwait(true);
            return;
        }

        IsBusy = true;
        try
        {
            var currentName = remote.Name;
            if (!string.Equals(name, currentName, StringComparison.Ordinal))
            {
                ProcessResult rename = await Handler.RenameRemoteAsync(Path, currentName, name).ConfigureAwait(true);
                if (!rename.Success)
                {
                    await Dialogs.ShowMessageAsync(Strings.Repo_RenameRemoteFailedTitle, rename.Combined).ConfigureAwait(true);
                    return;
                }
                currentName = name;
            }

            if (!string.Equals(url, remote.Url ?? string.Empty, StringComparison.Ordinal))
            {
                ProcessResult setUrl = await Handler.SetRemoteUrlAsync(Path, currentName, url).ConfigureAwait(true);
                if (!setUrl.Success)
                {
                    // The rename already succeeded; refresh so the sidebar does
                    // not keep showing a remote name that no longer exists.
                    await ReloadAllCoreAsync().ConfigureAwait(true);
                    await Dialogs.ShowMessageAsync(Strings.Repo_UpdateRemoteFailedTitle, setUrl.Combined).ConfigureAwait(true);
                    return;
                }
            }

            StatusMessage = string.Format(Strings.Repo_StatusUpdatedRemote, name);
            await ReloadAllCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool LooksLikeRemoteUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https" or "ssh" or "git" or "file")
        {
            return true;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(url, @"^[^@\s]+@[^:\s]+:.+$"))
        {
            return true;
        }

        return System.IO.Path.IsPathRooted(url)
               || url.StartsWith("./", StringComparison.Ordinal)
               || url.StartsWith("../", StringComparison.Ordinal)
               || url.StartsWith(@".\", StringComparison.Ordinal)
               || url.StartsWith(@"..\", StringComparison.Ordinal);
    }

    private async Task InteractiveRebaseAsync()
    {
        CommitRowViewModel? commit = SelectedCommit;
        if (commit is null)
        {
            return;
        }

        if (IsMergeInProgress || IsRebaseInProgress)
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_OpInteractiveRebase,
                Strings.Repo_InteractiveRebaseInProgressMessage).ConfigureAwait(true);
            return;
        }
        if (commit.Row.ParentIds.Length > 1)
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_OpInteractiveRebase,
                Strings.Repo_InteractiveRebaseMergeCommitMessage).ConfigureAwait(true);
            return;
        }
        var fromRoot = commit.Row.ParentIds.Length == 0;

        if (!fromRoot)
        {
            ProcessResult ancestor = await Handler.RunAsync(Path,
                ["merge-base", "--is-ancestor", commit.Row.CommitId, "HEAD"]).ConfigureAwait(true);
            if (!ancestor.Success)
            {
                if (ancestor.ExitCode == 1)
                {
                    await Dialogs.ShowMessageAsync(Strings.Repo_OpInteractiveRebase,
                        Strings.Repo_InteractiveRebaseNotAncestorMessage).ConfigureAwait(true);
                }
                else
                {
                    await Dialogs.ShowMessageAsync(Strings.Repo_OpInteractiveRebase, ancestor.Combined).ConfigureAwait(true);
                }
                return;
            }
        }

        var baseRevision = fromRoot ? string.Empty : commit.Row.CommitId + "^";
        Handler.ClearLastError();
        string todo;
        try
        {
            todo = await Handler.GetInteractiveRebaseTodoAsync(Path, baseRevision, fromRoot).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_OpInteractiveRebase, ex.Message).ConfigureAwait(true);
            return;
        }

        if (ReportHandlerError(Strings.Repo_ErrorBuildRebaseTodo))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(todo))
        {
            StatusMessage = Strings.Repo_StatusNoCommitsToRebase;
            return;
        }

        var items = todo.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(record =>
            {
                var fields = record.Split('\x1f', 3);
                var sha = fields.Length > 0 ? fields[0].Trim() : string.Empty;
                var shortSha = fields.Length > 1 ? fields[1].Trim() : (sha.Length > 7 ? sha[..7] : sha);
                var message = fields.Length > 2 ? fields[2].TrimEnd('\r', '\n') : string.Empty;
                return new RebasePlanItem { Sha = sha, ShortSha = shortSha, Message = message, Action = "pick" };
            })
            .Where(item => item.Sha.Length > 0)
            .ToList();

        IReadOnlyList<RebasePlanItem>? plan = await Dialogs.EditRebasePlanAsync(Strings.Repo_OpInteractiveRebase, items).ConfigureAwait(true);
        if (plan is null || plan.Count == 0)
        {
            return;
        }

        RebasePlanItem? firstAction = plan.FirstOrDefault(item => item.Action != "drop");
        if (firstAction is { Action: "squash" or "fixup" })
        {
            await Dialogs.ShowMessageAsync(Strings.Repo_OpInteractiveRebase,
                Strings.Repo_InteractiveRebaseFirstSquashMessage).ConfigureAwait(true);
            return;
        }

        var editedTodo = string.Join("\n", plan.Select(item => $"{item.Action} {item.Sha}"));
        var rewordMessages = plan
            .Where(item => item.Action == "reword" && !string.IsNullOrWhiteSpace(item.Message))
            .ToDictionary(item => item.Sha, item => item.Message, StringComparer.Ordinal);

        ProcessResult result = await Handler.InteractiveRebaseAsync(Path, baseRevision, editedTodo, rewordMessages, fromRoot)
            .ConfigureAwait(true);
        await HandleOperationResultAsync(Strings.Repo_OpInteractiveRebase, result, reloadAll: true).ConfigureAwait(true);
    }

    private const int WatcherQuietPeriodMs = 600;
    private const int WatcherMaxBurstMs = 3000;

    // Typical build and dependency folders; events under them are dropped once git
    // confirms the folder is ignored (checked once per folder).
    private static readonly HashSet<string> HeavyDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "target", "build", "dist", "out", ".vs", ".idea",
        ".gradle", "__pycache__", ".next", ".nuxt", "packages", ".venv", "venv"
    };

    private void StartWatcher()
    {
        if (_watcher is not null || _disposed)
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                               | NotifyFilters.DirectoryName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;
        }
        catch
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>
    /// Called by the main window when this tab is shown or hidden. A hidden tab
    /// only records that something changed and refreshes when shown again.
    /// </summary>
    public void SetActive(bool active)
    {
        if (_isActive == active)
        {
            return;
        }

        _isActive = active;
        if (active && IsLoaded && Interlocked.Exchange(ref _changedWhileInactive, 0) != 0)
        {
            _ = RefreshFromWatcherAsync();
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        var separator = System.IO.Path.DirectorySeparatorChar;
        var gitSegment = $"{separator}.git{separator}";
        if (_disposed
            || e.FullPath.Contains($"{gitSegment}objects{separator}", StringComparison.OrdinalIgnoreCase)
            || e.FullPath.Contains($"{gitSegment}logs{separator}", StringComparison.OrdinalIgnoreCase)
            || e.FullPath.Contains($"{gitSegment}avgit-rebase{separator}", StringComparison.OrdinalIgnoreCase)
            // index.lock, HEAD.lock etc. are transient; the rename onto the real
            // file raises its own event, so lock churn only causes extra refreshes.
            || e.FullPath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Working-tree and index changes only need a status refresh. History
        // (and the selected commit diff) changes only when HEAD or a ref moves.
        var gitIndex = e.FullPath.IndexOf(gitSegment, StringComparison.OrdinalIgnoreCase);
        if (gitIndex >= 0)
        {
            var relative = e.FullPath[(gitIndex + gitSegment.Length)..].Replace('\\', '/');
            var isRefChange = relative.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                              || relative.Equals("packed-refs", StringComparison.OrdinalIgnoreCase)
                              || relative.StartsWith("refs/", StringComparison.OrdinalIgnoreCase)
                              || relative.StartsWith("worktrees/", StringComparison.OrdinalIgnoreCase);
            if (isRefChange)
            {
                Interlocked.Exchange(ref _refsDirty, 1);
            }
        }
        else if (IsInIgnoredDirectory(e.FullPath))
        {
            return;
        }

        if (!_isActive)
        {
            Interlocked.Exchange(ref _changedWhileInactive, 1);
            return;
        }

        Interlocked.CompareExchange(ref _burstStartTicks, Environment.TickCount64, 0);
        var generation = Interlocked.Increment(ref _watchGeneration);
        _ = DebouncedRefreshAsync(generation);
    }

    /// <summary>
    /// True for events under a git-ignored top-level folder or a git-ignored folder
    /// with a typical build/dependency name (node_modules, bin, obj, ...). Each
    /// candidate folder is checked once with `git check-ignore` in the background.
    /// </summary>
    private bool IsInIgnoredDirectory(string fullPath)
    {
        var relative = System.IO.Path.GetRelativePath(Path, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
        {
            return false;
        }

        var segments = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var prefix = string.Empty;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            prefix = i == 0 ? segments[0] : prefix + "/" + segments[i];
            if (i > 0 && !HeavyDirectoryNames.Contains(segments[i]))
            {
                continue;
            }

            if (_ignoredDirectories.TryGetValue(prefix, out var ignored))
            {
                if (ignored)
                {
                    return true;
                }

                continue;
            }

            if (_ignoredDirectories.TryAdd(prefix, false))
            {
                _ = CheckIgnoredDirectoryAsync(prefix);
            }
        }

        return false;
    }

    private async Task CheckIgnoredDirectoryAsync(string directory)
    {
        try
        {
            ProcessResult result = await Handler.RunAsync(Path, ["check-ignore", "-q", "--", directory + "/"]).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                _ignoredDirectories[directory] = true;
            }
        }
        catch
        {
            // Unknown folders are simply watched.
        }
    }

    private async Task DebouncedRefreshAsync(int generation)
    {
        await Task.Delay(WatcherQuietPeriodMs).ConfigureAwait(false);
        if (_disposed)
        {
            return;
        }

        // Refresh once events pause; during a long burst (build, checkout, package
        // install) refresh at most every few seconds instead of never or constantly.
        var start = Interlocked.Read(ref _burstStartTicks);
        var burstAge = start == 0 ? 0 : Environment.TickCount64 - start;
        if (generation != Volatile.Read(ref _watchGeneration) && burstAge < WatcherMaxBurstMs)
        {
            return;
        }

        Interlocked.Exchange(ref _burstStartTicks, 0);
        Dispatcher.UIThread.Post(() => _ = RefreshFromWatcherAsync());
    }

    private async Task RefreshFromWatcherAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (IsBusy)
        {
            // An operation is running (and will reload); look again afterwards.
            _ = DebouncedRefreshAsync(Interlocked.Increment(ref _watchGeneration));
            return;
        }

        var refsChanged = Interlocked.Exchange(ref _refsDirty, 0) != 0;
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_disposed)
            {
                return;
            }

            if (refsChanged)
            {
                // A ref moved (commit, fetch, checkout from another tool): branches,
                // tags and history may all have changed.
                await Task.WhenAll(LoadStatusCoreAsync(), LoadRefsCoreAsync(), LoadLogCoreAsync()).ConfigureAwait(true);
            }
            else
            {
                await LoadStatusCoreAsync().ConfigureAwait(true);
            }
        }
        catch
        {
            // File-watcher races are expected while files are being written.
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetAutoRefresh(bool enabled)
    {
        if (enabled)
        {
            StartWatcher();
        }
        else
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
        _commitSelectionCts?.Cancel();
        _commitDiffCts?.Cancel();
        _workingDiffCts?.Cancel();
    }

    private void OpenTerminal()
    {
        var error = TerminalLauncher.Launch(Path, Settings.Settings.TerminalShell);
        StatusMessage = error ?? Strings.Repo_StatusOpenedTerminal;
    }

    private void OpenInExplorer()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = Path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void OpenSelectedFile()
    {
        ChangedFile? file = SelectedUnstagedFile ?? SelectedStagedFile ?? SelectedCommitFile;
        if (file is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = file.FullPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>One side of a comparison, labelled from its history row.</summary>
    private sealed record CommitSide(string Sha, string ShortSha, string Title)
    {
        public static CommitSide FromRow(CommitRowViewModel row) =>
            new(row.Row.CommitId, row.Row.ShortId, row.Row.Title);
    }

    /// <summary>The two commits compared in the right-hand panes; <see cref="Base"/> is the older side.</summary>
    private sealed record CommitPair(CommitSide Base, CommitSide Target);
}




