using System.Text;
using System.Windows.Input;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.App.ViewModels;

/// <summary>
/// "Trim branches": deletes local branches whose remote branch is gone, such as
/// pull request branches that GitHub deleted after a (squash) merge.
/// </summary>
public sealed partial class RepositoryViewModel
{
    public ICommand TrimBranchesCommand => field ??= new AsyncRelayCommand(TrimBranchesAsync);

    internal async Task TrimBranchesAsync()
    {
        var target = CurrentBranch == RepoSummary.DetachedHeadLabel || string.IsNullOrEmpty(CurrentBranch)
            ? "HEAD"
            : CurrentBranch;
        var fetched = false;
        var dialog = new TrimBranchesViewModel(target, Settings.Settings.TrimBranchesOlderThanDays, async fetch =>
        {
            TrimBranchesSnapshot snapshot = await LoadTrimBranchesAsync(fetch).ConfigureAwait(true);
            fetched |= fetch;
            return snapshot;
        });
        _ = dialog.LoadAsync();

        var accepted = await Dialogs.TrimBranchesAsync(dialog).ConfigureAwait(true);
        await dialog.Loading.ConfigureAwait(true);
        if (Settings.Settings.TrimBranchesOlderThanDays != dialog.SelectedAge.Days)
        {
            Settings.Settings.TrimBranchesOlderThanDays = dialog.SelectedAge.Days;
            Settings.Save();
        }

        IReadOnlyList<TrimBranchRow> rows = accepted ? dialog.SelectedRows : [];
        var unmerged = rows.Count(r => !r.IsMerged);
        if (unmerged > 0
            && !await Dialogs.ConfirmAsync(Strings.Repo_TrimUnmergedTitle, unmerged == 1
                    ? string.Format(Strings.Repo_TrimUnmergedConfirmOne, target)
                    : string.Format(Strings.Repo_TrimUnmergedConfirmMany, unmerged, target))
                .ConfigureAwait(true))
        {
            rows = [];
        }

        if (rows.Count == 0)
        {
            // A fetch from the dialog still changed the remote branches.
            if (fetched)
            {
                await RefreshAfterTrimAsync().ConfigureAwait(true);
            }

            return;
        }

        var deleted = new List<TrimBranchRow>();
        var failures = new List<(string Name, string Reason)>();
        IsBusy = true;
        StatusMessage = Strings.Repo_StatusTrimDeleting;
        try
        {
            foreach (TrimBranchRow row in rows)
            {
                // Merged branches use -d so git still refuses if that changed meanwhile.
                ProcessResult result = await Handler.DeleteBranchAsync(Path, row.Name, force: !row.IsMerged).ConfigureAwait(true);
                if (result.Success)
                {
                    deleted.Add(row);
                }
                else
                {
                    failures.Add((row.Name, FirstLine(result.Combined)));
                }
            }

            await ReloadAllCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        StatusMessage = failures.Count > 0
            ? string.Format(Strings.Repo_StatusTrimPartial, deleted.Count, failures.Count)
            : deleted.Count == 1
                ? Strings.Repo_StatusTrimDeletedOne
                : string.Format(Strings.Repo_StatusTrimDeletedMany, deleted.Count);
        await Dialogs.ShowMessageAsync(Strings.Trim_Title, BuildTrimSummary(deleted, failures)).ConfigureAwait(true);
    }

    /// <summary>Lists each deleted branch with the SHA that restores it, then any failures.</summary>
    internal static string BuildTrimSummary(IReadOnlyList<TrimBranchRow> deleted, IReadOnlyList<(string Name, string Reason)> failures)
    {
        var text = new StringBuilder();
        if (deleted.Count > 0)
        {
            text.Append(deleted.Count == 1
                ? Strings.Repo_TrimSummaryDeletedOne
                : string.Format(Strings.Repo_TrimSummaryDeletedMany, deleted.Count)).Append('\n');
            foreach (TrimBranchRow row in deleted)
            {
                text.Append(string.Format(Strings.Repo_TrimSummaryDeletedLine, row.Name, row.ShortSha)).Append('\n');
            }
        }

        if (failures.Count > 0)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(Strings.Repo_TrimSummaryFailedHeader).Append('\n');
            foreach ((var name, var reason) in failures)
            {
                text.Append(string.Format(Strings.Repo_TrimSummaryFailedLine, name, reason)).Append('\n');
            }
        }

        return text.ToString().TrimEnd();
    }

    private async Task<TrimBranchesSnapshot> LoadTrimBranchesAsync(bool fetch)
    {
        string? error = null;
        // Without a remote there is nothing to fetch, and git would only complain.
        if (fetch && Remotes.Count > 0)
        {
            IsBusy = true;
            StatusMessage = $"{Strings.Repo_OpFetch}...";
            try
            {
                ProcessResult result = await Handler.FetchAsync(Path).ConfigureAwait(true);
                error = result.Success ? null : string.Format(Strings.Repo_TrimFetchFailed, FirstLine(result.Combined));
                StatusMessage = result.Success
                    ? string.Format(Strings.Repo_StatusOperationCompleted, Strings.Repo_OpFetch)
                    : Strings.Repo_StatusFetchFailed;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // The tips query has the commit dates; the refs snapshot knows which worktree has each branch.
        Task<IReadOnlyList<BranchRef>> tipsTask = Handler.GetLocalBranchTipsAsync(Path);
        Task<RefsSnapshot> refsTask = Handler.GetRefsSnapshotAsync(Path);
        Task<IReadOnlyList<string>> mergedTask = Handler.GetMergedBranchesAsync(Path);
        await Task.WhenAll(tipsTask, refsTask, mergedTask).ConfigureAwait(true);
        RefsSnapshot refs = refsTask.Result;
        var worktrees = refs.Branches
            .Where(b => b.Kind == RefKind.Branch && b.WorktreePath is not null)
            .ToDictionary(b => b.Name, b => b.WorktreePath, StringComparer.Ordinal);
        foreach (BranchRef branch in tipsTask.Result)
        {
            branch.WorktreePath = worktrees.GetValueOrDefault(branch.Name);
        }

        return new TrimBranchesSnapshot([.. tipsTask.Result.Where(b => b.Kind == RefKind.Branch)], mergedTask.Result,
            error ?? refs.Error);
    }

    private async Task RefreshAfterTrimAsync()
    {
        IsBusy = true;
        try
        {
            await ReloadAllCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FirstLine(string text)
        => text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? Strings.Repo_TrimUnknownError;
}
