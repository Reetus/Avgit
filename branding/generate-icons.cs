#:package SkiaSharp@3.119.4
#:package SkiaSharp.NativeAssets.Linux.NoDependencies@3.119.4

// Regenerates every platform icon from branding/avgit-logo-original.png.
//
//   dotnet run branding/generate-icons.cs
//
// Outputs (paths relative to the repository root):
//   Avgit.App/Assets/avgit.ico           Windows executable icon (16-256 px)
//   Avgit.App/Assets/avgit-256.png       window/taskbar icon used at runtime
//   branding/macos/avgit.icns            macOS app bundle icon (16-1024 px)
//   branding/linux/hicolor/NxN/apps/avgit.png   freedesktop icon theme sizes
//   branding/avgit-1024.png              cleaned master (opaque tile, square)

using SkiaSharp;

var root = FindRepositoryRoot();
var sourcePath = Path.Combine(root, "branding", "avgit-logo-original.png");
using var original = SKBitmap.Decode(sourcePath)
    ?? throw new InvalidOperationException($"Cannot decode {sourcePath}");

// The tile is very slightly translucent (alpha 253); make its interior fully
// opaque so desktop backgrounds never show through, keeping anti-aliased edges.
using var master = MakeTileOpaque(original);

// macOS draws icons with ~10% transparent padding (Apple's icon grid); the
// original already has that framing. Windows and Linux icons fill the canvas,
// so those are cropped to the tile with a small margin, which keeps the graph
// legible at 16-32 px.
var tile = FindOpaqueBounds(master);
var margin = (int)(Math.Max(tile.Width, tile.Height) * 0.03);
var tightSide = Math.Max(tile.Width, tile.Height) + 2 * margin;
var tight = new SKRectI(
    tile.MidX - tightSide / 2, tile.MidY - tightSide / 2,
    tile.MidX - tightSide / 2 + tightSide, tile.MidY - tightSide / 2 + tightSide);

byte[] Framed(int size) => EncodePng(Resize(master, new SKRectI(0, 0, master.Width, master.Height), size));
byte[] Tight(int size) => EncodePng(Resize(master, tight, size));

Write(Path.Combine(root, "branding", "avgit-1024.png"), Framed(1024));

// Windows: PNG-compressed ICO entries (supported since Windows Vista).
var icoSizes = [ 16, 20, 24, 32, 40, 48, 64, 128, 256 ];
Write(Path.Combine(root, "Avgit.App", "Assets", "avgit.ico"),
    BuildIco(icoSizes.Select(s => (s, Tight(s)))));
Write(Path.Combine(root, "Avgit.App", "Assets", "avgit-256.png"), Tight(256));

// Linux: freedesktop hicolor theme sizes.
foreach (var size in [ 16, 24, 32, 48, 64, 128, 256, 512 ])
    Write(Path.Combine(root, "branding", "linux", "hicolor", $"{size}x{size}", "apps", "avgit.png"), Tight(size));

// macOS: ICNS with PNG payloads, including the @2x (Retina) variants.
var icns = new (string Type, int Size)[]
{
    ("icp4", 16), ("ic11", 32), ("icp5", 32), ("ic12", 64), ("ic07", 128),
    ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024)
};
Write(Path.Combine(root, "branding", "macos", "avgit.icns"),
    BuildIcns(icns.Select(e => (e.Type, Framed(e.Size)))));

Console.WriteLine("Icons generated.");

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Avgit.slnx")))
        directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("Run from inside the Avgit repository.");
}

static SKBitmap MakeTileOpaque(SKBitmap source)
{
    var result = source.Copy(SKColorType.Rgba8888) ?? throw new InvalidOperationException("Copy failed.");
    for (var y = 0; y < result.Height; y++)
    {
        for (var x = 0; x < result.Width; x++)
        {
            var color = result.GetPixel(x, y);
            if (color.Alpha >= 250 && color.Alpha < 255)
                result.SetPixel(x, y, color.WithAlpha(255));
        }
    }

    return result;
}

static SKRectI FindOpaqueBounds(SKBitmap bitmap)
{
    int left = bitmap.Width, top = bitmap.Height, right = 0, bottom = 0;
    for (var y = 0; y < bitmap.Height; y++)
    {
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).Alpha < 128)
                continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + 1);
            bottom = Math.Max(bottom, y + 1);
        }
    }

    return new SKRectI(left, top, right, bottom);
}

static SKBitmap Resize(SKBitmap source, SKRectI sourceRect, int size)
{
    // Downscale in halving steps so small sizes average every source pixel
    // instead of skipping detail (single-step scaling aliases thin lines).
    using var cropped = new SKBitmap(sourceRect.Width, sourceRect.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(cropped))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, sourceRect, new SKRect(0, 0, sourceRect.Width, sourceRect.Height));
    }

    var current = cropped.Copy();
    while (current.Width / 2 >= size)
    {
        var half = current.Resize(new SKImageInfo(current.Width / 2, current.Height / 2, SKColorType.Rgba8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        current.Dispose();
        current = half;
    }

    var final = current.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul),
        new SKSamplingOptions(SKCubicResampler.Mitchell));
    current.Dispose();
    return final;
}

static byte[] EncodePng(SKBitmap bitmap)
{
    using (bitmap)
    using (var image = SKImage.FromBitmap(bitmap))
    using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        return data.ToArray();
}

static byte[] BuildIco(IEnumerable<(int Size, byte[] Png)> images)
{
    var entries = images.ToList();
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);              // reserved
    writer.Write((ushort)1);              // type: icon
    writer.Write((ushort)entries.Count);
    var offset = 6 + 16 * entries.Count;
    foreach (var (size, png) in entries)
    {
        writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);            // palette colours
        writer.Write((byte)0);            // reserved
        writer.Write((ushort)1);          // colour planes
        writer.Write((ushort)32);         // bits per pixel
        writer.Write(png.Length);
        writer.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in entries)
        writer.Write(png);
    return stream.ToArray();
}

static byte[] BuildIcns(IEnumerable<(string Type, byte[] Png)> images)
{
    var entries = images.ToList();
    using var stream = new MemoryStream();
    void WriteBigEndian(int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    stream.Write("icns"u8);
    WriteBigEndian(8 + entries.Sum(e => 8 + e.Png.Length));
    foreach (var (type, png) in entries)
    {
        stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
        WriteBigEndian(8 + png.Length);
        stream.Write(png);
    }
    return stream.ToArray();
}

static void Write(string path, byte[] bytes)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
    Console.WriteLine($"  {Path.GetRelativePath(Environment.CurrentDirectory, path)} ({bytes.Length:N0} bytes)");
}
