using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Avgit.Core.Model;

/// <summary>Which comparison a set of <see cref="FileDiff"/> objects describes.</summary>
public readonly struct DiffSetup
{
    public DiffScope Scope { get; init; }
    public DiffSubject Subject { get; init; }
}

/// <summary>A hunk of a file diff, exposed to the UI through one shared shape.</summary>
public interface IHunk
{
    int Number { get; set; }
    int OldStart { get; set; }
    int OldEnd { get; set; }
    int NewStart { get; set; }
    int NewEnd { get; set; }
    string RawText { get; set; }
    bool Truncated { get; set; }
    string? Annotation { get; set; }
    string Caption { get; }
    ObservableCollection<HunkLine> LineItems { get; set; }
    FileDiff Owner { get; }
    bool HasSelection { get; set; }
    bool CanDiscard { get; }
}

/// <summary>One changed file and the hunks that make up its textual diff.</summary>
public partial class FileDiff(DiffSetup setup) : ObservableObject
{
    public string FileName
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                Extension = Path.HasExtension(value)
                    ? Path.GetExtension(value).TrimStart('.')
                    : "txt";
                OnPropertyChanged(nameof(Extension));
            }
        }
    } = string.Empty;

    public string Extension { get; private set; } = "txt";

    [ObservableProperty]
    public partial string? OldPath { get; set; }

    public PathState State
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                InvalidateCaption();
            }
        }
    }

    public bool Truncated
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                InvalidateCaption();
            }
        }
    }

    [AllowNull]
    public string Caption
    {
        get => field ??= BuildCaption();
        private set;
    }

    private string BuildCaption()
    {
        var parts = new List<string>(4);
        var what = State switch
        {
            PathState.New => CoreStrings.FileState_New,
            PathState.Removed => CoreStrings.FileState_Deleted,
            PathState.Moved => string.IsNullOrEmpty(OldPath) ? CoreStrings.FileState_Renamed : string.Format(CoreStrings.FileState_RenamedFrom, OldPath),
            PathState.Duplicated => string.IsNullOrEmpty(OldPath) ? CoreStrings.FileState_Copied : string.Format(CoreStrings.FileState_CopiedFrom, OldPath),
            PathState.Untracked => CoreStrings.FileState_Untracked,
            PathState.Unchanged => CoreStrings.FileState_Unmodified,
            PathState.SubmoduleCommitChanged => CoreStrings.FileState_SubmoduleCommitChanged,
            PathState.Conflicted => CoreStrings.FileState_Conflicted,
            _ => CoreStrings.FileState_Modified
        };
        parts.Add(Binary ? string.Format(CoreStrings.FileCaption_Binary, what) : what);

        if (!Binary && (AddedLines > 0 || RemovedLines > 0 || Truncated))
        {
            parts.Add(string.Format(Truncated ? CoreStrings.FileCaption_LineCountsTruncated : CoreStrings.FileCaption_LineCounts,
                AddedLines, RemovedLines));
        }

        if (OldMode != 0 && NewMode != 0 && OldMode != NewMode)
        {
            parts.Add(string.Format(CoreStrings.FileCaption_ModeChanged, OldMode, NewMode));
        }

        if (Suppressed)
        {
            parts.Add(CoreStrings.FileCaption_DiffHidden);
        }

        return string.Join(" · ", parts);
    }

    partial void OnSuppressedChanged(bool value) => InvalidateCaption();

    partial void OnOldPathChanged(string? value) => InvalidateCaption();

    private void InvalidateCaption()
    {
        Caption = null;
        OnPropertyChanged(nameof(Caption));
    }

    public bool Binary
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                InvalidateCaption();
            }
        }
    }

    public bool ShowHunks => !Binary && Hunks.Count > 0;

    public string EmptyMessage
    {
        get
        {
            if (ShowHunks)
            {
                return string.Empty;
            }

            if (Suppressed)
            {
                return CoreStrings.FileEmpty_DiffHidden;
            }

            if (Binary)
            {
                return CoreStrings.FileEmpty_Binary;
            }

            if (Truncated)
            {
                return CoreStrings.FileEmpty_TooLarge;
            }

            if (OldMode != 0 && NewMode != 0 && OldMode != NewMode)
            {
                return string.Format(CoreStrings.FileEmpty_ModeOnly, OldMode, NewMode);
            }

            return State switch
            {
                PathState.Moved => CoreStrings.FileEmpty_RenamedOnly,
                PathState.Duplicated => CoreStrings.FileEmpty_CopiedOnly,
                PathState.New or PathState.Untracked or PathState.Unchanged => CoreStrings.FileEmpty_EmptyFile,
                PathState.Removed => CoreStrings.FileEmpty_EmptyFileDeleted,
                _ => CoreStrings.FileEmpty_NoContentChanges
            };
        }
    }

    public int AddedLines
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                InvalidateCaption();
            }
        }
    }

    public int RemovedLines
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                InvalidateCaption();
            }
        }
    }

    public int OldMode
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                InvalidateCaption();
            }
        }
    }

    public int NewMode
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                InvalidateCaption();
            }
        }
    }

    public ObservableCollection<IHunk> Hunks
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ShowHunks));
                OnPropertyChanged(nameof(EmptyMessage));
            }
        }
    } = [];

    [ObservableProperty]
    public partial bool Suppressed { get; set; }

    public DiffSetup Setup { get; set; } = setup;

    public bool CanDiscard => Setup.Subject == DiffSubject.PendingFiles
                              && Setup.Scope != DiffScope.IndexVsHead
                              && (State & PathState.ChangedMask) > PathState.None;

    /// <summary>When a file is too big to diff, allow reading it whole instead.</summary>
    public bool AllowLargeText { get; set; }
}

/// <summary>A contiguous run of changed lines inside a <see cref="FileDiff"/>.</summary>
public partial class Hunk(FileDiff owner) : ObservableObject, IHunk
{
    [ObservableProperty]
    public partial int Number { get; set; }

    [ObservableProperty]
    public partial int OldStart { get; set; }

    [ObservableProperty]
    public partial int OldEnd { get; set; }

    [ObservableProperty]
    public partial int NewStart { get; set; }

    [ObservableProperty]
    public partial int NewEnd { get; set; }

    [ObservableProperty]
    public partial string RawText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial bool Truncated { get; set; }

    public string? Annotation
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                Caption = null;
                OnPropertyChanged(nameof(Caption));
            }
        }
    }

    [AllowNull]
    public string Caption
    {
        get => field ??= BuildCaption();
        private set;
    }

    private string BuildCaption()
    {
        var oldCount = OldEnd == 0 ? 0 : OldEnd - OldStart + 1;
        var newCount = NewEnd == 0 ? 0 : NewEnd - NewStart + 1;
        var wholeFile = Owner.State is PathState.New or PathState.Untracked or PathState.Unchanged;
        string range;
        if (wholeFile)
        {
            range = Truncated || Owner.Truncated
                ? string.Format(CoreStrings.HunkCaption_FirstLines, LineItems.Count(l => l.Marker != '\\'))
                : string.Format(CoreStrings.HunkCaption_WholeFile, newCount);
        }
        else if (Owner.State == PathState.Removed && NewEnd == 0)
        {
            range = string.Format(CoreStrings.HunkCaption_DeletedLines, oldCount);
        }
        else if (newCount == 0)
        {
            range = oldCount == 1
                ? string.Format(CoreStrings.HunkCaption_LineRemoved, OldStart)
                : string.Format(CoreStrings.HunkCaption_LinesRemoved, OldStart, OldEnd);
        }
        else
        {
            range = newCount == 1
                ? string.Format(CoreStrings.HunkCaption_Line, NewStart)
                : string.Format(CoreStrings.HunkCaption_Lines, NewStart, NewEnd);
        }

        if (Truncated && !wholeFile)
        {
            range = string.Format(CoreStrings.HunkCaption_Truncated, range);
        }

        return string.IsNullOrEmpty(Annotation) ? range : $"{Annotation} · {range}";
    }

    partial void OnTruncatedChanged(bool value)
    {
        Caption = null;
        OnPropertyChanged(nameof(Caption));
    }

    [ObservableProperty]
    public partial ObservableCollection<HunkLine> LineItems { get; set; } = [];

    public FileDiff Owner { get; } = owner;
    public bool CanDiscard => Owner.CanDiscard;
}

/// <summary>A single line of a diff, as rendered in the diff pane.</summary>
public partial class HunkLine : ObservableObject
{
    [ObservableProperty]
    public partial char Marker { get; set; }

    [ObservableProperty]
    public partial string OldLineNo { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewLineNo { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RawText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Selected { get; set; }

    public HunkLine Copy() => new()
    {
        NewLineNo = NewLineNo,
        OldLineNo = OldLineNo,
        Selected = false,
        Text = Text,
        Marker = Marker,
        RawText = RawText
    };
}
