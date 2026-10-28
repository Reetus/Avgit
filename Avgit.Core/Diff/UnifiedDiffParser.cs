using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using Avgit.Core.Model;

namespace Avgit.Core.Diff;

/// <summary>Limits and display settings for <see cref="UnifiedDiffParser.Parse"/>.</summary>
public sealed class DiffParseOptions
{
    public int MaxFiles { get; init; } = 50;
    public int MaxLines { get; init; } = 2000;
    public int MaxLinesPerHunk { get; init; } = 500;
    public DiffSetup Setup { get; init; }
    public bool ConvertTabsToSpaces { get; init; } = true;
    public int TabSize { get; init; } = 4;
}

/// <summary>
/// Parses git's unified diff output (<c>git diff</c>, <c>git show</c>) into
/// <see cref="FileDiff"/> objects.
/// </summary>
/// <remarks>
/// <para>
/// The parser makes one forward pass over the text, splitting on <c>\n</c> only,
/// so a CRLF file's trailing <c>\r</c> survives in <see cref="HunkLine.RawText"/>.
/// Hunk bodies are consumed by the line counts in their <c>@@</c> header, so
/// content lines that look like headers (<c>--- x</c>, <c>diff --git</c>) are
/// never misread. A hunk that ends before its counts are used up (for example
/// because the input was cut off) is marked <see cref="IHunk.Truncated"/>.
/// </para>
/// <para>
/// Limits: parsing stops before the file that would exceed
/// <see cref="DiffParseOptions.MaxFiles"/>. Lines beyond
/// <see cref="DiffParseOptions.MaxLinesPerHunk"/> are skipped and the hunk and
/// its file are marked truncated. Once <see cref="DiffParseOptions.MaxLines"/>
/// lines have been stored in total, no further lines are stored: the current
/// hunk and file are marked truncated, later hunks are omitted, and later files
/// are still returned (name, state, modes, binary flag) without hunks and marked
/// truncated when they had any.
/// </para>
/// </remarks>
public static class UnifiedDiffParser
{
    /// <summary>Parses <paramref name="diff"/> into one <see cref="FileDiff"/> per file, in order.</summary>
    public static List<FileDiff> Parse(string diff, DiffParseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Session(diff ?? string.Empty, options).Run();
    }

    private const string DevNull = "/dev/null";

    private static readonly SearchValues<char> DisplaySpecials =
        SearchValues.Create("\r\t‪‫‬‭‮⁦⁧⁨⁩‎‏؜");

    /// <summary>Header facts collected for the file being parsed.</summary>
    private sealed class FileState(FileDiff file, string? diffGitPaths)
    {
        public FileDiff File { get; } = file;
        public string? DiffGitPaths { get; } = diffGitPaths;
        public string? MinusPath { get; set; }
        public string? PlusPath { get; set; }
        public string? RenameFrom { get; set; }
        public string? RenameTo { get; set; }
        public string? CopyFrom { get; set; }
        public string? CopyTo { get; set; }
        public bool NewFile { get; set; }
        public bool Deleted { get; set; }
        public bool Gitlink { get; set; }
        public bool Combined { get; set; }
        public int OldMode { get; set; }
        public int NewMode { get; set; }
        public int Added { get; set; }
        public int Removed { get; set; }
        public bool SawHunk { get; set; }
        public List<IHunk> Hunks { get; } = [];
    }

    private sealed class Session(string text, DiffParseOptions options)
    {
        private readonly List<FileDiff> _files = [];
        private FileState? _file;

        // Hunk body state. _inHunk is true while lines belong to a hunk body;
        // _hunk is null when the body is being skipped because of the limits.
        private bool _inHunk;
        private Hunk? _hunk;
        private List<HunkLine>? _lines;
        private int _oldLeft;
        private int _newLeft;
        private int _oldNo;
        private int _newNo;
        private int _hunkLineCount;
        private bool _lastLineKept;

        private int _totalLines;
        private bool _globalLimitHit;

        public List<FileDiff> Run()
        {
            var pos = 0;
            while (pos < text.Length)
            {
                var start = pos;
                var newline = text.IndexOf('\n', pos);
                var end = newline < 0 ? text.Length : newline;
                pos = newline < 0 ? text.Length : newline + 1;

                if (_inHunk && IsBodyLine(start, end))
                {
                    ConsumeBodyLine(start, end);
                    continue;
                }

                if (_inHunk)
                {
                    CloseHunk();
                }

                if (!HandleHeaderLine(start, end))
                {
                    _file = null;
                    return _files;
                }
            }

            if (_inHunk)
            {
                CloseHunk();
            }

            FinishFile();
            return _files;
        }

        private bool IsBodyLine(int start, int end)
        {
            if (_oldLeft > 0 || _newLeft > 0)
            {
                return end == start || text[start] is ' ' or '+' or '-' or '\\' || (end - start == 1 && text[start] == '\r');
            }

            // Counts are used up: only a "\ No newline at end of file" marker can follow.
            return end > start && text[start] == '\\';
        }

        private void ConsumeBodyLine(int start, int end)
        {
            var marker = end > start ? text[start] : ' ';
            if (marker == '\\')
            {
                if (_hunk is not null && _lastLineKept)
                {
                    var raw = text.Substring(start + 1, end - start - 1);
                    _lines!.Add(new HunkLine { Marker = '\\', RawText = raw, Text = ToDisplay(raw.TrimEnd('\r')) });
                }

                return;
            }

            var contentStart = start + 1;
            if (marker is not (' ' or '+' or '-'))
            {
                // A line holding only "\r": an empty context line in a CRLF-converted diff.
                marker = ' ';
                contentStart = start;
            }

            var oldNo = marker != '+' ? _oldNo++ : 0;
            var newNo = marker != '-' ? _newNo++ : 0;
            if (marker != '+')
            {
                _oldLeft--;
            }

            if (marker != '-')
            {
                _newLeft--;
            }

            _hunkLineCount++;
            if (_hunk is null)
            {
                _lastLineKept = false;
                return;
            }

            if (_hunkLineCount > options.MaxLinesPerHunk || _totalLines >= options.MaxLines)
            {
                if (_totalLines >= options.MaxLines)
                {
                    _globalLimitHit = true;
                }

                MarkTruncated();
                _lastLineKept = false;
                return;
            }

            _totalLines++;
            var content = contentStart < end ? text.Substring(contentStart, end - contentStart) : string.Empty;
            var line = new HunkLine
            {
                Marker = marker,
                RawText = content,
                Text = ToDisplay(content),
                OldLineNo = oldNo > 0 ? oldNo.ToString(CultureInfo.InvariantCulture) : string.Empty,
                NewLineNo = newNo > 0 ? newNo.ToString(CultureInfo.InvariantCulture) : string.Empty
            };
            _lines!.Add(line);
            _lastLineKept = true;

            var file = _file!;
            if (marker == '+')
            {
                file.Added++;
            }
            else if (marker == '-')
            {
                file.Removed++;
            }

            if (marker != ' ' && content.StartsWith("Subproject commit ", StringComparison.Ordinal))
            {
                file.Gitlink = true;
            }
        }

        private void MarkTruncated()
        {
            if (_hunk is not null)
            {
                _hunk.Truncated = true;
            }

            if (_file is not null)
            {
                _file.File.Truncated = true;
            }
        }

        private void CloseHunk()
        {
            if (_oldLeft > 0 || _newLeft > 0)
            {
                // The hunk ended before its header's line counts were used up.
                MarkTruncated();
            }

            if (_hunk is not null)
            {
                _hunk.LineItems = new ObservableCollection<HunkLine>(_lines!);
                _file!.Hunks.Add(_hunk);
            }

            _inHunk = false;
            _hunk = null;
            _lines = null;
        }

        /// <summary>Handles a line outside hunk bodies. Returns false to stop parsing.</summary>
        private bool HandleHeaderLine(int start, int end)
        {
            var line = text.AsSpan(start, end - start);
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            if (line.StartsWith("diff --git "))
            {
                return StartFile(line["diff --git ".Length..].ToString(), combined: false);
            }

            if (line.StartsWith("diff --cc ") || line.StartsWith("diff --combined "))
            {
                var rest = line[(line.IndexOf("--", StringComparison.Ordinal) + 2)..];
                rest = rest[(rest.IndexOf(' ') + 1)..];
                if (!StartFile(null, combined: true))
                {
                    return false;
                }

                _file!.PlusPath = TextUtil.UnquoteGitPath(rest.ToString());
                return true;
            }

            if (line.StartsWith("--- ") && (_file is null || _file.SawHunk || _file.MinusPath is not null))
            {
                // A plain unified diff without a "diff --git" line.
                if (!StartFile(null, combined: false))
                {
                    return false;
                }
            }

            FileState? file = _file;
            if (file is null)
            {
                return true; // Preamble such as commit headers.
            }

            if (line.StartsWith("@@"))
            {
                OpenHunk(line);
                return true;
            }

            if (file.SawHunk)
            {
                return true;
            }

            if (line.StartsWith("--- "))
            {
                file.MinusPath = ParseMarkerPath(line[4..], 'a');
            }
            else if (line.StartsWith("+++ "))
            {
                file.PlusPath = ParseMarkerPath(line[4..], 'b');
            }
            else if (line.StartsWith("index "))
            {
                var space = line.LastIndexOf(' ');
                if (space > "index ".Length)
                {
                    var mode = ParseMode(line[(space + 1)..]);
                    if (mode != 0)
                    {
                        if (file.OldMode == 0 && !file.NewFile)
                        {
                            file.OldMode = mode;
                        }

                        if (file.NewMode == 0 && !file.Deleted)
                        {
                            file.NewMode = mode;
                        }
                    }
                }
            }
            else if (line.StartsWith("old mode "))
            {
                file.OldMode = ParseMode(line["old mode ".Length..]);
            }
            else if (line.StartsWith("new mode "))
            {
                file.NewMode = ParseMode(line["new mode ".Length..]);
            }
            else if (line.StartsWith("new file mode "))
            {
                file.NewFile = true;
                file.NewMode = ParseMode(line["new file mode ".Length..]);
            }
            else if (line.StartsWith("deleted file mode "))
            {
                file.Deleted = true;
                file.OldMode = ParseMode(line["deleted file mode ".Length..]);
            }
            else if (line.StartsWith("rename from "))
            {
                file.RenameFrom = TextUtil.UnquoteGitPath(line["rename from ".Length..].ToString());
            }
            else if (line.StartsWith("rename to "))
            {
                file.RenameTo = TextUtil.UnquoteGitPath(line["rename to ".Length..].ToString());
            }
            else if (line.StartsWith("rename old "))
            {
                file.RenameFrom = TextUtil.UnquoteGitPath(line["rename old ".Length..].ToString());
            }
            else if (line.StartsWith("rename new "))
            {
                file.RenameTo = TextUtil.UnquoteGitPath(line["rename new ".Length..].ToString());
            }
            else if (line.StartsWith("copy from "))
            {
                file.CopyFrom = TextUtil.UnquoteGitPath(line["copy from ".Length..].ToString());
            }
            else if (line.StartsWith("copy to "))
            {
                file.CopyTo = TextUtil.UnquoteGitPath(line["copy to ".Length..].ToString());
            }
            else if (line.StartsWith("Binary files ") || line.SequenceEqual("GIT binary patch"))
            {
                file.File.Binary = true;
            }

            return true;
        }

        private bool StartFile(string? diffGitPaths, bool combined)
        {
            FinishFile();
            if (_files.Count >= options.MaxFiles)
            {
                return false;
            }

            _file = new FileState(new FileDiff(options.Setup), diffGitPaths) { Combined = combined };
            return true;
        }

        private void OpenHunk(ReadOnlySpan<char> header)
        {
            FileState file = _file!;
            file.SawHunk = true;
            if (file.Combined || !TryParseHunkHeader(header, out var oldStart, out var oldCount, out var newStart, out var newCount, out var annotation))
            {
                // Combined ("@@@") or malformed headers are not rendered.
                return;
            }

            _inHunk = true;
            _oldLeft = oldCount;
            _newLeft = newCount;
            _oldNo = oldStart;
            _newNo = newStart;
            _hunkLineCount = 0;
            _lastLineKept = false;

            if (_globalLimitHit)
            {
                _hunk = null;
                _lines = null;
                if (oldCount > 0 || newCount > 0)
                {
                    file.File.Truncated = true;
                }

                return;
            }

            _lines = [];
            _hunk = new Hunk(file.File)
            {
                Number = file.Hunks.Count + 1,
                OldStart = oldStart,
                OldEnd = oldCount == 0 ? 0 : oldStart + oldCount - 1,
                NewStart = newStart,
                NewEnd = newCount == 0 ? 0 : newStart + newCount - 1,
                RawText = header.ToString(),
                Annotation = annotation
            };
        }

        private void FinishFile()
        {
            FileState? state = _file;
            if (state is null)
            {
                return;
            }

            _file = null;
            string? oldPath = state.RenameFrom ?? state.CopyFrom ?? (state.MinusPath is DevNull ? null : state.MinusPath);
            string? newPath = state.RenameTo ?? state.CopyTo ?? (state.PlusPath is DevNull ? null : state.PlusPath);
            if (oldPath is null && newPath is null && state.DiffGitPaths is not null)
            {
                (oldPath, newPath) = ParseDiffGitPaths(state.DiffGitPaths);
            }

            PathState pathState;
            if (state.NewFile || (state.MinusPath is DevNull && state.PlusPath is not DevNull))
            {
                pathState = PathState.New;
            }
            else if (state.Deleted || state.PlusPath is DevNull)
            {
                pathState = PathState.Removed;
            }
            else if (state.RenameFrom is not null || state.RenameTo is not null)
            {
                pathState = PathState.Moved;
            }
            else if (state.CopyFrom is not null || state.CopyTo is not null)
            {
                pathState = PathState.Duplicated;
            }
            else if (state.Gitlink || state.OldMode == 160000 || state.NewMode == 160000)
            {
                pathState = PathState.SubmoduleCommitChanged;
            }
            else
            {
                pathState = PathState.Edited;
            }

            FileDiff file = state.File;
            file.FileName = (pathState == PathState.Removed ? oldPath ?? newPath : newPath ?? oldPath) ?? string.Empty;
            file.OldPath = pathState is PathState.Moved or PathState.Duplicated ? oldPath : null;
            file.State = pathState;
            file.OldMode = state.OldMode;
            file.NewMode = state.NewMode;
            file.AddedLines = state.Added;
            file.RemovedLines = state.Removed;
            if (state.Hunks.Count > 0)
            {
                file.Hunks = new ObservableCollection<IHunk>(state.Hunks);
            }

            _files.Add(file);
        }

        private string ToDisplay(string raw)
        {
            if (raw.AsSpan().IndexOfAny(DisplaySpecials) < 0)
            {
                return raw;
            }

            var display = raw.Contains('\r') ? raw.Replace("\r", string.Empty, StringComparison.Ordinal) : raw;
            display = TextUtil.StripBidiControls(display);
            return options.ConvertTabsToSpaces ? TextUtil.ConvertTabsToSpaces(display, options.TabSize) : display;
        }
    }

    /// <summary>
    /// Parses <c>@@ -a[,b] +c[,d] @@[ annotation]</c>. An omitted count means 1.
    /// </summary>
    private static bool TryParseHunkHeader(ReadOnlySpan<char> header, out int oldStart, out int oldCount,
        out int newStart, out int newCount, out string? annotation)
    {
        oldStart = oldCount = newStart = newCount = 0;
        annotation = null;
        if (!header.StartsWith("@@ -"))
        {
            return false;
        }

        var i = 4;
        if (!TryParseRange(header, ref i, out oldStart, out oldCount))
        {
            return false;
        }

        if (i + 1 >= header.Length || header[i] != ' ' || header[i + 1] != '+')
        {
            return false;
        }

        i += 2;
        if (!TryParseRange(header, ref i, out newStart, out newCount))
        {
            return false;
        }

        var rest = header[i..];
        if (!rest.StartsWith(" @@"))
        {
            return false;
        }

        var text = rest[3..].Trim();
        if (text.Length > 0)
        {
            annotation = TextUtil.StripBidiControls(text.ToString());
        }

        return true;
    }

    private static bool TryParseRange(ReadOnlySpan<char> s, ref int i, out int start, out int count)
    {
        start = 0;
        count = 1;
        if (!TryParseNumber(s, ref i, out start))
        {
            return false;
        }

        if (i < s.Length && s[i] == ',')
        {
            i++;
            return TryParseNumber(s, ref i, out count);
        }

        return true;
    }

    private static bool TryParseNumber(ReadOnlySpan<char> s, ref int i, out int value)
    {
        value = 0;
        var begin = i;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            value = (value * 10) + (s[i] - '0');
            i++;
        }

        return i > begin;
    }

    /// <summary>Parses an octal mode string such as <c>100644</c> into an int with the same digits.</summary>
    private static int ParseMode(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        foreach (var c in text)
        {
            if (c is < '0' or > '7')
            {
                return 0;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var mode) ? mode : 0;
    }

    /// <summary>
    /// Reads the path from a <c>---</c>/<c>+++</c> line: C-unquoted, with git's
    /// trailing tab (added after names containing spaces) removed and the
    /// <c>a/</c> or <c>b/</c> prefix stripped. Returns <see cref="DevNull"/> for
    /// a missing side.
    /// </summary>
    private static string ParseMarkerPath(ReadOnlySpan<char> rest, char prefix)
    {
        string path;
        if (rest.Length > 0 && rest[0] == '"')
        {
            var s = rest.ToString();
            path = TextUtil.ReadQuoted(s, 0, out _) ?? s;
        }
        else
        {
            var tab = rest.IndexOf('\t');
            if (tab >= 0)
            {
                rest = rest[..tab];
            }

            if (rest.SequenceEqual(DevNull))
            {
                return DevNull;
            }

            path = rest.ToString();
        }

        return StripPrefix(path, prefix);
    }

    private static string StripPrefix(string path, char prefix)
        => path.Length > 2 && path[0] == prefix && path[1] == '/' ? path[2..] : path;

    /// <summary>
    /// Splits the paths of a <c>diff --git a/X b/Y</c> line. Used only when no
    /// other header names the file (mode-only changes, binary files, empty new
    /// files). Unquoted paths are ambiguous when they contain " b/"; for a
    /// non-rename both sides are equal, which resolves it.
    /// </summary>
    private static (string? Old, string? New) ParseDiffGitPaths(string rest)
    {
        string? a;
        string? b;
        if (rest.StartsWith('"'))
        {
            a = TextUtil.ReadQuoted(rest, 0, out var end);
            var second = end < rest.Length ? rest[(end + 1)..] : string.Empty;
            b = second.StartsWith('"') ? TextUtil.ReadQuoted(second, 0, out _) : second;
        }
        else if (rest.EndsWith('"') && rest.IndexOf(" \"", StringComparison.Ordinal) is var quote and >= 0)
        {
            a = rest[..quote];
            b = TextUtil.ReadQuoted(rest, quote + 1, out _);
        }
        else if ((rest.Length - 1) % 2 == 0 && rest.Length > 5
                 && rest[(rest.Length - 1) / 2] == ' '
                 && rest.AsSpan(2, (rest.Length - 1) / 2 - 2).SequenceEqual(rest.AsSpan(((rest.Length - 1) / 2) + 3)))
        {
            var half = (rest.Length - 1) / 2;
            a = rest[..half];
            b = rest[(half + 1)..];
        }
        else
        {
            var split = rest.IndexOf(" b/", StringComparison.Ordinal);
            if (split < 0)
            {
                return (null, null);
            }

            a = rest[..split];
            b = rest[(split + 1)..];
        }

        return (a is null ? null : StripPrefix(a, 'a'), b is null ? null : StripPrefix(b, 'b'));
    }
}
