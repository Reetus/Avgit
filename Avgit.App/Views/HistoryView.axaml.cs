using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avgit.App.ViewModels;

namespace Avgit.App.Views;

public partial class HistoryView : UserControl
{
    private RepositoryViewModel? _repository;
    private CommitRowViewModel? _contextRow;

    public HistoryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ListBox? commitList = this.FindControl<ListBox>("CommitList");
        commitList?.AddHandler(
            InputElement.PointerPressedEvent,
            OnCommitPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        commitList?.SizeChanged += (_, e) => UpdateCommitListWidthClasses(commitList, e.NewSize.Width);

        // Switching to the File Status tab detaches this view without changing its
        // DataContext, so DataContextChanged alone would not reconnect it on return
        // and clicks on commits would be ignored.
        AttachedToVisualTree += (_, _) =>
        {
            SetRepository(DataContext as RepositoryViewModel);
            // A history reload while hidden rebuilt the rows and asked for the selection
            // to be restored while nothing was listening; restore it now.
            if (_repository?.SelectedCommit is { } selected
                && this.FindControl<ListBox>("CommitList")?.SelectedItems is { } items
                && !items.Contains(selected))
            {
                OnScrollToCommit(selected.Row.CommitId);
            }
        };
        DetachedFromVisualTree += (_, _) => SetRepository(null);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Below these list widths the author, then the date, column is dropped so the
    // message and ref pills keep usable room (graph up to 160 + author 126 + date 96).
    private const double HideAuthorBelow = 620;
    private const double HideDateBelow = 460;

    internal static void UpdateCommitListWidthClasses(ListBox list, double width)
    {
        list.Classes.Set("hideAuthor", width < HideAuthorBelow);
        list.Classes.Set("hideDate", width < HideDateBelow);
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => SetRepository(DataContext as RepositoryViewModel);

    private void SetRepository(RepositoryViewModel? repository)
    {
        if (ReferenceEquals(_repository, repository))
        {
            return;
        }

        _repository?.ScrollToCommitRequested -= OnScrollToCommit;
        _repository = repository;
        _repository?.ScrollToCommitRequested += OnScrollToCommit;
    }

    private void OnScrollToCommit(string sha)
    {
        CommitRowViewModel? row = _repository?.LogRows.FirstOrDefault(r =>
            string.Equals(r.Row.CommitId, sha, StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            ListBox? list = this.FindControl<ListBox>("CommitList");
            if (list is null || _repository is null)
            {
                return;
            }

            list.ScrollIntoView(row);
            RepositoryViewModel repository = _repository;
            // A click since the refresh owns the selection now.
            if (!ReferenceEquals(repository.SelectedCommit, row))
            {
                return;
            }

            // A comparison has two selected rows; restore both so the list keeps
            // matching the comparison the panes show after a refresh.
            repository.RestoreCommitSelection(() =>
            {
                list.SelectedItems?.Clear();
                list.SelectedItems?.Add(row);
                foreach (CommitRowViewModel other in repository.SelectedCommits)
                {
                    if (!ReferenceEquals(other, row))
                    {
                        list.SelectedItems?.Add(other);
                    }
                }
            });
        });
    }

    private void OnCommitPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_repository is null || e.Source is not Visual source)
        {
            return;
        }

        ListBoxItem? item = source.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not CommitRowViewModel row)
        {
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                _contextRow = null;
            }

            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            _contextRow = row;
            var list = sender as ListBox;
            if (list?.SelectedItems is { } selectedItems && !selectedItems.Contains(row))
            {
                selectedItems.Clear();
                selectedItems.Add(row);
            }
        }

        // Keep multi-selection intact while making the clicked row the active
        // commit for details and single-target commands.
        _repository.SetContextCommit(row);
    }

    private void OnCommitSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_repository is null || sender is not ListBox { SelectedItems: { } selectedItems })
        {
            return;
        }

        _repository.SetSelectedCommits(selectedItems.OfType<CommitRowViewModel>());
        if (e.AddedItems.OfType<CommitRowViewModel>().LastOrDefault() is { } active)
        {
            _repository.SetContextCommit(active);
        }
    }

    private void OnCommitMenuOpening(object? sender, CancelEventArgs e)
    {
        if (_contextRow is null || _repository?.SelectedCommit is null)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = false;
    }

    private void OnCommitMenuClosing(object? sender, CancelEventArgs e) => _contextRow = null;

    private void OnCheckoutCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.CheckoutCommitCommand.Execute(null);

    private void OnCreateBranchHere(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.CreateBranchAtCommitCommand.Execute(null);

    private void OnCreateTagHere(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.CreateTagAtCommitCommand.Execute(null);

    private void OnMergeCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.MergeCommitCommand.Execute(null);

    private void OnCherryPick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.CherryPickCommand.Execute(null);

    private void OnRevertCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.RevertCommitCommand.Execute(null);

    private void OnRebaseOntoCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.RebaseCommitCommand.Execute(null);

    private void OnInteractiveRebase(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.InteractiveRebaseCommand.Execute(null);

    private void OnResetCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.ResetCommitCommand.Execute(null);

    private void OnCopyCommitSha(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.CopyCommitShaCommand.Execute(null);

    private void OnSetCompareBase(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.SetCompareBaseCommand.Execute(null);

    private void OnCompareWithBase(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repository?.CompareWithBaseCommand.Execute(null);
}
