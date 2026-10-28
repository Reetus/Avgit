using Avalonia.Media.Imaging;
using Avgit.Core.Model;

namespace Avgit.App.ViewModels;

/// <summary>
/// Where one side of an image comparison is read from: a git object
/// (<c>rev:path</c>, or <c>:path</c> for the index) or a file in the work tree.
/// </summary>
public sealed record ImageOrigin(string? ObjectSpec = null, string? FilePath = null)
{
    public static ImageOrigin Object(string spec) => new(ObjectSpec: spec);
    public static ImageOrigin File(string path) => new(FilePath: path);
}

/// <summary>The two sides of an image comparison; a missing side is a new or deleted file.</summary>
public sealed record ImageDiffSides(ImageOrigin? Old, ImageOrigin? New);

/// <summary>An image's bytes, or why they could not be read.</summary>
public sealed record ImageBytes(byte[]? Data, string? Error = null);

public static class DiffImages
{
    /// <summary>Formats Avalonia's Skia decoder reads. Animated GIFs show their first frame.</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"
    };

    /// <summary>Larger files are not loaded.</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    public static bool IsImagePath(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// The sides to show for <paramref name="file"/>, or null when it is not an image
    /// (or is conflicted, where neither side is well defined). New files have no old
    /// side and deleted files no new one; a renamed file's old side uses its old path.
    /// </summary>
    public static ImageDiffSides? SidesFor(FileDiff file, Func<string, ImageOrigin>? oldSide, Func<string, ImageOrigin>? newSide)
    {
        if (!IsImagePath(file.FileName) || file.State == PathState.Conflicted)
        {
            return null;
        }

        ImageOrigin? before = file.State is PathState.New or PathState.Untracked or PathState.Unchanged
            ? null
            : oldSide?.Invoke(file.OldPath ?? file.FileName);
        ImageOrigin? after = file.State == PathState.Removed ? null : newSide?.Invoke(file.FileName);
        return before is null && after is null ? null : new ImageDiffSides(before, after);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / (1024.0 * 1024):N1} MB"
    };
}

/// <summary>One panel of an image comparison: a label, the decoded image and a caption.</summary>
public sealed partial class DiffImageSide(string label, ImageOrigin origin) : ObservableObject
{
    public string Label { get; } = label;
    public ImageOrigin Origin { get; } = origin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial Bitmap? Image { get; private set; }

    public bool HasImage => Image is not null;

    [ObservableProperty]
    public partial string Info { get; private set; } = "Loading…";

    public void SetImage(Bitmap image, long byteCount)
    {
        Image = image;
        Info = $"{image.PixelSize.Width:N0} × {image.PixelSize.Height:N0} px · {DiffImages.FormatSize(byteCount)}";
    }

    public void SetError(string message) => Info = message;

    /// <summary>Frees the bitmap once the side is no longer displayed.</summary>
    public void Release()
    {
        Bitmap? image = Image;
        Image = null;
        image?.Dispose();
    }
}

/// <summary>
/// An image file shown as pictures instead of a text diff: Old File and New File side
/// by side, or a single panel when the file was added or deleted.
/// </summary>
public sealed class DiffImageItem : DiffDisplayItem
{
    public DiffImageItem(FileDiff file, ImageDiffSides sides)
    {
        File = file;
        if (sides.Old is { } before)
        {
            Sides.Add(new DiffImageSide("Old File", before));
        }

        if (sides.New is { } after)
        {
            // An unmodified file is shown as its content, not as a change.
            Sides.Add(new DiffImageSide(file.State == PathState.Unchanged ? "File" : "New File", after));
        }
    }

    public FileDiff File { get; }
    public List<DiffImageSide> Sides { get; } = [];
}
