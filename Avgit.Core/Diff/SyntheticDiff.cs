using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avgit.Core.Model;

namespace Avgit.Core.Diff;

/// <summary>
/// Diffs git does not produce itself: untracked files (shown as new-file diffs)
/// and a file's current content (shown as context lines).
/// </summary>
public static class SyntheticDiff
{
    private const string NoNewline = " No newline at end of file";

    /// <summary>
    /// Builds unified diff text equivalent to <c>git diff</c> of a newly added
    /// file, which <see cref="UnifiedDiffParser"/> parses as <see cref="PathState.New"/>.
    /// </summary>
    /// <remarks>
    /// Binary files produce a <c>Binary files ... differ</c> body. When the file is
    /// larger than <paramref name="maxBytes"/>, only whole lines from its first
    /// <paramref name="maxBytes"/> bytes are included and the hunk header claims
    /// one more line than is present, so the parser marks the hunk and file
    /// truncated (and the patch builder refuses to build from it).
    /// </remarks>
    public static string ForUntrackedFile(string repositoryRelativePath, string absolutePath, int maxBytes = 2 * 1024 * 1024)
    {
        var path = repositoryRelativePath.Replace('\\', '/');
        var builder = new StringBuilder();
        builder.Append("diff --git ").Append(TextUtil.QuoteGitPath("a/" + path)).Append(' ')
            .Append(TextUtil.QuoteGitPath("b/" + path)).Append('\n');
        builder.Append("new file mode ").Append(FileText.GetMode(absolutePath)).Append('\n');

        if (FileText.LooksBinary(absolutePath))
        {
            builder.Append("Binary files /dev/null and ").Append(TextUtil.QuoteGitPath("b/" + path)).Append(" differ\n");
            return builder.ToString();
        }

        var limit = Math.Max(maxBytes, 0);
        var truncated = FileText.GetLength(absolutePath) > limit;
        var content = FileText.ReadTextPrefix(absolutePath, limit);
        List<string> lines = FileText.SplitKeepingCarriageReturns(content, out var endsWithNewline);
        if (truncated && !endsWithNewline && lines.Count > 1)
        {
            // Drop the partial last line so only whole lines are shown.
            lines.RemoveAt(lines.Count - 1);
            endsWithNewline = true;
        }

        if (lines.Count == 0 && !truncated)
        {
            return builder.ToString(); // Empty file: git prints the header only.
        }

        var count = lines.Count + (truncated ? 1 : 0);
        builder.Append("--- /dev/null\n");
        builder.Append("+++ ").Append(TextUtil.QuoteGitPath("b/" + path)).Append('\n');
        builder.Append("@@ -0,0 +1");
        if (count != 1)
        {
            builder.Append(',').Append(count.ToString(CultureInfo.InvariantCulture));
        }

        builder.Append(" @@\n");
        foreach (var line in lines)
        {
            builder.Append('+').Append(line).Append('\n');
        }

        if (!endsWithNewline && !truncated && lines.Count > 0)
        {
            builder.Append('\\').Append(NoNewline).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds a <see cref="FileDiff"/> that shows a file's current content as
    /// context lines (<see cref="PathState.Unchanged"/>, one hunk), up to
    /// <paramref name="maxLines"/> lines. Binary files get <see cref="FileDiff.Binary"/>
    /// and no hunks; a missing or unreadable file gets no hunks.
    /// </summary>
    public static FileDiff CreateContentFile(string repositoryRelativePath, string absolutePath, DiffSetup environment, int maxLines = 500)
    {
        var file = new FileDiff(environment)
        {
            FileName = repositoryRelativePath.Replace('\\', '/'),
            State = PathState.Unchanged
        };

        if (!File.Exists(absolutePath))
        {
            return file;
        }

        if (FileText.LooksBinary(absolutePath))
        {
            file.Binary = true;
            return file;
        }

        List<string> lines;
        bool endsWithNewline;
        bool truncated;
        try
        {
            lines = FileText.ReadLines(absolutePath, Math.Max(maxLines, 0), out endsWithNewline, out truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return file;
        }

        file.Truncated = truncated;
        if (lines.Count == 0)
        {
            return file;
        }

        var items = new ObservableCollection<HunkLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            var number = (i + 1).ToString(CultureInfo.InvariantCulture);
            items.Add(new HunkLine
            {
                Marker = ' ',
                RawText = lines[i],
                Text = TextUtil.ConvertTabsToSpaces(TextUtil.StripBidiControls(lines[i].Replace("\r", string.Empty, StringComparison.Ordinal)), 4),
                OldLineNo = number,
                NewLineNo = number
            });
        }

        if (!endsWithNewline && !truncated)
        {
            items.Add(new HunkLine { Marker = '\\', RawText = NoNewline, Text = NoNewline });
        }

        var count = lines.Count.ToString(CultureInfo.InvariantCulture);
        file.Hunks =
        [
            new Hunk(file)
            {
                Number = 1,
                OldStart = 1,
                OldEnd = lines.Count,
                NewStart = 1,
                NewEnd = lines.Count,
                RawText = lines.Count == 1 ? "@@ -1 +1 @@" : $"@@ -1,{count} +1,{count} @@",
                Truncated = truncated,
                LineItems = items
            }
        ];
        return file;
    }
}

/// <summary>Reading helpers for working-tree files shown in synthetic diffs.</summary>
public static class FileText
{
    /// <summary>How many leading bytes the binary check inspects (git uses the same amount).</summary>
    public const int BinaryProbeBytes = 8000;

    /// <summary>
    /// True when the file looks binary: a NUL byte within its first
    /// <see cref="BinaryProbeBytes"/> bytes, unless it starts with a UTF-16 or
    /// UTF-32 byte-order mark. Only that prefix is read. Unreadable files are
    /// reported as not binary.
    /// </summary>
    public static bool LooksBinary(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[BinaryProbeBytes];
            var read = ReadFully(stream, buffer);
            var bytes = buffer.AsSpan(0, read);
            if (DetectBom(bytes, out _) is { } encoding && encoding is not UTF8Encoding)
            {
                return false;
            }

            return bytes.Contains((byte)0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes up to <paramref name="maxBytes"/> bytes from the start of the file.
    /// The encoding is taken from a UTF-8, UTF-16 or UTF-32 byte-order mark
    /// (which is not included in the result), otherwise UTF-8. A multi-byte
    /// character cut by the limit is dropped. Unreadable files give an empty string.
    /// </summary>
    public static string ReadTextPrefix(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = (int)Math.Min(Math.Max(maxBytes, 0), stream.Length);
            var buffer = new byte[size];
            var read = ReadFully(stream, buffer);
            var bytes = buffer.AsSpan(0, read);
            Encoding encoding = DetectBom(bytes, out var bomLength) ?? new UTF8Encoding(false);
            bytes = bytes[bomLength..];
            var complete = stream.Position >= stream.Length;
            Decoder decoder = encoding.GetDecoder();
            var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
            var count = decoder.GetChars(bytes, chars, flush: complete);
            return new string(chars, 0, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>The file's size in bytes, or 0 when it cannot be read.</summary>
    internal static long GetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>git's mode string for a new regular file: 100755 when executable (Unix only), else 100644.</summary>
    internal static string GetMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return "100644";
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode & UnixFileMode.UserExecute) != 0 ? "100755" : "100644";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "100644";
        }
    }

    /// <summary>
    /// Splits on <c>\n</c> only, keeping any <c>\r</c> in the lines so they can be
    /// written back byte-exact. No trailing empty element for a final newline.
    /// </summary>
    internal static List<string> SplitKeepingCarriageReturns(string text, out bool endsWithNewline)
    {
        var lines = new List<string>();
        var pos = 0;
        while (pos < text.Length)
        {
            var newline = text.IndexOf('\n', pos);
            if (newline < 0)
            {
                lines.Add(text[pos..]);
                break;
            }

            lines.Add(text[pos..newline]);
            pos = newline + 1;
        }

        endsWithNewline = text.Length == 0 || text[^1] == '\n';
        return lines;
    }

    /// <summary>
    /// Reads up to <paramref name="maxLines"/> lines (split on <c>\n</c>, <c>\r</c>
    /// kept) without reading the rest of the file. <paramref name="truncated"/>
    /// is true when more content follows.
    /// </summary>
    internal static List<string> ReadLines(string path, int maxLines, out bool endsWithNewline, out bool truncated)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
        var lines = new List<string>();
        var current = new StringBuilder();
        var buffer = new char[64 * 1024];
        truncated = false;
        endsWithNewline = true;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n')
                {
                    continue;
                }

                if (lines.Count >= maxLines)
                {
                    truncated = true;
                    return lines;
                }

                current.Append(buffer, start, i - start);
                lines.Add(current.ToString());
                current.Clear();
                start = i + 1;
            }

            if (start < read)
            {
                if (lines.Count >= maxLines)
                {
                    truncated = true;
                    return lines;
                }

                current.Append(buffer, start, read - start);
            }
        }

        if (current.Length > 0)
        {
            if (lines.Count >= maxLines)
            {
                truncated = true;
                return lines;
            }

            lines.Add(current.ToString());
            endsWithNewline = false;
        }

        return lines;
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        return total;
    }

    private static Encoding? DetectBom(ReadOnlySpan<byte> bytes, out int length)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            length = 3;
            return new UTF8Encoding(false);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            length = 4;
            return new UTF32Encoding(bigEndian: false, byteOrderMark: false);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            length = 4;
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            length = 2;
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            length = 2;
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
        }

        length = 0;
        return null;
    }
}
