using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.App.Views;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>Image files in the diff view: reading their bytes from git and showing Old File / New File.</summary>
public class ImageDiffTests
{
    private static readonly string ArtifactDirectory =
        Path.Combine(Path.GetTempPath(), "avgit-headless");

    [Fact]
    public async Task File_bytes_round_trip_from_commits_and_the_index()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        // NUL, CR/LF and high bytes would all be damaged by a text read.
        byte[] committed = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFE, 0x00, 0x80];
        byte[] staged = [.. committed, 0x0D, 0x0A, 0x00];
        var file = Path.Combine(repo.Path, "data.bin");
        await File.WriteAllBytesAsync(file, committed);
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "Add data");
        await File.WriteAllBytesAsync(file, staged);
        await repo.StageAllAsync();

        BinaryProcessResult head = await repo.Git.GetFileBytesAsync(repo.Path, "HEAD:data.bin", 1024);
        Assert.True(head.Success, head.Error);
        Assert.Equal(committed, head.Output);

        BinaryProcessResult index = await repo.Git.GetFileBytesAsync(repo.Path, ":data.bin", 1024);
        Assert.True(index.Success, index.Error);
        Assert.Equal(staged, index.Output);

        BinaryProcessResult tooLarge = await repo.Git.GetFileBytesAsync(repo.Path, "HEAD:data.bin", 4);
        Assert.True(tooLarge.TooLarge);
        Assert.False(tooLarge.Success);
        Assert.Empty(tooLarge.Output);

        BinaryProcessResult missing = await repo.Git.GetFileBytesAsync(repo.Path, "HEAD:missing.png", 1024);
        Assert.False(missing.Success);
    }

    [Fact]
    public void Sides_follow_the_file_state()
    {
        static ImageOrigin Old(string p) => ImageOrigin.Object("A:" + p);
        static ImageOrigin New(string p) => ImageOrigin.Object("B:" + p);
        static FileDiff Diff(string name, PathState state, string? oldPath = null)
            => new(default) { FileName = name, State = state, OldPath = oldPath };

        ImageDiffSides? modified = DiffImages.SidesFor(Diff("img/a.PNG", PathState.Edited), Old, New);
        Assert.Equal(new ImageDiffSides(Old("img/a.PNG"), New("img/a.PNG")), modified);

        Assert.Equal(new ImageDiffSides(null, New("a.png")), DiffImages.SidesFor(Diff("a.png", PathState.New), Old, New));
        Assert.Equal(new ImageDiffSides(null, New("a.png")), DiffImages.SidesFor(Diff("a.png", PathState.Untracked), Old, New));
        Assert.Equal(new ImageDiffSides(Old("a.png"), null), DiffImages.SidesFor(Diff("a.png", PathState.Removed), Old, New));
        Assert.Equal(new ImageDiffSides(Old("old.jpg"), New("new.jpg")),
            DiffImages.SidesFor(Diff("new.jpg", PathState.Moved, "old.jpg"), Old, New));

        // A root commit has no old side to read.
        Assert.Equal(new ImageDiffSides(null, New("a.gif")), DiffImages.SidesFor(Diff("a.gif", PathState.Edited), null, New));

        Assert.Null(DiffImages.SidesFor(Diff("a.svg", PathState.Edited), Old, New));
        Assert.Null(DiffImages.SidesFor(Diff("a.bin", PathState.Edited), Old, New));
        Assert.Null(DiffImages.SidesFor(Diff("a.png", PathState.Conflicted), Old, New));
    }

    [AvaloniaFact]
    public async Task Images_show_old_and_new_side_by_side()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var logo = Path.Combine(repo.Path, "logo.png");
        WritePng(logo, 40, 30, 0xFFD03030);
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "Add logo");
        WritePng(logo, 2000, 40, 0xFF3060D0);
        await repo.StageAllAsync();
        await repo.Git.CommitAsync(repo.Path, "Change logo");
        WritePng(logo, 50, 50, 0x8030B040);
        WritePng(Path.Combine(repo.Path, "new.png"), 16, 16, 0xFFE0C020);

        var settings = new AppSettingsService(Path.Combine(ArtifactDirectory, "settings-images.json"));
        var vm = new MainWindowViewModel(settings, new FakeDialogService());
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;

        // The newest commit changed only the logo, so its diff opens on it.
        DiffImageItem commitImage = await WaitForImageAsync(repository.CommitDiff);
        Assert.Equal(["Old File", "New File"], commitImage.Sides.Select(s => s.Label));
        Assert.Equal(new PixelSize(40, 30), commitImage.Sides[0].Image!.PixelSize);
        Assert.Equal(new PixelSize(2000, 40), commitImage.Sides[1].Image!.PixelSize);
        Assert.DoesNotContain(repository.CommitDiff.DisplayItems, i => i is DiffNoteItem);

        // The wide image scales down to its half of the pane instead of scrolling sideways.
        Dispatcher.UIThread.RunJobs();
        DiffView commitView = window.GetVisualDescendants().OfType<DiffView>()
            .First(v => ReferenceEquals(v.DataContext, repository.CommitDiff));
        Image[] images = commitView.GetVisualDescendants().OfType<Image>().ToArray();
        Assert.Equal(2, images.Length);
        Assert.InRange(images[1].Bounds.Width, 1, commitView.Bounds.Width / 2);
        Assert.InRange(images[0].Bounds.Width, 39.9, 40.1);

        repository.SelectedUnstagedFile = repository.UnstagedFiles.First(f => f.RelativePath == "logo.png");
        DiffImageItem working = await WaitForImageAsync(repository.WorkingDiff);
        Assert.Equal(new PixelSize(2000, 40), working.Sides[0].Image!.PixelSize);
        Assert.Equal(new PixelSize(50, 50), working.Sides[1].Image!.PixelSize);
        Assert.Contains("50 × 50 px", working.Sides[1].Info);

        Directory.CreateDirectory(ArtifactDirectory);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(ArtifactDirectory, "image-diff.png"), PngBitmapEncoderOptions.Default);

        repository.SelectedUnstagedFile = repository.UnstagedFiles.First(f => f.RelativePath == "new.png");
        DiffImageItem added = await WaitForImageAsync(repository.WorkingDiff, "new.png");
        DiffImageSide only = Assert.Single(added.Sides);
        Assert.Equal("New File", only.Label);
        Assert.Equal(new PixelSize(16, 16), only.Image!.PixelSize);
        // Moving to another file frees the previous bitmaps.
        Assert.All(working.Sides, s => Assert.Null(s.Image));

        await repo.StageAllAsync();
        await repository.RefreshAsync();
        repository.SelectedStagedFile = repository.StagedFiles.First(f => f.RelativePath == "logo.png");
        DiffImageItem staged = await WaitForImageAsync(repository.WorkingDiff, "logo.png");
        Assert.Equal(new PixelSize(2000, 40), staged.Sides[0].Image!.PixelSize);
        Assert.Equal(new PixelSize(50, 50), staged.Sides[1].Image!.PixelSize);

        window.Close();
    }

    private static async Task<DiffImageItem> WaitForImageAsync(DiffViewModel diff, string? fileName = null)
    {
        DiffImageItem? item = null;
        await WaitForAsync(() =>
        {
            item = diff.DisplayItems.OfType<DiffImageItem>()
                .FirstOrDefault(i => fileName is null || i.File.FileName == fileName);
            return item is not null && item.Sides.All(s => s.HasImage);
        });
        return item!;
    }

    /// <summary>Writes a solid <paramref name="argb"/> PNG.</summary>
    private static void WritePng(string path, int width, int height, uint argb)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer frame = bitmap.Lock())
        {
            var row = new int[width];
            Array.Fill(row, unchecked((int)argb));
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(row, 0, frame.Address + y * frame.RowBytes, width);
            }
        }

        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
