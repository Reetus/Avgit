using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using Avgit.Core.Model;

namespace Avgit.App.ViewModels;

/// <summary>Local branches plus the names fully merged into HEAD, read for one listing.</summary>
/// <param name="Error">Why fetching or reading failed (the list may still show local data), or null.</param>
public sealed record TrimBranchesSnapshot(IReadOnlyList<BranchRef> Branches, IReadOnlyCollection<string> Merged, string? Error);

/// <summary>One choice of the "Last commit older than" filter; 0 days means any age. Logic uses only <see cref="Days"/>.</summary>
public sealed record TrimAgeOption(int Days, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A branch offered for deletion.</summary>
public sealed partial class TrimBranchRow : ObservableObject
{
    public TrimBranchRow(BranchRef branch, bool isMerged, DateTimeOffset now)
    {
        Branch = branch;
        IsMerged = isMerged;
        LastCommitText = branch.LastCommitDate is { } date ? FormatDate(date, now) : string.Empty;
    }

    public BranchRef Branch { get; }
    public string Name => Branch.Name;
    public string Sha => Branch.Target;

    /// <summary>Long enough to stay unambiguous for <c>git branch &lt;name&gt; &lt;sha&gt;</c> in large repositories.</summary>
    public string ShortSha => Sha.Length > 10 ? Sha[..10] : Sha;

    public string LastCommitText { get; }

    public string Details => string.IsNullOrEmpty(Branch.LastCommitAuthor)
        ? Branch.LastCommitSubject
        : string.Format(Strings.Trim_RowDetails, Branch.LastCommitAuthor, Branch.LastCommitSubject);

    public string UpstreamText => Branch.Upstream is { Gone: true } tracking
        ? string.Format(Strings.Trim_UpstreamDeleted, tracking.RemoteBranchForPull)
        : Strings.Trim_NeverPushed;

    /// <summary>A deleted remote branch is struck through; "never pushed" is plain.</summary>
    public TextDecorationCollection? UpstreamDecorations => Branch.Upstream is { Gone: true } ? TextDecorations.Strikethrough : null;

    /// <summary>Fully merged into the current branch, so <c>git branch -d</c> can delete it.</summary>
    public bool IsMerged { get; }
    public bool IsNotMerged => !IsMerged;

    public string StatusToolTip => IsMerged ? Strings.Trim_MergedToolTip : Strings.Trim_NotMergedToolTip;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    /// <summary>"3 days ago" for recent commits, otherwise the date.</summary>
    public static string FormatDate(DateTimeOffset date, DateTimeOffset now)
    {
        TimeSpan age = now - date;
        if (age < TimeSpan.FromMinutes(1))
        {
            return Strings.Trim_JustNow;
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Ago((int)age.TotalMinutes, Strings.Trim_MinuteAgo, Strings.Trim_MinutesAgo);
        }

        if (age < TimeSpan.FromDays(1))
        {
            return Ago((int)age.TotalHours, Strings.Trim_HourAgo, Strings.Trim_HoursAgo);
        }

        return age < TimeSpan.FromDays(30)
            ? Ago((int)age.TotalDays, Strings.Trim_DayAgo, Strings.Trim_DaysAgo)
            : date.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string Ago(int count, string one, string many) => count == 1 ? one : string.Format(many, count);
}

/// <summary>
/// The "Trim branches" dialog: lists local branches whose remote branch was
/// deleted (and optionally ones never pushed), filtered by the age of their last
/// commit. Ticks survive filter changes; the window only binds to this.
/// </summary>
public sealed partial class TrimBranchesViewModel : ObservableObject
{
    private readonly Func<bool, Task<TrimBranchesSnapshot>> _load;
    private readonly Func<DateTimeOffset> _now;
    // Every candidate by name, including ones the filter hides, so unticking a
    // row and then changing the filter back does not tick it again.
    private readonly Dictionary<string, TrimBranchRow> _candidates = new(StringComparer.Ordinal);
    private int _loadGeneration;
    private bool _fetching;

    /// <param name="currentBranch">Shown in the description; merged means merged into this.</param>
    /// <param name="olderThanDays">Initial age filter; the nearest option is chosen.</param>
    /// <param name="load">Reads the branches, fetching first when its argument is true.</param>
    /// <param name="now">Clock for the age filter and relative dates (tests pass a fixed one).</param>
    public TrimBranchesViewModel(string currentBranch, int olderThanDays, Func<bool, Task<TrimBranchesSnapshot>> load,
        Func<DateTimeOffset>? now = null)
    {
        CurrentBranch = currentBranch;
        _load = load;
        _now = now ?? (() => DateTimeOffset.Now);
        SelectedAge = AgeOptions.LastOrDefault(o => o.Days <= olderThanDays) ?? AgeOptions[0];
    }

    public static IReadOnlyList<TrimAgeOption> AgeOptions { get; } =
    [
        new(0, Strings.Trim_AgeAny),
        new(1, Strings.Trim_Age1Day),
        new(7, Strings.Trim_Age7Days),
        new(30, Strings.Trim_Age30Days),
        new(90, Strings.Trim_Age90Days)
    ];

    public string CurrentBranch { get; }

    public string Description => string.Format(Strings.Trim_Description, CurrentBranch);

    public ObservableCollection<TrimBranchRow> Rows { get; } = [];

    /// <summary>Run <c>git fetch --prune</c> before listing, so branches deleted on the remote show as gone.</summary>
    [ObservableProperty]
    public partial bool FetchFirst { get; set; } = true;

    [ObservableProperty]
    public partial TrimAgeOption SelectedAge { get; set; }

    /// <summary>Also list branches without an upstream (never pushed).</summary>
    [ObservableProperty]
    public partial bool IncludeNeverPushed { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsEmpty => !IsLoading && Rows.Count == 0;

    public string EmptyText => IncludeNeverPushed
        ? Strings.Trim_EmptyWithNeverPushed
        : Strings.Trim_EmptyGone;

    public string LoadingText => _fetching ? Strings.Trim_Fetching : Strings.Trim_ReadingBranches;

    public int SelectedCount => Rows.Count(r => r.IsSelected);
    public string DeleteText => SelectedCount == 1 ? Strings.Trim_DeleteOne : string.Format(Strings.Trim_DeleteMany, SelectedCount);
    public bool CanDelete => SelectedCount > 0 && !IsLoading;

    /// <summary>The ticked rows that the filter shows.</summary>
    public IReadOnlyList<TrimBranchRow> SelectedRows => [.. Rows.Where(r => r.IsSelected)];

    /// <summary>Set by the window when Delete is clicked.</summary>
    public bool Accepted { get; set; }

    /// <summary>The most recent load (completes when the list is up to date).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the branches, fetching first when <see cref="FetchFirst"/> is on.</summary>
    public Task LoadAsync()
    {
        Loading = LoadCoreAsync(FetchFirst);
        return Loading;
    }

    /// <summary>
    /// Whether a branch can be offered at all: a local branch not checked out
    /// here or in another worktree, whose upstream is gone (or, when asked, that
    /// has no upstream). Branches with a live upstream are never offered.
    /// </summary>
    public static bool IsCandidate(BranchRef branch, bool includeNeverPushed)
    {
        if (branch.Kind != RefKind.Branch || branch.IsCheckedOut || branch.WorktreePath is not null)
        {
            return false;
        }

        return branch.Upstream is { } tracking ? tracking.Gone : includeNeverPushed;
    }

    /// <summary>Whether the last commit is at least <paramref name="days"/> old (always, for 0).</summary>
    public static bool IsOldEnough(BranchRef branch, int days, DateTimeOffset now)
        => days <= 0 || (branch.LastCommitDate is { } date && now - date >= TimeSpan.FromDays(days));

    private async Task LoadCoreAsync(bool fetch)
    {
        var generation = ++_loadGeneration;
        IsLoading = true;
        _fetching = fetch;
        OnPropertyChanged(nameof(LoadingText));
        RaiseCounts();
        TrimBranchesSnapshot snapshot;
        try
        {
            snapshot = await _load(fetch).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            snapshot = new TrimBranchesSnapshot([], [], ex.Message);
        }

        if (generation != _loadGeneration)
        {
            return;
        }

        var merged = new HashSet<string>(snapshot.Merged, StringComparer.Ordinal);
        DateTimeOffset now = _now();
        var previous = new Dictionary<string, TrimBranchRow>(_candidates, StringComparer.Ordinal);
        foreach (TrimBranchRow row in _candidates.Values)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        _candidates.Clear();
        foreach (BranchRef branch in snapshot.Branches)
        {
            if (!IsCandidate(branch, includeNeverPushed: true))
            {
                continue;
            }

            var row = new TrimBranchRow(branch, merged.Contains(branch.Name), now)
            {
                IsSelected = !previous.TryGetValue(branch.Name, out TrimBranchRow? old) || old.IsSelected
            };
            row.PropertyChanged += OnRowChanged;
            _candidates[branch.Name] = row;
        }

        Error = snapshot.Error;
        _fetching = false;
        IsLoading = false;
        Rebuild();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrimBranchRow.IsSelected))
        {
            RaiseCounts();
        }
    }

    /// <summary>Shows the candidates that pass the current filter, oldest last commit first.</summary>
    private void Rebuild()
    {
        DateTimeOffset now = _now();
        Rows.Clear();
        foreach (TrimBranchRow row in _candidates.Values
                     .Where(r => IsCandidate(r.Branch, IncludeNeverPushed) && IsOldEnough(r.Branch, SelectedAge.Days, now))
                     .OrderBy(r => r.Branch.LastCommitDate ?? DateTimeOffset.MinValue)
                     .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(EmptyText));
        RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(CanDelete));
    }

    partial void OnFetchFirstChanged(bool value)
    {
        // Unticking keeps what was already fetched; ticking again fetches now.
        if (value)
        {
            _ = LoadAsync();
        }
    }

    partial void OnSelectedAgeChanged(TrimAgeOption value) => Rebuild();

    partial void OnIncludeNeverPushedChanged(bool value) => Rebuild();

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
