using System.Collections.ObjectModel;
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

    /// <summary>Reads one side of an image comparison; without it images show as binary files.</summary>
    public Func<ImageOrigin, CancellationToken, Task<ImageBytes>>? ReadImageAction { get; set; }

    private CancellationTokenSource? _imageLoadCts;
    private readonly List<DiffImageSide> _imageSides = [];

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
        ReleaseImages();
        Title = string.Empty;
        EmptyMessage = message;
        HasContent = false;
        CanStage = CanUnstage = CanDiscard = false;
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
            side.SetError(bytes.Error ?? "Could not read the image.");
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
            side.SetError($"Could not decode the image ({DiffImages.FormatSize(data.Length)}).");
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
