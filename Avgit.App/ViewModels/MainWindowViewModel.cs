using System.Collections.ObjectModel;
using System.Windows.Input;
using Avgit.App.Services;
using Avgit.Git;

namespace Avgit.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private GitManager _gitManager;
    private string _statusMessage = "Open a repository to get started.";
    private readonly Stack<string> _recentlyClosed = new();

    public MainWindowViewModel(AppSettingsService settings, IDialogService dialogs)
    {
        SettingsService = settings;
        _dialogs = dialogs;
        _gitManager = new GitManager(settings.Settings.GitExecutable);
        _gitManager.Git.SshCommand = settings.Settings.GitSshCommand;
        _gitManager.Git.CredentialHelper = settings.Settings.GitCredentialHelper;

        foreach (var recent in settings.Settings.RecentRepositories)
        {
            RecentRepositories.Add(recent);
        }

        OpenRepositoryCommand = new AsyncRelayCommand(OpenRepositoryAsync);
        ShowHomeCommand = new RelayCommand(ShowHome);
        NextRepositoryCommand = new RelayCommand(() => CycleRepository(1));
        PreviousRepositoryCommand = new RelayCommand(() => CycleRepository(-1));
        ReopenClosedRepositoryCommand = new AsyncRelayCommand(ReopenClosedAsync);
        CloseOtherRepositoriesCommand = new RelayCommand(p => CloseOtherRepositories(p as RepositoryViewModel));
        CloseAllRepositoriesCommand = new RelayCommand(CloseAllRepositories);
        CopyRepositoryPathCommand = new RelayCommand(p =>
        {
            if (p is RepositoryViewModel repository)
            {
                CopyText?.Invoke(repository.Path);
                _statusMessage = "Repository path copied.";
                OnPropertyChanged(nameof(HomeStatusMessage));
            }
        });
        CloneRepositoryCommand = new AsyncRelayCommand(CloneRepositoryAsync);
        InitRepositoryCommand = new AsyncRelayCommand(InitRepositoryAsync);
        OpenRecentCommand = new AsyncRelayCommand(p => OpenPathAsync(p as string));
        RemoveRecentCommand = new RelayCommand(p =>
        {
            if (p is string path)
            {
                SettingsService.RemoveRecent(path);
                RecentRepositories.Remove(path);
            }
        });
        CloseRepositoryCommand = new RelayCommand(p => CloseRepository(p as RepositoryViewModel ?? Repository));
        RefreshCommand = new AsyncRelayCommand(() => Repository?.RefreshAsync() ?? Task.CompletedTask);
        SettingsCommand = new AsyncRelayCommand(async () =>
        {
            if (ShowSettingsAction is not null)
            {
                await ShowSettingsAction();
            }
        });
        AboutCommand = new AsyncRelayCommand(() => _dialogs.ShowMessageAsync("Avgit",
            $"Avgit {typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3)}\n\n" +
            "A Git client for Windows, macOS and Linux.\n\n" +
            "Commit graph, unified diff viewer, line-level staging, interactive rebase and repository tools."));
        ExitCommand = new RelayCommand(() => ExitAction?.Invoke());
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);
        RestartToUpdateCommand = new RelayCommand(() => Updates?.ApplyAndRestart());
    }

    public ObservableCollection<string> RecentRepositories { get; } = [];
    public ObservableCollection<RepositoryViewModel> Repositories { get; } = [];
    public AppSettingsService SettingsService { get; }

    public RepositoryViewModel? Repository
    {
        get;
        set
        {
            RepositoryViewModel? previous = field;
            if (SetProperty(ref field, value))
            {
                // Hidden tabs stop refreshing; they catch up when shown again.
                previous?.SetActive(false);
                if (value is not null)
                {
                    IsHomeVisible = false;
                    value.CopyText = text => CopyText?.Invoke(text);
                    value.SetActive(true);
                    // Tabs restored at startup load the first time they are shown.
                    _ = value.LoadAsync();
                    if (!string.Equals(SettingsService.Settings.ActiveRepository, value.Path, StringComparison.Ordinal))
                    {
                        SettingsService.Settings.ActiveRepository = value.Path;
                        SettingsService.Save();
                    }
                }
                else if (Repositories.Count > 0)
                {
                    IsHomeVisible = true;
                }

                OnPropertyChanged(nameof(HasRepository));
                OnPropertyChanged(nameof(ShowRepositoryContent));
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public bool HasRepository => Repositories.Count > 0;
    public bool WelcomeVisible => IsHomeVisible;
    public bool ShowRepositoryContent => HasRepository && Repository is not null && !IsHomeVisible;

    public bool IsHomeVisible
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(WelcomeVisible));
                OnPropertyChanged(nameof(ShowRepositoryContent));
                OnPropertyChanged(nameof(Title));
            }
        }
    } = true;

    public string Title => IsHomeVisible || Repository is null
        ? "Avgit"
        : $"{Repository.Name} - Avgit";

    public string StatusMessage
    {
        get => Repository?.StatusMessage ?? _statusMessage;
        set
        {
            if (Repository is not null)
            {
                Repository.StatusMessage = value;
            }
            else
            {
                SetProperty(ref _statusMessage, value);
            }
        }
    }

    public bool IsBusy
    {
        get => Repository?.IsBusy ?? field;
        set
        {
            if (Repository is not null)
            {
                Repository.IsBusy = value;
            }
            else
            {
                SetProperty(ref field, value);
            }
        }
    }

    public Action? ExitAction { get; set; }
    public Action<string>? CopyText { get; set; }
    public Func<Task>? ShowSettingsAction { get; set; }

    public ICommand OpenRepositoryCommand { get; }
    public ICommand ShowHomeCommand { get; }
    public ICommand NextRepositoryCommand { get; }
    public ICommand PreviousRepositoryCommand { get; }
    public ICommand ReopenClosedRepositoryCommand { get; }
    public ICommand CloseOtherRepositoriesCommand { get; }
    public ICommand CloseAllRepositoriesCommand { get; }
    public ICommand CopyRepositoryPathCommand { get; }
    public ICommand CloneRepositoryCommand { get; }
    public ICommand InitRepositoryCommand { get; }
    public ICommand OpenRecentCommand { get; }
    public ICommand RemoveRecentCommand { get; }
    public ICommand CloseRepositoryCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand RestartToUpdateCommand { get; }

    /// <summary>Set by the app at startup; null in tests and design-time use.</summary>
    public UpdateService? Updates { get; set; }

    /// <summary>Version downloaded and waiting for a restart; null when there is none.</summary>
    public string? UpdateReadyVersion
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsUpdateReady));
                OnPropertyChanged(nameof(UpdateReadyText));
            }
        }
    }

    public bool IsUpdateReady => UpdateReadyVersion is not null;
    public string UpdateReadyText => $"Avgit {UpdateReadyVersion} is ready to install.";

    /// <summary>
    /// Startup check: downloads a newer release quietly when automatic updates are
    /// on. Failures (offline, rate limits) are ignored; the next start tries again.
    /// </summary>
    public async Task CheckForUpdatesInBackgroundAsync()
    {
        if (Updates is null || !SettingsService.Settings.CheckForUpdates)
        {
            return;
        }

        try
        {
            UpdateReadyVersion = await Updates.DownloadLatestAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (Updates is null || !Updates.IsInstalled)
        {
            await _dialogs.ShowMessageAsync("Check for updates",
                "Updates are only available when Avgit was installed from a release (Setup.exe on Windows, the AppImage on Linux).\n\n" +
                $"Releases: {UpdateService.RepositoryUrl}/releases").ConfigureAwait(true);
            return;
        }

        try
        {
            UpdateReadyVersion = await Updates.DownloadLatestAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Check for updates", $"Could not check for updates: {ex.Message}").ConfigureAwait(true);
            return;
        }

        if (UpdateReadyVersion is null)
        {
            await _dialogs.ShowMessageAsync("Check for updates", $"Avgit {Updates.CurrentVersion} is up to date.").ConfigureAwait(true);
        }
        else if (await _dialogs.ConfirmAsync("Update ready",
                     $"Avgit {UpdateReadyVersion} has been downloaded. Restart now to install it?").ConfigureAwait(true))
        {
            Updates.ApplyAndRestart();
        }
    }

    private void CycleRepository(int delta)
    {
        if (Repositories.Count == 0)
        {
            return;
        }

        var index = Repository is null ? -1 : Repositories.IndexOf(Repository);
        if (index < 0)
        {
            index = 0;
        }
        else
        {
            index = (index + delta + Repositories.Count) % Repositories.Count;
        }

        Repository = Repositories[index];
        HideHome();
    }

    private void ShowHome()
    {
        // Keep the last repository selected so its tab stays highlighted; the
        // content pane switches to the home/recent screen instead.
        IsHomeVisible = true;
        _statusMessage = "Select a recent repository or open another.";
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HomeStatusMessage));
        OnPropertyChanged(nameof(ShowRepositoryContent));
        OnPropertyChanged(nameof(Title));
    }

    public void HideHome()
    {
        if (!IsHomeVisible)
        {
            return;
        }

        IsHomeVisible = false;
        OnPropertyChanged(nameof(ShowRepositoryContent));
        OnPropertyChanged(nameof(Title));
    }

    public string HomeStatusMessage => _statusMessage;

    [ObservableProperty]
    public partial bool IsHomeBusy { get; private set; }

    private void SaveOpenTabs()
    {
        SettingsService.Settings.OpenRepositories = [.. Repositories.Select(r => r.Path)];
        SettingsService.Save();
    }

    private async Task ReopenClosedAsync()
    {
        while (_recentlyClosed.Count > 0)
        {
            var path = _recentlyClosed.Pop();
            if (Directory.Exists(path))
            {
                await OpenPathAsync(path);
                return;
            }
        }
        _statusMessage = "No recently closed repository.";
        OnPropertyChanged(nameof(HomeStatusMessage));
    }

    /// <summary>
    /// Re-creates the tabs open at the last exit. All tabs appear immediately; the
    /// tab that was active is selected and loads, the others load when first shown
    /// (previously each tab loaded fully before the next one even appeared).
    /// </summary>
    public async Task RestoreOpenTabsAsync()
    {
        if (Repositories.Count > 0)
        {
            return;
        }

        foreach (var path in SettingsService.Settings.OpenRepositories.ToList())
        {
            if (!Directory.Exists(path) || !GitManager.IsRepositoryRoot(path))
            {
                continue;
            }

            var root = CanonicalPath(path);
            if (Repositories.Any(r => string.Equals(CanonicalPath(r.Path), root, AppSettingsService.PathComparison)))
            {
                continue;
            }

            Repositories.Add(CreateRepository(root));
        }

        if (Repositories.Count == 0)
        {
            return;
        }

        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(WelcomeVisible));
        var saved = SettingsService.Settings.ActiveRepository;
        RepositoryViewModel active = Repositories.FirstOrDefault(r => saved is not null
                         && string.Equals(CanonicalPath(r.Path), CanonicalPath(saved), AppSettingsService.PathComparison))
                     ?? Repositories[0];
        Repository = active;
        await active.LoadAsync();
    }

    private RepositoryViewModel CreateRepository(string root) => new(_gitManager.CreateEngine(), root, SettingsService, _dialogs)
    {
        CopyText = text => CopyText?.Invoke(text),
        OpenRepositoryAction = OpenPathAsync,
        WorktreeRemoved = CloseRemovedWorktree
    };

    /// <summary>Closes the tab of a worktree that was just removed; its folder is gone.</summary>
    private void CloseRemovedWorktree(string folder)
    {
        var root = CanonicalPath(folder);
        RepositoryViewModel? tab = Repositories.FirstOrDefault(r =>
            string.Equals(CanonicalPath(r.Path), root, AppSettingsService.PathComparison));
        if (tab is not null)
        {
            CloseRepository(tab);
        }
    }

    private void CloseOtherRepositories(RepositoryViewModel? keep)
    {
        if (keep is null)
        {
            return;
        }

        foreach (RepositoryViewModel? repository in Repositories.Where(r => !ReferenceEquals(r, keep)).ToList())
        {
            _recentlyClosed.Push(repository.Path);
            Repositories.Remove(repository);
            repository.Dispose();
        }
        Repository = keep;
        SaveOpenTabs();
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(ShowRepositoryContent));
    }

    private void CloseAllRepositories()
    {
        foreach (RepositoryViewModel repository in Repositories)
        {
            _recentlyClosed.Push(repository.Path);
            repository.Dispose();
        }
        Repositories.Clear();
        SaveOpenTabs();
        Repository = null;
        IsHomeVisible = true;
        _statusMessage = "Open a repository to get started.";
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(WelcomeVisible));
        OnPropertyChanged(nameof(ShowRepositoryContent));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HomeStatusMessage));
        OnPropertyChanged(nameof(Title));
    }

    private void CloseRepository(RepositoryViewModel? repository)
    {
        if (repository is null)
        {
            return;
        }

        var index = Repositories.IndexOf(repository);
        if (index < 0)
        {
            return;
        }

        var wasActive = ReferenceEquals(Repository, repository);
        var wasHome = IsHomeVisible;
        Repositories.RemoveAt(index);
        repository.Dispose();
        if (Repositories.Count == 0)
        {
            Repository = null;
            IsHomeVisible = true;
            _statusMessage = "Open a repository to get started.";
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(HomeStatusMessage));
        }
        else if (wasActive)
        {
            Repository = Repositories[Math.Min(index, Repositories.Count - 1)];
        }

        _recentlyClosed.Push(repository.Path);
        SaveOpenTabs();
        if (wasHome && Repositories.Count > 0)
        {
            IsHomeVisible = true;
        }

        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(WelcomeVisible));
        OnPropertyChanged(nameof(ShowRepositoryContent));
        OnPropertyChanged(nameof(Title));
    }

    private async Task OpenRepositoryAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Open repository");
        if (folder is not null)
        {
            await OpenPathAsync(folder);
        }
    }

    public async Task OpenPathAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var root = CanonicalPath(GitManager.FindRepositoryRoot(path) ?? path);
        RepositoryViewModel? existing = Repositories.FirstOrDefault(r =>
            string.Equals(CanonicalPath(r.Path), root, AppSettingsService.PathComparison));
        if (existing is not null)
        {
            Repository = existing;
            IsHomeVisible = false;
            await existing.LoadAsync();
            return;
        }

        if (!GitManager.IsRepositoryRoot(root))
        {
            await _dialogs.ShowMessageAsync("Not a repository",
                $"'{root}' does not contain a .git directory.");
            return;
        }

        RepositoryViewModel repo = CreateRepository(root);

        repo.IsBusy = true;
        repo.StatusMessage = $"Opening {root}...";
        try
        {
            Repositories.Add(repo);
            Repository = repo;
            SaveOpenTabs();
            OnPropertyChanged(nameof(HasRepository));
            OnPropertyChanged(nameof(WelcomeVisible));
            SettingsService.AddRecent(root);
            if (!RecentRepositories.Contains(root, AppSettingsService.PathComparer))
            {
                RecentRepositories.Insert(0, root);
            }

            await repo.LoadAsync();
            repo.StatusMessage = repo.SummaryText;
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Could not open repository", ex.ToString());
            repo.StatusMessage = ex.Message;
        }
        finally
        {
            repo.IsBusy = false;
        }
    }

    private async Task CloneRepositoryAsync()
    {
        var url = await _dialogs.PromptAsync("Clone repository", "Repository URL", string.Empty);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        (string? Value, bool Flag)? options = await _dialogs.PromptWithCheckBoxAsync("Clone repository",
            "Additional git clone options (optional, e.g. --depth 1 --branch dev)", string.Empty, "Partial clone (blobless)", false);
        if (options is null)
        {
            return;
        }

        var parent = await _dialogs.PickFolderAsync("Choose a folder for the clone");
        if (parent is null)
        {
            return;
        }

        var name = await _dialogs.PromptAsync("Clone repository", "Folder name", GuessNameFromUrl(url));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var destination = System.IO.Path.Combine(parent, name);
        if (System.IO.Directory.Exists(destination) && System.IO.Directory.EnumerateFileSystemEntries(destination).Any())
        {
            await _dialogs.ShowMessageAsync("Clone failed", "The destination folder is not empty.");
            return;
        }

        RepositoryViewModel? previous = Repository;
        IsHomeBusy = true;
        _statusMessage = "Cloning...";
        OnPropertyChanged(nameof(HomeStatusMessage));
        previous?.StatusMessage = "Cloning...";

        try
        {
            IReadOnlyList<string> extraOptions = SplitCommandLine(options.Value.Value ?? string.Empty);
            ProcessResult result = await _gitManager.Git.CloneAsync(url, destination, options.Value.Flag, extraOptions);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Clone failed", result.Combined);
                _statusMessage = "Clone failed.";
                OnPropertyChanged(nameof(HomeStatusMessage));
                return;
            }

            await OpenPathAsync(destination);
        }
        finally
        {
            IsHomeBusy = false;
            if (previous is not null && !ReferenceEquals(previous, Repository))
            {
                previous.StatusMessage = previous.SummaryText;
            }
        }
    }

    private async Task InitRepositoryAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Create repository in folder");
        if (folder is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Create repository", $"Create a new Git repository in '{folder}'?"))
        {
            return;
        }

        RepositoryViewModel? previous = Repository;
        IsHomeBusy = true;
        _statusMessage = "Creating repository...";
        OnPropertyChanged(nameof(HomeStatusMessage));
        previous?.StatusMessage = "Creating repository...";

        try
        {
            ProcessResult result = await _gitManager.Git.InitAsync(folder);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Create repository failed", result.Combined);
                _statusMessage = "Create repository failed.";
                OnPropertyChanged(nameof(HomeStatusMessage));
                return;
            }

            await OpenPathAsync(folder);
        }
        finally
        {
            IsHomeBusy = false;
            if (previous is not null && !ReferenceEquals(previous, Repository))
            {
                previous.StatusMessage = previous.SummaryText;
            }
        }
    }

    public void ReloadGit()
    {
        _gitManager = new GitManager(SettingsService.Settings.GitExecutable);
        _gitManager.Git.SshCommand = SettingsService.Settings.GitSshCommand;
        _gitManager.Git.CredentialHelper = SettingsService.Settings.GitCredentialHelper;
        foreach (RepositoryViewModel repository in Repositories)
        {
            repository.SetGitExecutable(SettingsService.Settings.GitExecutable);
            repository.SetGitEnvironment();
            repository.SetAutoRefresh(SettingsService.Settings.AutoRefresh);
            // Settings such as the untracked-files mode change what status shows.
            if (repository.IsLoaded)
            {
                _ = repository.RefreshAsync();
            }
        }
    }

    private static string CanonicalPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string GuessNameFromUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/', '\\');
        // ':' also separates host and path in scp-style addresses (git@host:repo.git).
        var slash = Math.Max(Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\')), trimmed.LastIndexOf(':'));
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Splits extra clone options on whitespace, honouring double quotes.</summary>
    internal static IReadOnlyList<string> SplitCommandLine(string text)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var hasToken = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                }

                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(ch);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}





