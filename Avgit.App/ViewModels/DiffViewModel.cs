using System.Collections.ObjectModel;
using System.Windows.Input;
using Avgit.Core.Diff;
using Avgit.Core.Model;

namespace Avgit.App.ViewModels;

public sealed partial class DiffViewModel : ObservableObject
{
    public DiffViewModel()
    {
        StageFileCommand = new AsyncRelayCommand(p => StageFileAction?.Invoke((FileDiff)p!) ?? Task.CompletedTask, p => CanStage && p is FileDiff);
        UnstageFileCommand = new AsyncRelayCommand(p => UnstageFileAction?.Invoke((FileDiff)p!) ?? Task.CompletedTask, p => CanUnstage && p is FileDiff);
        DiscardFileCommand = new AsyncRelayCommand(p => DiscardFileAction?.Invoke((FileDiff)p!) ?? Task.CompletedTask, p => CanDiscard && p is FileDiff);
        StageHunkCommand = new AsyncRelayCommand(p => StageHunkAction?.Invoke((Hunk)p!) ?? Task.CompletedTask, p => CanStage && p is Hunk h && !h.Truncated);
        UnstageHunkCommand = new AsyncRelayCommand(p => UnstageHunkAction?.Invoke((Hunk)p!) ?? Task.CompletedTask, p => CanUnstage && p is Hunk h && !h.Truncated);
        DiscardHunkCommand = new AsyncRelayCommand(p => DiscardHunkAction?.Invoke((Hunk)p!) ?? Task.CompletedTask, p => CanDiscard && p is Hunk h && !h.Truncated);
        StageSelectedLinesCommand = new AsyncRelayCommand(p => StageSelectedLinesAction?.Invoke((Hunk)p!) ?? Task.CompletedTask, p => CanStage && p is Hunk h && !h.Truncated);
        UnstageSelectedLinesCommand = new AsyncRelayCommand(p => UnstageSelectedLinesAction?.Invoke((Hunk)p!) ?? Task.CompletedTask, p => CanUnstage && p is Hunk h && !h.Truncated);
        DiscardSelectedLinesCommand = new AsyncRelayCommand(p => DiscardSelectedLinesAction?.Invoke((Hunk)p!) ?? Task.CompletedTask, p => CanDiscard && p is Hunk h && !h.Truncated);
    }

    public ObservableCollection<DiffDisplayItem> DisplayItems { get; } = [];
    public ObservableCollection<FileDiff> Files { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = "Select a file to view its diff.";

    public bool HasContent
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => !HasContent;

    [ObservableProperty]
    public partial bool CanStage { get; private set; }

    [ObservableProperty]
    public partial bool CanUnstage { get; private set; }

    [ObservableProperty]
    public partial bool CanDiscard { get; private set; }

    public Func<FileDiff, Task>? StageFileAction { get; set; }
    public Func<FileDiff, Task>? UnstageFileAction { get; set; }
    public Func<FileDiff, Task>? DiscardFileAction { get; set; }
    public Func<Hunk, Task>? StageHunkAction { get; set; }
    public Func<Hunk, Task>? UnstageHunkAction { get; set; }
    public Func<Hunk, Task>? DiscardHunkAction { get; set; }
    public Func<Hunk, Task>? StageSelectedLinesAction { get; set; }
    public Func<Hunk, Task>? UnstageSelectedLinesAction { get; set; }
    public Func<Hunk, Task>? DiscardSelectedLinesAction { get; set; }

    public ICommand StageFileCommand { get; }
    public ICommand UnstageFileCommand { get; }
    public ICommand DiscardFileCommand { get; }
    public ICommand StageHunkCommand { get; }
    public ICommand UnstageHunkCommand { get; }
    public ICommand DiscardHunkCommand { get; }
    public ICommand StageSelectedLinesCommand { get; }
    public ICommand UnstageSelectedLinesCommand { get; }
    public ICommand DiscardSelectedLinesCommand { get; }

    public void SetFiles(IReadOnlyList<FileDiff> files, string title, bool canStage, bool canUnstage, bool canDiscard)
    {
        Title = title;
        CanStage = canStage;
        CanUnstage = canUnstage;
        CanDiscard = canDiscard;
        Files.Clear();
        DisplayItems.Clear();

        foreach (FileDiff file in files)
        {
            var gutter = DiffGutter.For(file);
            Files.Add(file);
            DisplayItems.Add(new DiffFileHeaderItem(file, canStage, canUnstage, canDiscard)
            {
                StageCommand = StageFileCommand,
                UnstageCommand = UnstageFileCommand,
                DiscardCommand = DiscardFileCommand
            });

            if (file.Binary)
            {
                DisplayItems.Add(new DiffNoteItem("Binary file; no text diff is available."));
                continue;
            }

            if (file.Hunks.Count == 0)
            {
                DisplayItems.Add(new DiffNoteItem(file.EmptyMessage));
                continue;
            }

            foreach (IHunk hunk in file.Hunks)
            {
                DisplayItems.Add(new DiffHunkHeaderItem(hunk, file, canStage, canUnstage, canDiscard)
                {
                    StageCommand = StageHunkCommand,
                    UnstageCommand = UnstageHunkCommand,
                    DiscardCommand = DiscardHunkCommand,
                    StageSelectedLinesCommand = canStage ? StageSelectedLinesCommand : null,
                    UnstageSelectedLinesCommand = canUnstage ? UnstageSelectedLinesCommand : null,
                    DiscardSelectedLinesCommand = canDiscard ? DiscardSelectedLinesCommand : null
                });

                foreach (HunkLine line in hunk.LineItems)
                {
                    DisplayItems.Add(new DiffLineItem(line, hunk, file, canStage || canUnstage || canDiscard, gutter));
                }
            }
        }

        HasContent = files.Count > 0;
        EmptyMessage = files.Count == 0 ? "No changes to display." : string.Empty;
    }

    public void Clear(string message = "Select a file to view its diff.")
    {
        Files.Clear();
        DisplayItems.Clear();
        Title = string.Empty;
        EmptyMessage = message;
        HasContent = false;
        CanStage = CanUnstage = CanDiscard = false;
    }
}
