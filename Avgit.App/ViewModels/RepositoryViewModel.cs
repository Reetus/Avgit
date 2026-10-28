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
    private string _selectedBranchFilter = "(all branches)";
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
    private string? _compareBaseSha;
    private bool _rangeMode;
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

        IsLinkedWorktree = !string.Equals(GitRepoHandler.GetGitDir(path), GitRepoHandler.GetCommonGitDir(path),
            AppSettingsService.PathComparison);
        RepoTypeText = IsLinkedWorktree ? "Git · linked worktree" : "Git";
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

        CommitDiff = new DiffViewModel();
        WorkingDiff = new DiffViewModel();
        WireDiffActions();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        FetchCommand = new AsyncRelayCommand(() => RunOperationAsync("Fetch", ct => Handler.FetchAsync(Path, null, ct)));
        PullCommand = new AsyncRelayCommand(() => RunOperationAsync("Pull", ct => Handler.PullAsync(Path, null, ct)));
        PushCommand = new AsyncRelayCommand(PushAsync);
        PushToRemoteCommand = new AsyncRelayCommand(PushToRemoteAsync);
        ForcePushCommand = new AsyncRelayCommand(ForcePushAsync);
        PullRebaseCommand = new AsyncRelayCommand(() => RunOperationAsync("Pull with rebase", ct => Handler.PullAsync(Path, true, ct)));
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
            _compareBaseSha = SelectedCommit?.Row.CommitId;
            _rangeMode = false;
            StatusMessage = _compareBaseSha is null ? "Select a commit to compare." : $"Compare base: {_compareBaseSha[..Math.Min(8, _compareBaseSha.Length)]}";
        });
        CompareWithBaseCommand = new AsyncRelayCommand(CompareWithBaseAsync);
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
                StatusMessage = "Commit SHA copied.";
            }
        });
        AbortMergeCommand = new AsyncRelayCommand(() => RunOperationAsync("Abort merge", ct => Handler.AbortMergeAsync(Path, ct)));
        AbortRebaseCommand = new AsyncRelayCommand(() => RunOperationAsync("Abort rebase", ct => Handler.AbortRebaseAsync(Path, ct)));
        ContinueRebaseCommand = new AsyncRelayCommand(() => RunOperationAsync("Continue rebase", ct => Handler.ContinueRebaseAsync(Path, ct)));
        SkipRebaseCommitCommand = new AsyncRelayCommand(() => RunOperationAsync("Skip commit", ct => Handler.SkipRebaseCommitAsync(Path, ct)));
        CherryPickContinueCommand = new AsyncRelayCommand(() => RunOperationAsync("Continue cherry-pick", ct => Handler.CherryPickContinueAsync(Path, ct)));
        CherryPickAbortCommand = new AsyncRelayCommand(() => RunOperationAsync("Abort cherry-pick", ct => Handler.CherryPickAbortAsync(Path, ct)));
        CherryPickSkipCommand = new AsyncRelayCommand(() => RunOperationAsync("Skip cherry-pick", ct => Handler.CherryPickSkipAsync(Path, ct)));
        RevertContinueCommand = new AsyncRelayCommand(() => RunOperationAsync("Continue revert", ct => Handler.RevertContinueAsync(Path, ct)));
        RevertAbortCommand = new AsyncRelayCommand(() => RunOperationAsync("Abort revert", ct => Handler.RevertAbortAsync(Path, ct)));
        InteractiveRebaseCommand = new AsyncRelayCommand(InteractiveRebaseAsync);
        AddRemoteCommand = new AsyncRelayCommand(AddRemoteAsync);
        DeleteRemoteCommand = new AsyncRelayCommand(p => DeleteRemoteAsync(p as RemoteEntry));
        EditRemoteCommand = new AsyncRelayCommand(p => EditRemoteAsync(p as RemoteEntry));
        ApplyLogFilterCommand = new AsyncRelayCommand(LoadLogCoreAsync);
        LoadMoreLogCommand = new AsyncRelayCommand(LoadMoreLogAsync);
        ClearLogFilterCommand = new AsyncRelayCommand(() =>
        {
            AuthorFilter = string.Empty;
            _selectedBranchFilter = "(all branches)";
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
    public bool HasLinkedWorktrees => Worktrees.Count > 1;
    public bool HasPrunableWorktrees => Worktrees.Any(w => w.IsPrunable);
    public ObservableCollection<RemoteEntry> Remotes { get; } = [];
    public ObservableCollection<string> Tags { get; } = [];
    public ObservableCollection<StashEntry> Stashes { get; } = [];
    public ObservableCollection<string> BranchFilterOptions { get; } = ["(all branches)"];
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
                _selectionTask = SelectCommitAsync(value);
            }
        }
    }

    public void SetSelectedCommits(IEnumerable<CommitRowViewModel> commits)
    {
        _selectedCommits.Clear();
        _selectedCommits.AddRange(commits);
    }

    /// <summary>
    /// Sets the context/active commit without forcing the multi-selection to a
    /// single row. Used by the history context menu.
    /// </summary>
    public void SetContextCommit(CommitRowViewModel? commit)
    {
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
                StatusMessage = "There is no commit to amend yet.";
                return;
            }

            var current = CommitMessage;
            var isPlaceholder = string.IsNullOrWhiteSpace(current)
                                || string.Equals(current, _commitTemplate, StringComparison.Ordinal);
            if (!isPlaceholder && !string.Equals(current, message, StringComparison.Ordinal))
            {
                if (!await Dialogs.ConfirmAsync("Amend last commit",
                        "Replace the current commit message with the last commit's message?").ConfigureAwait(true))
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

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready";

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
        ? $"{LogRows.Count} commits (cached, refreshing)"
        : $"{LogRows.Count} commits";

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
        ? "A rebase is in progress. Resolve conflicts, then continue, skip or abort."
        : IsCherryPickInProgress
            ? "A cherry-pick is in progress. Resolve conflicts, then continue, skip or abort."
            : IsRevertInProgress
                ? "A revert is in progress. Resolve conflicts, then continue or abort."
                : IsMergeInProgress
                    ? "A merge is in progress. Resolve conflicts, stage the files, then commit or abort."
                    : string.Empty;

    [ObservableProperty]
    public partial string AuthorFilter { get; set; } = string.Empty;

    public string SelectedBranchFilter
    {
        get => _selectedBranchFilter;
        set
        {
            if (SetProperty(ref _selectedBranchFilter, value) && IsLoaded)
            {
                _selectTipAfterLog = value != "(all branches)";
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

    public string StagedHeader => $"Staged ({StagedFiles.Count})";
    public string UnstagedHeader => $"Unstaged ({UnstagedFiles.Count})";

    public ICommand RefreshCommand { get; }
    public ICommand FetchCommand { get; }
    public ICommand PullCommand { get; }
    public ICommand PushCommand { get; }
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
            StatusMessage = $"Loading {Name}...";
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
            await Dialogs.ShowMessageAsync("Could not load repository", ex.ToString()).ConfigureAwait(true);
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
            StatusMessage = "Refreshing...";
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
            RecordError("Could not read repository references", refs.Error);
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
        OnPropertyChanged(nameof(HasLinkedWorktrees));
        OnPropertyChanged(nameof(HasPrunableWorktrees));
        OnPropertyChanged(nameof(HasRemotes));
        OnPropertyChanged(nameof(HasRemoteBranches));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasStashes));

        var previousFilter = SelectedBranchFilter;
        BranchFilterOptions.Clear();
        BranchFilterOptions.Add("(all branches)");
        foreach (BranchRef branch in Branches)
        {
            BranchFilterOptions.Add(branch.Name);
        }

        foreach (BranchRef branch in RemoteBranches)
        {
            BranchFilterOptions.Add(branch.Name);
        }

        _selectedBranchFilter = BranchFilterOptions.Contains(previousFilter) ? previousFilter : "(all branches)";
        OnPropertyChanged(nameof(SelectedBranchFilter));
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

    private bool IsUnfilteredHistory => SelectedBranchFilter == "(all branches)" && string.IsNullOrWhiteSpace(AuthorFilter);

    private HistoryFilter BuildLogQuery(int skip, int limit) => new()
    {
        Offset = (uint)skip,
        Count = (uint)limit,
        StartRef = SelectedBranchFilter == "(all branches)" ? null : SelectedBranchFilter,
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
        try
        {
            LogPage page = await Handler.GetLogPageAsync(Path, BuildLogQuery(0, _logLimit)).ConfigureAwait(true);
            if (generation != _logGeneration)
            {
                return;
            }

            if (page.Error is not null)
            {
                RecordError("Could not read history", page.Error);
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
            // Commit contents are immutable for a given SHA, so reloading the
            // details/diff is unnecessary when the reselected commit is the one
            // already displayed.
            sameSelection = !selectTip
                            && reselect is not null
                            && string.Equals(_displayedCommitSha, reselect.Row.CommitId, StringComparison.Ordinal);
            toSelect = selectTip ? LogRows.FirstOrDefault() : (reselect ?? LogRows.FirstOrDefault());
            _selectedCommit = toSelect;
            _selectedCommits.Clear();
            if (toSelect is not null)
            {
                _selectedCommits.Add(toSelect);
            }

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
        if (!sameSelection)
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
                RecordError("Could not read history", page.Error);
                return;
            }
            if (page.Rows.Count == 0)
            {
                StatusMessage = "No more history to load.";
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
            _rangeMode = false;
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
                    RecordError("Could not read the commit", result.Error);
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
        CommitRowViewModel? commit = SelectedCommit;
        if (file is null || commit is null)
        {
            CommitDiff.Clear("Select a file in the commit to view its diff.");
            return;
        }

        var generation = ++_commitDiffGeneration;
        _commitDiffCts?.Cancel();
        CancellationTokenSource cts = _commitDiffCts = new CancellationTokenSource();
        var baseSha = _compareBaseSha;
        var useRange = _rangeMode && !string.IsNullOrWhiteSpace(baseSha);
        var request = new DiffSpec
        {
            Kind = useRange ? DiffRequestKind.CommitRange : DiffRequestKind.Commit,
            BaseRevision = useRange ? baseSha : null,
            Revision = commit.Row.CommitId,
            // Parents are known from history, so the diff needs no rev-list lookup.
            FirstParent = useRange ? null : (commit.Row.ParentIds.Length > 0 ? commit.Row.ParentIds[0] : string.Empty),
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
                || !string.Equals(SelectedCommit?.Row.CommitId, commit.Row.CommitId, StringComparison.Ordinal))
            {
                return;
            }

            if (ReportHandlerError("Could not load the commit diff"))
            {
                return;
            }

            var env = new DiffSetup
            {
                Subject = useRange ? DiffSubject.Range : DiffSubject.SingleCommit,
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

            var shortBase = baseSha is null ? string.Empty : baseSha[..Math.Min(8, baseSha.Length)];
            var title = useRange
                ? $"{shortBase}..{commit.Row.ShortId}  {file.RelativePath}"
                : $"{commit.Row.ShortId}  {file.RelativePath}";
            var revision = commit.Row.CommitId;
            var oldRevision = useRange ? baseSha : (commit.Row.ParentIds.Length > 0 ? commit.Row.ParentIds[0] : null);
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

    private async Task CompareWithBaseAsync()
    {
        if (_selectionTask is not null)
        {
            await _selectionTask.ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(_compareBaseSha) || SelectedCommit is null)
        {
            StatusMessage = "Set a compare base commit first.";
            return;
        }

        IsBusy = true;
        try
        {
            Handler.ClearLastError();
            IReadOnlyList<ChangedFile> changes = await Handler.GetRangeChangesAsync(Path, _compareBaseSha, SelectedCommit.Row.CommitId).ConfigureAwait(true);
            if (ReportHandlerError("Could not compare commits"))
            {
                return;
            }

            CommitFiles.Clear();
            foreach (ChangedFile change in changes)
            {
                CommitFiles.Add(change);
            }

            _rangeMode = true;
            _selectedCommitFile = CommitFiles.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedCommitFile));
            if (_selectedCommitFile is not null)
            {
                await LoadCommitDiffAsync().ConfigureAwait(true);
            }
            else
            {
                CommitDiff.Clear("No changed files in this range.");
            }
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
            RecordError("Could not read the working copy", snapshot.Error);
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
            SummaryText = $"Working copy clean on {CurrentBranch}";
        }
        else
        {
            var parts = new List<string>();
            void Add(int count, string label)
            {
                if (count > 0)
                {
                    parts.Add($"{count} {label}");
                }
            }

            Add(summary.Modified, "modified");
            Add(summary.Added, "added");
            Add(summary.Removed, "removed");
            Add(summary.Renamed, "renamed");
            Add(summary.Conflicted, "conflicted");
            Add(summary.Untracked, "untracked");
            SummaryText = $"{(parts.Count > 0 ? string.Join(", ", parts) : "Changes")} on {CurrentBranch}";
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
            WorkingDiff.Clear("Select a file to view its diff.");
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
                WorkingDiff.Clear($"Untracked folder with {(count >= 10_000 ? "10,000+" : count.ToString("N0"))} file(s). " +
                                  "Stage it to add everything inside, or turn on \"Show files inside untracked folders\" in Settings.");
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

                if (ReportHandlerError("Could not load the diff"))
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
            WorkingDiff.SetFiles(files, $"{(staged ? "Staged" : "Working copy")}  {file.RelativePath}",
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
                        return new ImageBytes(null, "The file no longer exists.");
                    }

                    return info.Length > DiffImages.MaxBytes
                        ? new ImageBytes(null, $"Too large to preview ({DiffImages.FormatSize(info.Length)}).")
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
            return new ImageBytes(null, $"Too large to preview (over {DiffImages.FormatSize(DiffImages.MaxBytes)}).");
        }

        return result.Success ? new ImageBytes(result.Output) : new ImageBytes(null, "Could not read the image from git.");
    }

    private void WireDiffActions()
    {
        CommitDiff.ReadImageAction = ReadImageAsync;
        WorkingDiff.ReadImageAction = ReadImageAsync;
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
                await Dialogs.ShowMessageAsync("Stage failed", result.Combined).ConfigureAwait(true);
            }
            else
            {
                StatusMessage = $"Staged {list.Count} file(s).";
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
                await Dialogs.ShowMessageAsync("Unstage failed", result.Combined).ConfigureAwait(true);
            }
            else
            {
                StatusMessage = $"Unstaged {list.Count} file(s).";
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
            ? $"Discard changes in {files.Count} file(s)? This cannot be undone."
            : $"Discard changes in {files.Count} item(s), including {folders} untracked folder(s) and " +
              "everything inside them? This cannot be undone.";
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
            StatusMessage = "Conflicted files must be resolved or the merge/rebase aborted.";
            return;
        }

        if (confirm && !await Dialogs.ConfirmAsync("Discard changes",
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
                await Dialogs.ShowMessageAsync("Discard failed", result.Combined).ConfigureAwait(true);
            }
            else
            {
                StatusMessage = $"Discarded changes in {list.Count} file(s)."
                                + (conflicts.Count > 0 ? $" Skipped {conflicts.Count} conflicted file(s)." : string.Empty);
            }

            await LoadStatusCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyHunkAsync(Hunk hunk, bool stage, bool unstage, bool discard, bool selectedOnly = false)
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

        if (discard && !await Dialogs.ConfirmAsync("Discard hunk", "Discard the selected hunk? This cannot be undone.").ConfigureAwait(true))
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
                StatusMessage = "Select added or removed lines first.";
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
            StatusMessage = "No stageable changes in the selection.";
            return;
        }

        IsBusy = true;
        try
        {
            ProcessResult result = await Handler.ApplyPatchAsync(Path, patch,
                cached: stage || unstage, reverse: unstage || discard).ConfigureAwait(true);
            if (!result.Success)
            {
                await Dialogs.ShowMessageAsync("Patch failed", result.Combined).ConfigureAwait(true);
            }

            StatusMessage = !result.Success
                ? "Patch failed."
                : discard ? "Hunk discarded." : stage ? "Hunk staged." : "Hunk unstaged.";
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
            StatusMessage = "Selected lines of a new file cannot be discarded; discard the whole file instead.";
            return;
        }

        var patch = PatchBuilder.CreateNewFileSelectionPatch(hunk, forUnstage: unstage);
        if (patch is null)
        {
            StatusMessage = "Select added lines first.";
            return;
        }

        IsBusy = true;
        try
        {
            ProcessResult result = await Handler.ApplyPatchAsync(Path, patch, cached: true, reverse: unstage,
                allowFuzzy: false).ConfigureAwait(true);
            if (!result.Success)
            {
                await Dialogs.ShowMessageAsync("Patch failed", result.Combined).ConfigureAwait(true);
            }

            StatusMessage = !result.Success ? "Patch failed." : stage ? "Selected lines staged." : "Selected lines unstaged.";
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
                ? "Resolve and stage all conflicts before committing."
                : "Enter a commit message and stage at least one file.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            StatusMessage = "Enter a commit message.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Committing...";
            ProcessResult result = await Handler.CommitAsync(Path, CommitMessage, Amend, SignOff, GpgSign).ConfigureAwait(true);
            if (!result.Success)
            {
                await Dialogs.ShowMessageAsync("Commit failed", result.Combined).ConfigureAwait(true);
                StatusMessage = "Commit failed.";
            }
            else
            {
                CommitMessage = string.Empty;
                // The draft saved when amend was ticked belongs to this commit; do not restore it.
                _amendMessage = null;
                _messageBeforeAmend = null;
                Amend = false;
                StatusMessage = "Commit created.";
                await ReloadAllCoreAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Recovery options offered when a push is rejected.
    internal const string PullRebaseThenPush = "Pull with rebase, then push";
    internal const string PullMergeThenPush = "Pull with merge, then push";
    internal const string ForcePushWithLease = "Force push with lease (replace the remote branch)";
    internal const string FetchOnly = "Fetch only";
    internal const string FetchToReview = "Fetch to review their commits";

    /// <summary>What to push: Branch is a local branch; DetachedTarget pushes HEAD to that remote branch.</summary>
    private sealed record PushRequest(string? Remote, string? Branch, string? DetachedTarget, bool SetUpstream, bool Force);

    private async Task PushToRemoteAsync()
    {
        IReadOnlyDictionary<string, string> remotes = await Handler.GetRemotesAsync(Path).ConfigureAwait(true);
        if (remotes.Count == 0)
        {
            await Dialogs.ShowMessageAsync("Push", "No remotes are configured.").ConfigureAwait(true);
            return;
        }

        var remote = await Dialogs.PromptAsync("Push to remote", "Remote", remotes.ContainsKey("origin") ? "origin" : remotes.Keys.First()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(remote))
        {
            return;
        }

        var branch = await Dialogs.PromptAsync("Push to remote", "Branch", CurrentBranch).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(branch))
        {
            return;
        }

        await PushWithRecoveryAsync("Push", new PushRequest(remote, branch, null, SetUpstream: true, Force: false),
            canPull: string.Equals(branch, CurrentBranch, StringComparison.Ordinal)).ConfigureAwait(true);
    }

    private async Task FetchAllAsync()
    {
        IReadOnlyDictionary<string, string> remotes = await Handler.GetRemotesAsync(Path).ConfigureAwait(true);
        if (remotes.Count == 0)
        {
            await Dialogs.ShowMessageAsync("Fetch", "No remotes are configured.").ConfigureAwait(true);
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
                await Dialogs.ShowMessageAsync("Fetch all remotes", string.Join("\n\n", failures)).ConfigureAwait(true);
            }
            else
            {
                StatusMessage = $"Fetched {remotes.Count} remote(s).";
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
            (string Remote, string Target)? prompt = await PromptDetachedTargetAsync("Force push").ConfigureAwait(true);
            if (prompt is null)
            {
                return;
            }

            await PushWithRecoveryAsync("Force push",
                new PushRequest(prompt.Value.Remote, null, prompt.Value.Target, SetUpstream: false, Force: true),
                canPull: false).ConfigureAwait(true);
            return;
        }

        await PushWithRecoveryAsync("Force push", new PushRequest(null, branch, null, SetUpstream: true, Force: true),
            canPull: true).ConfigureAwait(true);
    }

    private async Task<(string Remote, string Target)?> PromptDetachedTargetAsync(string title)
    {
        IReadOnlyDictionary<string, string> remotes = await Handler.GetRemotesAsync(Path).ConfigureAwait(true);
        if (remotes.Count == 0)
        {
            await Dialogs.ShowMessageAsync(title, "No remotes are configured.").ConfigureAwait(true);
            return null;
        }

        var remote = await Dialogs.PromptAsync(title, "Remote",
            remotes.ContainsKey("origin") ? "origin" : remotes.Keys.First()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(remote))
        {
            return null;
        }

        var target = await Dialogs.PromptAsync(title, "Remote branch", string.Empty).ConfigureAwait(true);
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
            (string Remote, string Target)? prompt = await PromptDetachedTargetAsync("Push").ConfigureAwait(true);
            if (prompt is null)
            {
                return;
            }

            await PushWithRecoveryAsync("Push",
                new PushRequest(prompt.Value.Remote, null, prompt.Value.Target, SetUpstream: false, Force: false),
                canPull: false).ConfigureAwait(true);
            return;
        }

        await PushWithRecoveryAsync("Push", new PushRequest(null, branch, null, SetUpstream: false, Force: false),
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
                    StatusMessage = $"{name} completed.";
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
                                StatusMessage = $"{name} rejected: the remote branch has commits that are not in yours.";
                                return;
                            }

                            if (choice is PullRebaseThenPush or PullMergeThenPush)
                            {
                                StatusMessage = choice == PullRebaseThenPush ? "Pulling with rebase..." : "Pulling...";
                                ProcessResult pull = await Handler.PullAsync(Path, rebase: choice == PullRebaseThenPush).ConfigureAwait(true);
                                if (!pull.Success)
                                {
                                    await Dialogs.ShowMessageAsync("Pull failed",
                                        "The pull did not complete, so nothing was pushed. If there are conflicts, resolve them " +
                                        "(or abort) and push again.\n\n" + pull.Combined).ConfigureAwait(true);
                                    StatusMessage = "Pull failed; nothing was pushed.";
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
                                StatusMessage = "Force push with lease...";
                                request = request with { Force = true };
                                result = await RunPushAsync(request).ConfigureAwait(true);
                                continue;
                            }

                            // Fetch only / fetch to review: bring the remote commits in and stop.
                            ProcessResult fetch = await Handler.FetchAsync(Path, request.Remote).ConfigureAwait(true);
                            if (!fetch.Success)
                            {
                                await Dialogs.ShowMessageAsync("Fetch failed", fetch.Combined).ConfigureAwait(true);
                            }

                            StatusMessage = fetch.Success
                                ? "Fetched. Review the incoming commits in the history, then push again."
                                : "Fetch failed.";
                            await ReloadAllCoreAsync().ConfigureAwait(true);
                            return;
                        }

                    case PushRejection.RemoteRejected:
                        {
                            var reason = outcome.RejectedRef?.Reason ?? "rejected";
                            var server = outcome.RemoteMessage is null ? string.Empty : $"\n\nThe server said:\n{outcome.RemoteMessage}";
                            await Dialogs.ShowMessageAsync($"{name} rejected by the server",
                                $"The remote refused the update ({reason}). This usually means the branch is protected or a " +
                                $"server-side check failed; pushing again will not help until that is resolved.{server}")
                                .ConfigureAwait(true);
                            break;
                        }

                    case PushRejection.Authentication:
                        await Dialogs.ShowMessageAsync($"{name} failed: authentication",
                            "The remote did not accept your credentials. Check the credential helper or SSH command in " +
                            "Settings, and that your SSH key or access token is valid.\n\n" + outcome.RawOutput)
                            .ConfigureAwait(true);
                        break;

                    default:
                        await Dialogs.ShowMessageAsync($"{name} failed", outcome.RawOutput).ConfigureAwait(true);
                        break;
                }

                StatusMessage = $"{name} failed.";
                await LoadStatusCoreAsync().ConfigureAwait(true);
                return;
            }

            StatusMessage = $"{name} stopped after several attempts; the remote keeps changing.";
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
            : request.Branch ?? request.DetachedTarget ?? "the branch";

        string message;
        var options = new List<string>();
        switch (outcome.Rejection)
        {
            case PushRejection.FetchFirst:
                // The remote's commits have not been fetched, so the user has never
                // seen them: forcing would discard unseen work. No force option here.
                message = $"The remote '{target}' has commits that you have not fetched yet, so your push was rejected.\n\n" +
                          "Pull them into your branch and push again, or fetch them first to review.";
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
                    var what = count is { } n ? $"{n} commit{(n == 1 ? string.Empty : "s")}" : "commits";
                    message = $"The remote '{target}' has {what} that your branch does not contain (your branch is behind or " +
                              "has diverged), so your push was rejected.\n\n" +
                              (canPull ? "Integrate them and push again, or force push" : "Force push") +
                              $" to replace the remote branch with yours, which discards those {what} from the remote. " +
                              "Force with lease still refuses if someone pushes again in the meantime.";
                    if (canPull)
                    {
                        options.Add(PullRebaseThenPush);
                        options.Add(PullMergeThenPush);
                    }
                    options.Add(ForcePushWithLease);
                    break;
                }

            default: // StaleLease
                message = $"The force push was refused: someone pushed to '{target}' after your last fetch, so " +
                          "overwriting it could discard work you have not seen.\n\n" +
                          "Fetch to review their commits, then integrate them or force push again.";
                options.Add(FetchToReview);
                break;
        }

        return await Dialogs.ChooseAsync($"{name} rejected", message, options).ConfigureAwait(true);
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
                await Dialogs.ShowMessageAsync($"{name} failed", result.Combined).ConfigureAwait(true);
                StatusMessage = $"{name} failed.";
                // A pull/merge/rebase can fail because of conflicts; refresh so the
                // conflict banner and abort/continue controls become visible.
                await LoadStatusCoreAsync().ConfigureAwait(true);
            }
            else
            {
                StatusMessage = $"{name} completed.";
                await ReloadAllCoreAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await Dialogs.ShowMessageAsync($"{name} failed", ex.Message).ConfigureAwait(true);
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
        (string? Value, bool Flag)? prompt = await Dialogs.PromptWithCheckBoxAsync("Create branch", "Branch name", string.Empty, "Checkout after creating", true).ConfigureAwait(true);
        if (prompt is null || string.IsNullOrWhiteSpace(prompt.Value.Value))
        {
            return;
        }

        ProcessResult result = await Handler.CreateBranchAsync(Path, prompt.Value.Value, start, prompt.Value.Flag).ConfigureAwait(true);
        await HandleOperationResultAsync("Create branch", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task CreateTagAsync()
    {
        (string? Value, bool Flag)? prompt = await Dialogs.PromptWithCheckBoxAsync("Create tag", "Tag name", string.Empty, "Annotated tag", false).ConfigureAwait(true);
        if (prompt is null || string.IsNullOrWhiteSpace(prompt.Value.Value))
        {
            return;
        }

        string? message = null;
        if (prompt.Value.Flag)
        {
            message = await Dialogs.PromptAsync("Tag message", "Message", string.Empty).ConfigureAwait(true);
            if (message is null)
            {
                return;
            }
        }

        var revision = SelectedCommit?.Row.CommitId ?? "HEAD";
        ProcessResult result = await Handler.CreateTagAsync(Path, prompt.Value.Value, revision, message).ConfigureAwait(true);
        await HandleOperationResultAsync("Create tag", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task StashAsync()
    {
        (string? Value, bool Flag)? prompt = await Dialogs.PromptWithCheckBoxAsync("Stash changes", "Stash message", string.Empty, "Include untracked files", true).ConfigureAwait(true);
        if (prompt is null)
        {
            return;
        }

        ProcessResult result = await Handler.StashSaveAsync(Path, prompt.Value.Value ?? string.Empty, prompt.Value.Flag).ConfigureAwait(true);
        await HandleOperationResultAsync("Stash", result, reloadAll: false).ConfigureAwait(true);
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
            if (await Dialogs.ConfirmAsync("Branch is in another worktree",
                    $"'{branch.Name}' is checked out in the worktree at\n{worktree}\n\nOpen that worktree?").ConfigureAwait(true))
            {
                await OpenWorktreePathAsync(worktree).ConfigureAwait(true);
            }

            return;
        }

        ProcessResult result = await Handler.CheckoutAsync(Path, branch.Name).ConfigureAwait(true);
        await HandleOperationResultAsync("Checkout", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task CheckoutReferenceAsync(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync("Checkout", $"Check out '{reference}'? HEAD may become detached.").ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.CheckoutAsync(Path, reference).ConfigureAwait(true);
        await HandleOperationResultAsync("Checkout", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task DeleteBranchAsync(BranchRef? branch)
    {
        if (branch is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync("Delete branch", $"Delete branch '{branch.Name}'?").ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.DeleteBranchAsync(Path, branch.Name, force: false).ConfigureAwait(true);
        await HandleOperationResultAsync("Delete branch", result, reloadAll: true).ConfigureAwait(true);
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
            await Dialogs.ShowMessageAsync("Create worktree",
                $"'{branch.Name}' is already checked out in {branch.WorktreePath ?? "this worktree"}.").ConfigureAwait(true);
            return;
        }

        var branchName = branch?.Name;
        if (branchName is null)
        {
            branchName = (await Dialogs.PromptAsync("New worktree", "Name of the new branch (starts at HEAD)").ConfigureAwait(true))?.Trim();
            if (string.IsNullOrEmpty(branchName))
            {
                return;
            }
        }

        var folder = (await Dialogs.PromptAsync("New worktree", $"Folder for '{branchName}'", SuggestWorktreePath(branchName))
            .ConfigureAwait(true))?.Trim();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        ProcessResult result = await Handler.AddWorktreeAsync(Path, folder, branchName, createBranch: branch is null, startPoint: null)
            .ConfigureAwait(true);
        await HandleOperationResultAsync("Create worktree", result, reloadAll: true).ConfigureAwait(true);
        if (result.Success
            && await Dialogs.ConfirmAsync("Worktree created", $"Created a worktree for '{branchName}' at\n{folder}\n\nOpen it in a new tab?")
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

        if (!await Dialogs.ConfirmAsync("Remove worktree",
                $"Remove the worktree at\n{worktree.Path}\n\nIts folder is deleted. The branch '{worktree.BranchText}' is kept.").ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.RemoveWorktreeAsync(Path, worktree.Path, force: false).ConfigureAwait(true);
        // git refuses to delete uncommitted work without --force; ask before discarding it.
        if (!result.Success && result.Error.Contains("modified or untracked files", StringComparison.OrdinalIgnoreCase))
        {
            if (!await Dialogs.ConfirmAsync("Remove worktree",
                    "The worktree has uncommitted changes or untracked files. Remove it anyway and lose them?").ConfigureAwait(true))
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

        await HandleOperationResultAsync("Remove worktree", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task PruneWorktreesAsync()
    {
        ProcessResult result = await Handler.PruneWorktreesAsync(Path).ConfigureAwait(true);
        await HandleOperationResultAsync("Prune worktrees", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task DeleteTagAsync(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync("Delete tag", $"Delete tag '{tag}'?").ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.DeleteTagAsync(Path, tag).ConfigureAwait(true);
        await HandleOperationResultAsync("Delete tag", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task CheckoutCommitAsync()
    {
        if (SelectedCommit is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync("Checkout commit", $"Check out {SelectedCommit.Row.ShortId}? HEAD will be detached.").ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.CheckoutAsync(Path, SelectedCommit.Row.CommitId).ConfigureAwait(true);
        await HandleOperationResultAsync("Checkout", result, reloadAll: true).ConfigureAwait(true);
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
            await Dialogs.ShowMessageAsync("Cherry-pick",
                "Merge commits cannot be cherry-picked. Select only non-merge commits.").ConfigureAwait(true);
            return;
        }
        if (commits.Count > 1
            && !await Dialogs.ConfirmAsync("Cherry-pick",
                $"Cherry-pick {commits.Count} commits onto the current branch?").ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = commits.Count > 1 ? $"Cherry-picking {commits.Count} commits..." : "Cherry-pick...";
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
                        ? $"{applied} of {commits.Count} commits were applied before the conflict.\n\n{result.Combined}"
                        : result.Combined;
                    await Dialogs.ShowMessageAsync("Cherry-pick failed", detail).ConfigureAwait(true);
                    StatusMessage = commits.Count > 1
                        ? $"Cherry-pick stopped after {applied} of {commits.Count} commits."
                        : "Cherry-pick failed.";
                    await LoadStatusCoreAsync().ConfigureAwait(true);
                    return;
                }
            }

            StatusMessage = commits.Count == 1 ? "Cherry-pick completed." : $"Cherry-picked {commits.Count} commits.";
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
            await Dialogs.ShowMessageAsync("Revert",
                "Merge commits cannot be reverted without choosing a mainline. Select only non-merge commits.").ConfigureAwait(true);
            return;
        }
        if (commits.Count > 1
            && !await Dialogs.ConfirmAsync("Revert",
                $"Revert {commits.Count} commits?").ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = commits.Count > 1 ? $"Reverting {commits.Count} commits..." : "Revert...";
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
                        ? $"{applied} of {commits.Count} commits were reverted before the conflict.\n\n{result.Combined}"
                        : result.Combined;
                    await Dialogs.ShowMessageAsync("Revert failed", detail).ConfigureAwait(true);
                    StatusMessage = commits.Count > 1
                        ? $"Revert stopped after {applied} of {commits.Count} commits."
                        : "Revert failed.";
                    await LoadStatusCoreAsync().ConfigureAwait(true);
                    return;
                }
            }

            StatusMessage = commits.Count == 1 ? "Revert completed." : $"Reverted {commits.Count} commits.";
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

        if (!await Dialogs.ConfirmAsync("Merge", $"Merge {SelectedCommit.Row.ShortId} into the current branch?").ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.MergeAsync(Path, SelectedCommit.Row.CommitId).ConfigureAwait(true);
        await HandleOperationResultAsync("Merge", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task RebaseCommitAsync()
    {
        if (SelectedCommit is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync("Rebase", $"Rebase the current branch onto {SelectedCommit.Row.ShortId}?").ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.RebaseAsync(Path, SelectedCommit.Row.CommitId).ConfigureAwait(true);
        await HandleOperationResultAsync("Rebase", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task ResetCommitAsync()
    {
        if (SelectedCommit is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync("Reset", $"Hard reset the current branch to {SelectedCommit.Row.ShortId}? Uncommitted changes will be lost.").ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.ResetAsync(Path, SelectedCommit.Row.CommitId, hard: true).ConfigureAwait(true);
        await HandleOperationResultAsync("Reset", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task ApplyStashAsync(StashEntry? stash)
    {
        if (stash is null)
        {
            return;
        }

        ProcessResult result = await Handler.StashApplyAsync(Path, stash.Name, pop: false).ConfigureAwait(true);
        await HandleOperationResultAsync("Apply stash", result, reloadAll: false).ConfigureAwait(true);
        await LoadStatusCoreAsync().ConfigureAwait(true);
    }

    private async Task PopStashAsync(StashEntry? stash)
    {
        if (stash is null)
        {
            return;
        }

        ProcessResult result = await Handler.StashApplyAsync(Path, stash.Name, pop: true).ConfigureAwait(true);
        await HandleOperationResultAsync("Pop stash", result, reloadAll: true).ConfigureAwait(true);
        await LoadStatusCoreAsync().ConfigureAwait(true);
    }

    private async Task DropStashAsync(StashEntry? stash)
    {
        if (stash is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync("Drop stash", $"Drop '{stash.Name}'? This cannot be undone.").ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.StashDropAsync(Path, stash.Name).ConfigureAwait(true);
        await HandleOperationResultAsync("Drop stash", result, reloadAll: false).ConfigureAwait(true);
        await LoadRefsCoreAsync().ConfigureAwait(true);
    }

    private async Task HandleOperationResultAsync(string name, ProcessResult result, bool reloadAll)
    {
        if (!result.Success)
        {
            await Dialogs.ShowMessageAsync($"{name} failed", result.Combined).ConfigureAwait(true);
            StatusMessage = $"{name} failed.";
            await LoadStatusCoreAsync().ConfigureAwait(true);
            return;
        }

        StatusMessage = $"{name} completed.";
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
        var name = await Dialogs.PromptAsync("Add remote", "Remote name", defaultName).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (existing.ContainsKey(name))
        {
            await Dialogs.ShowMessageAsync("Add remote", $"A remote named '{name}' already exists.").ConfigureAwait(true);
            return;
        }

        var url = await Dialogs.PromptAsync("Add remote", "Remote URL", string.Empty).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!LooksLikeRemoteUrl(url))
        {
            await Dialogs.ShowMessageAsync("Add remote",
                "Enter an http(s), ssh, git, file URL, scp-style address (git@host:path) or a local path.").ConfigureAwait(true);
            return;
        }

        ProcessResult result = await Handler.AddRemoteAsync(Path, name, url).ConfigureAwait(true);
        await HandleOperationResultAsync("Add remote", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task DeleteRemoteAsync(RemoteEntry? remote)
    {
        if (remote is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync("Remove remote",
                $"Remove remote '{remote.Name}'? Its remote-tracking refs will also be removed.").ConfigureAwait(true))
        {
            return;
        }

        ProcessResult result = await Handler.DeleteRemoteAsync(Path, remote.Name).ConfigureAwait(true);
        await HandleOperationResultAsync("Remove remote", result, reloadAll: true).ConfigureAwait(true);
    }

    private async Task EditRemoteAsync(RemoteEntry? remote)
    {
        if (remote is null)
        {
            return;
        }

        var name = await Dialogs.PromptAsync("Edit remote", "Remote name", remote.Name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        name = name.Trim();
        if (name != remote.Name && Remotes.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            await Dialogs.ShowMessageAsync("Edit remote", $"A remote named '{name}' already exists.").ConfigureAwait(true);
            return;
        }

        var url = await Dialogs.PromptAsync("Edit remote", "Remote URL", remote.Url).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        url = url.Trim();
        if (!LooksLikeRemoteUrl(url))
        {
            await Dialogs.ShowMessageAsync("Edit remote",
                "Enter an http(s), ssh, git, file URL, scp-style address (git@host:path) or a local path.").ConfigureAwait(true);
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
                    await Dialogs.ShowMessageAsync("Rename remote failed", rename.Combined).ConfigureAwait(true);
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
                    await Dialogs.ShowMessageAsync("Update remote failed", setUrl.Combined).ConfigureAwait(true);
                    return;
                }
            }

            StatusMessage = $"Updated remote '{name}'.";
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
            await Dialogs.ShowMessageAsync("Interactive rebase",
                "Finish or abort the current merge/rebase before starting another.").ConfigureAwait(true);
            return;
        }
        if (commit.Row.ParentIds.Length > 1)
        {
            await Dialogs.ShowMessageAsync("Interactive rebase",
                "Interactive rebase over a merge commit is not supported.").ConfigureAwait(true);
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
                    await Dialogs.ShowMessageAsync("Interactive rebase",
                        "The selected commit is not an ancestor of the current HEAD. Check out its branch first.").ConfigureAwait(true);
                }
                else
                {
                    await Dialogs.ShowMessageAsync("Interactive rebase", ancestor.Combined).ConfigureAwait(true);
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
            await Dialogs.ShowMessageAsync("Interactive rebase", ex.Message).ConfigureAwait(true);
            return;
        }

        if (ReportHandlerError("Could not build the rebase todo"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(todo))
        {
            StatusMessage = "No commits to rebase.";
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

        IReadOnlyList<RebasePlanItem>? plan = await Dialogs.EditRebasePlanAsync("Interactive rebase", items).ConfigureAwait(true);
        if (plan is null || plan.Count == 0)
        {
            return;
        }

        RebasePlanItem? firstAction = plan.FirstOrDefault(item => item.Action != "drop");
        if (firstAction is { Action: "squash" or "fixup" })
        {
            await Dialogs.ShowMessageAsync("Interactive rebase",
                "The first non-dropped commit cannot be squash or fixup.").ConfigureAwait(true);
            return;
        }

        var editedTodo = string.Join("\n", plan.Select(item => $"{item.Action} {item.Sha}"));
        var rewordMessages = plan
            .Where(item => item.Action == "reword" && !string.IsNullOrWhiteSpace(item.Message))
            .ToDictionary(item => item.Sha, item => item.Message, StringComparer.Ordinal);

        ProcessResult result = await Handler.InteractiveRebaseAsync(Path, baseRevision, editedTodo, rewordMessages, fromRoot)
            .ConfigureAwait(true);
        await HandleOperationResultAsync("Interactive rebase", result, reloadAll: true).ConfigureAwait(true);
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
        StatusMessage = error ?? "Opened a terminal in the repository folder.";
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
}




