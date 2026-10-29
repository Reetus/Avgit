using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using Avalonia.Media.Imaging;
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
    public partial string EmptyMessage { get; set; } = Strings.DiffVm_SelectFile;

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

    /// <summary>Reads one side of an image comparison; without it images show as binary files.</summary>
    public Func<ImageOrigin, CancellationToken, Task<ImageBytes>>? ReadImageAction { get; set; }

    /// <summary>Receives text copied from the diff; the app sets the system clipboard.</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>Asks the user to confirm (title, message); used once before discarding a selection.</summary>
    public Func<string, string, Task<bool>>? ConfirmAction { get; set; }

    /// <summary>
    /// Discards the checked lines of one hunk without asking again. The menu confirms once
    /// for the whole selection, which may span several hunks.
    /// </summary>
    public Func<Hunk, Task>? DiscardConfirmedLinesAction { get; set; }

    private CancellationTokenSource? _imageLoadCts;
    private readonly List<DiffImageSide> _imageSides = [];

    // Row selection is kept here instead of in the controls because the virtualizing
    // panel recycles containers. The order of _lineItems is the order lines are shown.
    private readonly List<DiffLineItem> _lineItems = [];
    private readonly Dictionary<DiffLineItem, int> _lineIndex = [];
    private readonly HashSet<DiffLineItem> _selectedLines = [];
    private DiffLineItem? _selectionAnchor;

    public bool HasSelection => _selectedLines.Count > 0;
    public DiffLineItem? SelectionAnchor => _selectionAnchor;

    /// <summary>Selected lines in display order.</summary>
    public IReadOnlyList<DiffLineItem> SelectedLines
    {
        get
        {
            var selected = new List<DiffLineItem>(_selectedLines.Count);
            foreach (DiffLineItem item in _lineItems)
            {
                if (item.IsSelected)
                {
                    selected.Add(item);
                }
            }

            return selected;
        }
    }

    public bool CanStageSelection => CanStage && SelectionAllows(line => !line.Hunk.Truncated);
    public bool CanUnstageSelection => CanUnstage && SelectionAllows(line => !line.Hunk.Truncated);

    // Line-level discard is not offered for new files (see ApplyNewFileSelectionAsync).
    public bool CanDiscardSelection => CanDiscard && SelectionAllows(line =>
        !line.Hunk.Truncated && line.File.State is not (PathState.New or PathState.Untracked));

    public ICommand StageFileCommand { get; }
    public ICommand UnstageFileCommand { get; }
    public ICommand DiscardFileCommand { get; }
    public ICommand StageHunkCommand { get; }
    public ICommand UnstageHunkCommand { get; }
    public ICommand DiscardHunkCommand { get; }
    public ICommand StageSelectedLinesCommand { get; }
    public ICommand UnstageSelectedLinesCommand { get; }
    public ICommand DiscardSelectedLinesCommand { get; }

    /// <summary>
    /// Shows <paramref name="files"/>. Files for which <paramref name="imageSides"/> returns
    /// sides are shown as images (loaded in the background) instead of a text diff.
    /// </summary>
    public void SetFiles(IReadOnlyList<FileDiff> files, string title, bool canStage, bool canUnstage, bool canDiscard,
        Func<FileDiff, ImageDiffSides?>? imageSides = null)
    {
        Title = title;
        CanStage = canStage;
        CanUnstage = canUnstage;
        CanDiscard = canDiscard;
        ResetSelection();
        Files.Clear();
        DisplayItems.Clear();
        ReleaseImages();
        CancellationToken imageToken = (_imageLoadCts = new CancellationTokenSource()).Token;

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

            if (ReadImageAction is { } readImage && imageSides?.Invoke(file) is { } sides)
            {
                var item = new DiffImageItem(file, sides);
                DisplayItems.Add(item);
                foreach (DiffImageSide side in item.Sides)
                {
                    _imageSides.Add(side);
                    _ = LoadImageAsync(side, readImage, imageToken);
                }

                continue;
            }

            if (file.Binary)
            {
                DisplayItems.Add(new DiffNoteItem(Strings.DiffVm_BinaryFile));
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
                    var item = new DiffLineItem(line, hunk, file, canStage || canUnstage || canDiscard, gutter);
                    _lineIndex[item] = _lineItems.Count;
                    _lineItems.Add(item);
                    DisplayItems.Add(item);
                }
            }
        }

        HasContent = files.Count > 0;
        EmptyMessage = files.Count == 0 ? Strings.DiffVm_NoChanges : string.Empty;
    }

    public void Clear(string? message = null)
    {
        ResetSelection();
        Files.Clear();
        DisplayItems.Clear();
        ReleaseImages();
        Title = string.Empty;
        EmptyMessage = message ?? Strings.DiffVm_SelectFile;
        HasContent = false;
        CanStage = CanUnstage = CanDiscard = false;
    }

    /// <summary>Applies a click to one line: shift extends, ctrl toggles, otherwise selects only it.</summary>
    public void SelectLine(DiffLineItem line, bool extend, bool toggle)
    {
        if (!_lineIndex.ContainsKey(line))
        {
            return;
        }

        if (extend && _selectionAnchor is not null)
        {
            SelectRangeCore(_selectionAnchor, line, add: toggle);
            return;
        }

        if (toggle)
        {
            SetSelected(line, !line.IsSelected);
        }
        else
        {
            SelectOnly(line);
        }

        _selectionAnchor = line;
    }

    /// <summary>Selects every line between two rows, as a shift+click or a drag does.</summary>
    public void SelectRange(DiffLineItem from, DiffLineItem to) => SelectRangeCore(from, to, add: false);

    public void SelectAllLines()
    {
        foreach (DiffLineItem line in _lineItems)
        {
            SetSelected(line, true);
        }

        _selectionAnchor ??= _lineItems.FirstOrDefault();
    }

    public void ClearSelection()
    {
        foreach (DiffLineItem line in _selectedLines.ToList())
        {
            SetSelected(line, false);
        }

        _selectionAnchor = null;
    }

    public void CopySelectedLines() => CopyText?.Invoke(BuildCopyText(line => true, withMarkers: false));

    /// <summary>Copies the selection as a diff snippet with each line's <c>+</c>, <c>-</c> or space.</summary>
    public void CopySelectedLinesWithMarkers() => CopyText?.Invoke(BuildCopyText(line => true, withMarkers: true));

    public void CopyNewVersion() => CopyText?.Invoke(
        BuildCopyText(line => line.Line.Marker is ' ' or '+', withMarkers: false));

    public void CopyOldVersion() => CopyText?.Invoke(
        BuildCopyText(line => line.Line.Marker is ' ' or '-', withMarkers: false));

    /// <summary>Copies the repository-relative path of the line the menu was opened on.</summary>
    public void CopyFilePath(DiffLineItem? line)
    {
        line ??= _lineItems.FirstOrDefault(item => item.IsSelected);
        if (line is not null)
        {
            CopyText?.Invoke(line.File.FileName);
        }
    }

    public Task StageSelectedLinesAsync() => ApplySelectionAsync(StageSelectedLinesAction, CanStageSelection);

    public Task UnstageSelectedLinesAsync() => ApplySelectionAsync(UnstageSelectedLinesAction, CanUnstageSelection);

    /// <summary>
    /// Discards the selected changed lines after one confirmation. Asking per hunk would let
    /// a cancel part-way through leave some hunks discarded and others not.
    /// </summary>
    public async Task DiscardSelectedLinesAsync()
    {
        if (!CanDiscardSelection || ConfirmAction is null)
        {
            return;
        }

        var count = _lineItems.Count(line => line.IsSelected && line.Line.Marker is '+' or '-');
        var message = count == 1 ? Strings.DiffVm_DiscardLineConfirm : string.Format(Strings.DiffVm_DiscardLinesConfirm, count);
        if (!await ConfirmAction(Strings.DiffVm_DiscardLinesTitle, message).ConfigureAwait(true))
        {
            return;
        }

        await ApplySelectionAsync(DiscardConfirmedLinesAction, allowed: true).ConfigureAwait(true);
    }

    private void SelectOnly(DiffLineItem line)
    {
        foreach (DiffLineItem item in _selectedLines.ToList())
        {
            if (!ReferenceEquals(item, line))
            {
                SetSelected(item, false);
            }
        }

        SetSelected(line, true);
    }

    private void SelectRangeCore(DiffLineItem from, DiffLineItem to, bool add)
    {
        if (!_lineIndex.TryGetValue(from, out var start) || !_lineIndex.TryGetValue(to, out var end))
        {
            return;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        if (!add)
        {
            foreach (DiffLineItem item in _selectedLines.ToList())
            {
                if (!_lineIndex.TryGetValue(item, out var index) || index < start || index > end)
                {
                    SetSelected(item, false);
                }
            }
        }

        for (var i = start; i <= end; i++)
        {
            SetSelected(_lineItems[i], true);
        }
    }

    private void SetSelected(DiffLineItem line, bool selected)
    {
        if (line.IsSelected == selected)
        {
            return;
        }

        line.IsSelected = selected;
        if (selected)
        {
            _selectedLines.Add(line);
        }
        else
        {
            _selectedLines.Remove(line);
        }
    }

    private void ResetSelection()
    {
        ClearSelection();
        _lineItems.Clear();
        _lineIndex.Clear();
    }

    /// <summary>True when a changed line is selected and every changed selected line passes the check.</summary>
    private bool SelectionAllows(Func<DiffLineItem, bool> allowed)
    {
        var anyChanged = false;
        foreach (DiffLineItem line in _lineItems)
        {
            if (!line.IsSelected || line.Line.Marker is not ('+' or '-'))
            {
                continue;
            }

            anyChanged = true;
            if (!allowed(line))
            {
                return false;
            }
        }

        return anyChanged;
    }

    /// <summary>
    /// Copies selected lines joined with <c>\n</c>. Raw text keeps tabs and is used
    /// instead of the tab-expanded display text; "no newline" markers are skipped.
    /// </summary>
    private string BuildCopyText(Func<DiffLineItem, bool> include, bool withMarkers)
    {
        var text = new StringBuilder();
        var first = true;
        foreach (DiffLineItem item in _lineItems)
        {
            if (!item.IsSelected || item.Line.Marker == '\\' || !include(item))
            {
                continue;
            }

            if (!first)
            {
                text.Append('\n');
            }

            first = false;
            if (withMarkers)
            {
                text.Append(item.Line.Marker);
            }

            var raw = item.Line.RawText;
            text.Append(raw.EndsWith('\r') ? raw[..^1] : raw);
        }

        return text.ToString();
    }

    /// <summary>
    /// Runs the existing selected-line action once per hunk that has a selected change.
    /// The staging checkboxes are snapshotted so menu actions do not disturb them.
    /// </summary>
    private async Task ApplySelectionAsync(Func<Hunk, Task>? action, bool allowed)
    {
        if (!allowed || action is null)
        {
            return;
        }

        var selected = new HashSet<HunkLine>();
        var hunks = new List<Hunk>();
        foreach (DiffLineItem item in _lineItems)
        {
            if (!item.IsSelected || item.Line.Marker is not ('+' or '-'))
            {
                continue;
            }

            selected.Add(item.Line);
            if (item.Hunk is Hunk hunk && !hunks.Contains(hunk))
            {
                hunks.Add(hunk);
            }
        }

        foreach (Hunk hunk in hunks)
        {
            IList<HunkLine> lines = hunk.LineItems;
            var checkboxes = new bool[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                checkboxes[i] = lines[i].Selected;
                lines[i].Selected = lines[i].Marker is '+' or '-' && selected.Contains(lines[i]);
            }

            try
            {
                await action(hunk).ConfigureAwait(true);
            }
            finally
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    lines[i].Selected = checkboxes[i];
                }
            }
        }
    }

    private static async Task LoadImageAsync(DiffImageSide side,
        Func<ImageOrigin, CancellationToken, Task<ImageBytes>> readImage, CancellationToken token)
    {
        ImageBytes bytes;
        try
        {
            bytes = await readImage(side.Origin, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            bytes = new ImageBytes(null, ex.Message);
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (bytes.Data is not { } data)
        {
            side.SetError(bytes.Error ?? Strings.DiffVm_CouldNotReadImage);
            return;
        }

        Bitmap? image = await Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(data, writable: false);
                return new Bitmap(stream);
            }
            catch (Exception)
            {
                return null;
            }
        }, CancellationToken.None).ConfigureAwait(true);

        if (image is null)
        {
            side.SetError(string.Format(Strings.DiffVm_CouldNotDecodeImage, DiffImages.FormatSize(data.Length)));
        }
        else if (token.IsCancellationRequested)
        {
            // The view moved on while decoding; nothing shows this bitmap.
            image.Dispose();
        }
        else
        {
            side.SetImage(image, data.Length);
        }
    }

    /// <summary>Stops pending image loads and frees the bitmaps of the items just removed.</summary>
    private void ReleaseImages()
    {
        _imageLoadCts?.Cancel();
        _imageLoadCts = null;
        foreach (DiffImageSide side in _imageSides)
        {
            side.Release();
        }

        _imageSides.Clear();
    }
}
