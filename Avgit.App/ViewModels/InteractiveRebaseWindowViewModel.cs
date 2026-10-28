using System.Collections.ObjectModel;
using System.Windows.Input;
using Avgit.App.Services;

namespace Avgit.App.ViewModels;

public sealed partial class RebasePlanRow : ObservableObject
{
    public RebasePlanRow(RebasePlanItem item)
    {
        Sha = item.Sha;
        ShortSha = item.ShortSha;
        Message = item.Message;
        Action = item.Action;
    }

    public static IReadOnlyList<string> Actions { get; } = ["pick", "reword", "edit", "squash", "fixup", "drop"];

    public string Sha { get; }
    public string ShortSha { get; }

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    public string Action
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsReword));
            }
        }
    } = "pick";

    public bool IsReword => Action == "reword";
}

public sealed class InteractiveRebaseWindowViewModel : ObservableObject
{
    public InteractiveRebaseWindowViewModel(IReadOnlyList<RebasePlanItem> commits)
    {
        Rows = new ObservableCollection<RebasePlanRow>(commits.Select(c => new RebasePlanRow(c)));
        MoveUpCommand = new RelayCommand(p => Move(p, -1));
        MoveDownCommand = new RelayCommand(p => Move(p, 1));
    }

    public ObservableCollection<RebasePlanRow> Rows { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public bool Accepted { get; set; }

    private void Move(object? parameter, int delta)
    {
        if (parameter is not RebasePlanRow row)
        {
            return;
        }

        var index = Rows.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Rows.Count)
        {
            return;
        }

        Rows.Move(index, target);
    }

    public IReadOnlyList<RebasePlanItem> ToPlan() => [.. Rows.Select(row => new RebasePlanItem
    {
        Sha = row.Sha,
        ShortSha = row.ShortSha,
        Message = row.Message,
        Action = row.Action
    })];
}
