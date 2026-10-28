using System.Text;
using Avgit.Core.Diff;
using Avgit.Core.Model;

namespace Avgit.Tests;

public sealed class SyntheticDiffTests : IDisposable
{
    private static readonly DiffParseOptions Options = new()
    {
        Setup = new DiffSetup { Subject = DiffSubject.PendingFiles, Scope = DiffScope.WorkTreeVsIndex }
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avgit-synth-" + Guid.NewGuid().ToString("N"));

    public SyntheticDiffTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string WriteFile(string name, string content) => WriteFile(name, new UTF8Encoding(false).GetBytes(content));

    [Fact]
    public void LooksBinary_detects_nul_in_the_probe_window_only()
    {
        Assert.True(FileText.LooksBinary(WriteFile("nul.bin", [65, 0, 66])));
        Assert.False(FileText.LooksBinary(WriteFile("text.txt", "hello\nworld\n")));

        var late = new byte[FileText.BinaryProbeBytes + 10];
        Array.Fill(late, (byte)'a');
        late[^1] = 0;
        Assert.False(FileText.LooksBinary(WriteFile("late.txt", late)));

        Assert.False(FileText.LooksBinary(WriteFile("utf16.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hi\n")).ToArray())));
        Assert.False(FileText.LooksBinary(Path.Combine(_dir, "missing.txt")));
    }

    [Fact]
    public void ReadTextPrefix_honours_byte_order_marks_and_the_limit()
    {
        Assert.Equal("héllo", FileText.ReadTextPrefix(WriteFile("bom8.txt", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("héllo")]), 100));
        Assert.Equal("wide", FileText.ReadTextPrefix(WriteFile("le.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("wide")]), 100));
        Assert.Equal("big", FileText.ReadTextPrefix(WriteFile("be.txt", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("big")]), 100));
        Assert.Equal("abc", FileText.ReadTextPrefix(WriteFile("plain.txt", "abcdef"), 3));

        // "é" is two bytes in UTF-8; a limit that splits it drops the partial character.
        Assert.Equal("a", FileText.ReadTextPrefix(WriteFile("split.txt", "aé"), 2));
    }

    [Fact]
    public void Untracked_text_file_parses_as_a_new_file()
    {
        var path = WriteFile("notes.txt", "one\ntwo\nthree\n");
        var diff = SyntheticDiff.ForUntrackedFile("docs/notes.txt", path);

        Assert.Contains("new file mode 100644", diff);
        Assert.Contains("--- /dev/null", diff);
        Assert.Contains("+++ b/docs/notes.txt", diff);
        Assert.EndsWith("\n", diff);

        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, Options));
        Assert.Equal("docs/notes.txt", file.FileName);
        Assert.Equal(PathState.New, file.State);
        Assert.Equal(3, file.AddedLines);
        Assert.False(file.Truncated);
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.Equal("@@ -0,0 +1,3 @@", hunk.RawText);
        Assert.Equal(["one", "two", "three"], hunk.LineItems.Select(l => l.RawText));
    }

    [Fact]
    public void Untracked_file_without_trailing_newline_gets_the_marker()
    {
        var path = WriteFile("tail.txt", "a\r\nb");
        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(SyntheticDiff.ForUntrackedFile("tail.txt", path), Options));
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.Equal(['+', '+', '\\'], hunk.LineItems.Select(l => l.Marker));
        Assert.Equal("a\r", hunk.LineItems[0].RawText);
        Assert.Equal(2, file.AddedLines);
    }

    [Fact]
    public void Untracked_file_with_quoted_path_round_trips_the_name()
    {
        var path = WriteFile("q.txt", "x\n");
        var diff = SyntheticDiff.ForUntrackedFile("café dir/q.txt", path);
        Assert.Contains("\"b/caf\\303\\251 dir/q.txt\"", diff);
        Assert.Equal("café dir/q.txt", Assert.Single(UnifiedDiffParser.Parse(diff, Options)).FileName);
    }

    [Fact]
    public void Untracked_binary_and_empty_files()
    {
        var binary = SyntheticDiff.ForUntrackedFile("image.png", WriteFile("image.png", [0x89, 0x50, 0, 0, 1]));
        Assert.Contains("Binary files /dev/null and b/image.png differ", binary);
        FileDiff parsed = Assert.Single(UnifiedDiffParser.Parse(binary, Options));
        Assert.True(parsed.Binary);
        Assert.Equal(PathState.New, parsed.State);
        Assert.Equal("image.png", parsed.FileName);

        FileDiff empty = Assert.Single(UnifiedDiffParser.Parse(SyntheticDiff.ForUntrackedFile("empty.txt", WriteFile("empty.txt", [])), Options));
        Assert.Equal(PathState.New, empty.State);
        Assert.Equal("empty.txt", empty.FileName);
        Assert.Empty(empty.Hunks);
        Assert.Contains("empty", empty.EmptyMessage);
    }

    [Fact]
    public void Untracked_file_over_max_bytes_is_truncated()
    {
        var content = string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i:D3}\n")); // 9 bytes per line
        var path = WriteFile("big.txt", content);
        var diff = SyntheticDiff.ForUntrackedFile("big.txt", path, maxBytes: 40);

        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, Options));
        Assert.True(file.Truncated);
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.True(hunk.Truncated);
        Assert.Equal(["line 000", "line 001", "line 002", "line 003"], hunk.LineItems.Select(l => l.RawText));
        Assert.DoesNotContain(hunk.LineItems, l => l.Marker == '\\');
        Assert.Equal("First 4 line(s)", hunk.Caption);
    }

    [Fact]
    public void Content_file_shows_context_lines_with_exact_raw_text()
    {
        var path = WriteFile("content.txt", "alpha\r\n\tbeta\ngamma");
        FileDiff file = SyntheticDiff.CreateContentFile("dir\\content.txt", path, Options.Setup);

        Assert.Equal("dir/content.txt", file.FileName);
        Assert.Equal(PathState.Unchanged, file.State);
        Assert.False(file.Truncated);
        Assert.False(file.Binary);
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.Equal((1, 3, 1, 3), (hunk.OldStart, hunk.OldEnd, hunk.NewStart, hunk.NewEnd));
        Assert.Equal("@@ -1,3 +1,3 @@", hunk.RawText);
        Assert.Equal([' ', ' ', ' ', '\\'], hunk.LineItems.Select(l => l.Marker));
        Assert.Equal(["alpha\r", "\tbeta", "gamma"], hunk.LineItems.Take(3).Select(l => l.RawText));
        Assert.Equal("    beta", hunk.LineItems[1].Text);
        Assert.Equal("alpha", hunk.LineItems[0].Text);
        Assert.Equal(["1", "2", "3", ""], hunk.LineItems.Select(l => l.OldLineNo));
        Assert.Equal(["1", "2", "3", ""], hunk.LineItems.Select(l => l.NewLineNo));
        Assert.Contains("Unmodified", file.Caption);
        Assert.Contains("Whole file", hunk.Caption);
    }

    [Fact]
    public void Content_file_honours_max_lines()
    {
        var path = WriteFile("long.txt", string.Concat(Enumerable.Range(1, 50).Select(i => $"{i}\n")));
        FileDiff file = SyntheticDiff.CreateContentFile("long.txt", path, Options.Setup, maxLines: 10);
        Assert.True(file.Truncated);
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.True(hunk.Truncated);
        Assert.Equal(10, hunk.LineItems.Count);
        Assert.Equal("10", hunk.LineItems[^1].NewLineNo);

        FileDiff exact = SyntheticDiff.CreateContentFile("long.txt", path, Options.Setup, maxLines: 50);
        Assert.False(exact.Truncated);
        Assert.Equal(50, exact.Hunks[0].LineItems.Count);
    }

    [Fact]
    public void Content_file_detects_binary_and_handles_missing_files()
    {
        FileDiff binary = SyntheticDiff.CreateContentFile("x.bin", WriteFile("x.bin", [1, 0, 2]), Options.Setup);
        Assert.True(binary.Binary);
        Assert.Empty(binary.Hunks);
        Assert.Contains("Binary", binary.EmptyMessage);

        FileDiff missing = SyntheticDiff.CreateContentFile("nope.txt", Path.Combine(_dir, "nope.txt"), Options.Setup);
        Assert.Empty(missing.Hunks);
        Assert.False(string.IsNullOrEmpty(missing.EmptyMessage));
    }
}
