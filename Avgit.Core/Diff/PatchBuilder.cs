using System.Globalization;
using System.Text;
using Avgit.Core.Model;

namespace Avgit.Core.Diff;

/// <summary>
/// Builds unified-diff text for <c>git apply</c> from parsed hunks: whole-hunk
/// and selected-line patches for staging, unstaging and discarding, selected-line
/// patches for new files, and whole-file serialisation of a <see cref="FileDiff"/>.
/// Every patch is built from <see cref="HunkLine.RawText"/> and ends with <c>\n</c>.
/// </summary>
public static class PatchBuilder
{
    private const string DefaultFileMode = "100644";
    private const string DevNull = "/dev/null";
    private const string NoNewlineText = " No newline at end of file";

    /// <summary>
    /// Builds one patch for the lines at <paramref name="selectedLines"/> (indexes
    /// into <see cref="IHunk.LineItems"/>) of <paramref name="hunk"/>.
    /// </summary>
    /// <param name="hunk">The hunk the selection belongs to.</param>
    /// <param name="selectedLines">Indexes of the selected lines. Out-of-range indexes are ignored.</param>
    /// <param name="isStaging">
    /// True for a patch applied forwards (<c>git apply --cached</c>); false for one
    /// applied in reverse (<c>--cached -R</c> to unstage, <c>-R</c> to discard).
    /// </param>
    /// <returns>The patch text, or null when the hunk is truncated or the selection has no changes.</returns>
    public static string? CreateSelectionPatch(IHunk hunk, IEnumerable<int> selectedLines, bool isStaging)
    {
        ArgumentNullException.ThrowIfNull(hunk);
        ArgumentNullException.ThrowIfNull(selectedLines);

        if (hunk.Truncated || hunk.Owner.Binary)
        {
            return null;
        }

        var selected = selectedLines as IReadOnlySet<int> ?? new HashSet<int>(selectedLines);
        List<PatchLine>? body = ConvertSelection(hunk.LineItems, selected, reverse: !isStaging);
        if (body is null)
        {
            return null;
        }

        body = FixMissingNewlines(body);

        var sb = new StringBuilder();
        AppendFileHeader(sb, hunk.Owner, includeFileLines: true);
        AppendHunk(sb, body, hunk);
        return sb.ToString();
    }

    /// <summary>
    /// Builds a patch for <paramref name="numLines"/> consecutive lines starting at
    /// <paramref name="startLine"/>. A convenience over <see cref="CreateSelectionPatch"/>;
    /// pass the full line range for a whole-hunk action.
    /// </summary>
    public static string? CreatePatchFromHunk(IHunk hunk, int startLine, int numLines, bool isStaging)
    {
        ArgumentNullException.ThrowIfNull(hunk);
        return CreateSelectionPatch(hunk, Enumerable.Range(startLine, Math.Max(0, numLines)), isStaging);
    }

    /// <summary>
    /// Builds a patch for the selected lines (<see cref="HunkLine.Selected"/>) of a
    /// new file, whose diff is a single all-added hunk.
    /// </summary>
    /// <param name="hunk">The new file's hunk.</param>
    /// <param name="forUnstage">
    /// False: a new-file patch, applied with <c>--cached</c>, that adds the file to the
    /// index containing only the selected lines. True: a patch applied with
    /// <c>--cached -R</c> that removes exactly the selected lines from the index copy.
    /// </param>
    /// <returns>The patch text, or null when the hunk is truncated or no added line is selected.</returns>
    public static string? CreateNewFileSelectionPatch(IHunk hunk, bool forUnstage)
    {
        ArgumentNullException.ThrowIfNull(hunk);

        var selected = new HashSet<int>();
        for (var i = 0; i < hunk.LineItems.Count; i++)
        {
            if (hunk.LineItems[i].Selected)
            {
                selected.Add(i);
            }
        }

        return CreateNewFileSelectionPatch(hunk, selected, forUnstage);
    }

    /// <summary>
    /// Builds a patch for the lines at <paramref name="selectedLines"/> of a new
    /// file's single all-added hunk. See <see cref="CreateNewFileSelectionPatch(IHunk, bool)"/>.
    /// </summary>
    public static string? CreateNewFileSelectionPatch(IHunk hunk, IEnumerable<int> selectedLines, bool forUnstage)
    {
        ArgumentNullException.ThrowIfNull(hunk);
        ArgumentNullException.ThrowIfNull(selectedLines);

        if (hunk.Truncated || hunk.Owner.Binary)
        {
            return null;
        }

        var selected = selectedLines as IReadOnlySet<int> ?? new HashSet<int>(selectedLines);
        IList<HunkLine> lines = hunk.LineItems;

        // Gather the file's lines (the '+' lines) in order with their no-newline flags.
        var fileLines = new List<(int Index, PatchLine Line)>();
        for (var i = 0; i < lines.Count; i++)
        {
            HunkLine line = lines[i];
            if (line.Marker == '\\')
            {
                if (fileLines.Count > 0 && fileLines[^1].Index == i - 1)
                {
                    fileLines[^1] = (fileLines[^1].Index, fileLines[^1].Line with { NoNewline = true, NoNewlineText = line.RawText });
                }

                continue;
            }

            if (line.Marker == '+')
            {
                fileLines.Add((i, new PatchLine('+', line.RawText)));
            }
        }

        if (!fileLines.Any(l => selected.Contains(l.Index)))
        {
            return null;
        }

        List<PatchLine> body;
        if (forUnstage)
        {
            // The index holds every line. The patch's new side is that content and its
            // old side is the content without the selected lines, so applying it in
            // reverse removes exactly the selection.
            body = [.. fileLines.Select(l => selected.Contains(l.Index) ? l.Line : l.Line with { Marker = ' ' })];
            body = FixMissingNewlines(body);
        }
        else
        {
            // Only the selected lines go into the new index entry. Once a line is not
            // the file's last, it is followed by others and must end with a newline.
            body = [.. fileLines.Where(l => selected.Contains(l.Index)).Select(l => l.Line)];
            for (var i = 0; i < body.Count - 1; i++)
            {
                body[i] = body[i] with { NoNewline = false };
            }
        }

        FileDiff owner = hunk.Owner;
        var path = owner.FileName;
        var sb = new StringBuilder();
        sb.Append("diff --git ").Append(Quote("a/" + path)).Append(' ').Append(Quote("b/" + path)).Append('\n');
        if (forUnstage)
        {
            // A plain modification of the existing index entry.
            AppendFileLine(sb, "---", "a/", path);
        }
        else
        {
            sb.Append("new file mode ").Append(FormatMode(owner.NewMode) ?? FormatMode(owner.OldMode) ?? DefaultFileMode).Append('\n');
            sb.Append("--- ").Append(DevNull).Append('\n');
        }

        AppendFileLine(sb, "+++", "b/", path);
        AppendHunkLines(sb, body, oldStart: 1, newStart: 1, annotation: null, oldStartWhenEmpty: 0, newStartWhenEmpty: 0);
        return sb.ToString();
    }

    /// <summary>
    /// Serialises a parsed <see cref="FileDiff"/> back into unified-diff text, so that
    /// parsing the result yields the same file name, state, hunks, markers and raw text.
    /// </summary>
    public static string CreatePatchFromFile(FileDiff file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var sb = new StringBuilder();
        var hasHunks = !file.Binary && file.Hunks.Count > 0;
        AppendFileHeader(sb, file, includeFileLines: hasHunks);

        if (file.Binary)
        {
            (var oldName, var newName) = GetSideNames(file);
            sb.Append("Binary files ").Append(oldName).Append(" and ").Append(newName).Append(" differ\n");
            return sb.ToString();
        }

        foreach (IHunk hunk in file.Hunks)
        {
            var body = new List<PatchLine>(hunk.LineItems.Count);
            foreach (HunkLine line in hunk.LineItems)
            {
                if (line.Marker == '\\')
                {
                    if (body.Count > 0)
                    {
                        body[^1] = body[^1] with { NoNewline = true, NoNewlineText = line.RawText };
                    }

                    continue;
                }

                body.Add(new PatchLine(line.Marker is '+' or '-' ? line.Marker : ' ', line.RawText));
            }

            AppendHunk(sb, body, hunk);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Applies the selection rules to a hunk's lines. Returns null when nothing is
    /// left to change.
    /// </summary>
    private static List<PatchLine>? ConvertSelection(IList<HunkLine> lines, IReadOnlySet<int> selected, bool reverse)
    {
        var body = new List<PatchLine>(lines.Count);
        var previousKept = false;
        var hasChange = false;

        for (var i = 0; i < lines.Count; i++)
        {
            HunkLine line = lines[i];
            char? marker;
            switch (line.Marker)
            {
                case '\\':
                    // The marker belongs to the line before it and follows that line's fate.
                    if (previousKept && body.Count > 0)
                    {
                        body[^1] = body[^1] with { NoNewline = true, NoNewlineText = line.RawText };
                    }

                    continue;
                case '+':
                    // Forwards an unselected addition simply isn't made; in reverse the
                    // line already exists on the target side, so it becomes context.
                    marker = selected.Contains(i) ? '+' : reverse ? ' ' : null;
                    break;
                case '-':
                    // Forwards an unselected removal leaves the line in place (context);
                    // in reverse the line is absent from the target side, so drop it.
                    marker = selected.Contains(i) ? '-' : reverse ? null : ' ';
                    break;
                default:
                    marker = ' ';
                    break;
            }

            previousKept = marker is not null;
            if (marker is { } m)
            {
                body.Add(new PatchLine(m, line.RawText));
                hasChange |= m != ' ';
            }
        }

        return hasChange ? body : null;
    }

    /// <summary>
    /// A "no newline at end of file" marker is only valid on the last line of each
    /// side it applies to. Converting a change line into context can leave such a
    /// line in the middle of one side; in that case the line is split into a removal
    /// and an addition so that only the side where it is still last keeps the marker.
    /// </summary>
    private static List<PatchLine> FixMissingNewlines(List<PatchLine> body)
    {
        var lastOld = body.FindLastIndex(l => l.Marker is ' ' or '-');
        var lastNew = body.FindLastIndex(l => l.Marker is ' ' or '+');
        if (!body.Any(l => l.NoNewline))
        {
            return body;
        }

        var result = new List<PatchLine>(body.Count + 2);
        for (var i = 0; i < body.Count; i++)
        {
            PatchLine line = body[i];
            if (!line.NoNewline)
            {
                result.Add(line);
                continue;
            }

            var isLastOld = i == lastOld;
            var isLastNew = i == lastNew;
            switch (line.Marker)
            {
                case ' ' when isLastOld && isLastNew:
                    result.Add(line);
                    break;
                case ' ' when isLastOld:
                    result.Add(line with { Marker = '-' });
                    result.Add(line with { Marker = '+', NoNewline = false });
                    break;
                case ' ' when isLastNew:
                    result.Add(line with { Marker = '-', NoNewline = false });
                    result.Add(line with { Marker = '+' });
                    break;
                case '-' when isLastOld:
                case '+' when isLastNew:
                    result.Add(line);
                    break;
                default:
                    result.Add(line with { NoNewline = false });
                    break;
            }
        }

        return result;
    }

    private static void AppendFileHeader(StringBuilder sb, FileDiff file, bool includeFileLines)
    {
        var newPath = file.FileName;
        var isRenameOrCopy = file.State is PathState.Moved or PathState.Duplicated && !string.IsNullOrEmpty(file.OldPath);
        var oldPath = isRenameOrCopy ? file.OldPath! : newPath;
        var isNew = file.State is PathState.New or PathState.Untracked;
        var isRemoved = file.State is PathState.Removed;

        sb.Append("diff --git ").Append(Quote("a/" + oldPath)).Append(' ').Append(Quote("b/" + newPath)).Append('\n');

        var oldMode = FormatMode(file.OldMode);
        var newMode = FormatMode(file.NewMode);
        if (isNew)
        {
            sb.Append("new file mode ").Append(newMode ?? oldMode ?? DefaultFileMode).Append('\n');
        }
        else if (isRemoved)
        {
            sb.Append("deleted file mode ").Append(oldMode ?? newMode ?? DefaultFileMode).Append('\n');
        }
        else if (oldMode is not null && newMode is not null && oldMode != newMode)
        {
            sb.Append("old mode ").Append(oldMode).Append('\n');
            sb.Append("new mode ").Append(newMode).Append('\n');
        }

        if (isRenameOrCopy)
        {
            var verb = file.State == PathState.Moved ? "rename" : "copy";
            sb.Append(verb).Append(" from ").Append(Quote(oldPath)).Append('\n');
            sb.Append(verb).Append(" to ").Append(Quote(newPath)).Append('\n');
        }

        if (!includeFileLines)
        {
            return;
        }

        if (isNew)
        {
            sb.Append("--- ").Append(DevNull).Append('\n');
        }
        else
        {
            AppendFileLine(sb, "---", "a/", oldPath);
        }

        if (isRemoved)
        {
            sb.Append("+++ ").Append(DevNull).Append('\n');
        }
        else
        {
            AppendFileLine(sb, "+++", "b/", newPath);
        }
    }

    private static (string Old, string New) GetSideNames(FileDiff file)
    {
        var isRenameOrCopy = file.State is PathState.Moved or PathState.Duplicated && !string.IsNullOrEmpty(file.OldPath);
        var oldPath = isRenameOrCopy ? file.OldPath! : file.FileName;
        var oldName = file.State is PathState.New or PathState.Untracked ? DevNull : Quote("a/" + oldPath);
        var newName = file.State is PathState.Removed ? DevNull : Quote("b/" + file.FileName);
        return (oldName, newName);
    }

    /// <summary>
    /// Writes a <c>---</c>/<c>+++</c> line. Like git, a name containing a space is
    /// followed by a tab so the end of the name is unambiguous.
    /// </summary>
    private static void AppendFileLine(StringBuilder sb, string tag, string prefix, string path)
    {
        sb.Append(tag).Append(' ').Append(Quote(prefix + path));
        if (path.Contains(' ', StringComparison.Ordinal))
        {
            sb.Append('\t');
        }

        sb.Append('\n');
    }

    /// <summary>Writes a hunk whose start positions come from <paramref name="hunk"/>.</summary>
    private static void AppendHunk(StringBuilder sb, List<PatchLine> body, IHunk hunk)
    {
        // Original side lengths, counted from the hunk's own lines.
        int origOld = 0, origNew = 0;
        foreach (HunkLine line in hunk.LineItems)
        {
            switch (line.Marker)
            {
                case '\\':
                    break;
                case '+':
                    origNew++;
                    break;
                case '-':
                    origOld++;
                    break;
                default:
                    origOld++;
                    origNew++;
                    break;
            }
        }

        (var oldStart, var newStart, var trailer) = ParseHeader(hunk.RawText);
        oldStart ??= hunk.OldStart;
        newStart ??= hunk.NewStart;
        trailer ??= string.IsNullOrEmpty(hunk.Annotation) ? null : " " + hunk.Annotation;

        // In unified diff, a side with lines names its first line; an empty side names
        // the line it follows. Move the start when a side gains or loses all its lines.
        var oldNonEmptyStart = origOld > 0 ? oldStart.Value : oldStart.Value + 1;
        var newNonEmptyStart = origNew > 0 ? newStart.Value : newStart.Value + 1;
        AppendHunkLines(sb, body, oldNonEmptyStart, newNonEmptyStart, trailer,
            oldStartWhenEmpty: Math.Max(0, oldNonEmptyStart - 1),
            newStartWhenEmpty: Math.Max(0, newNonEmptyStart - 1));
    }

    private static void AppendHunkLines(StringBuilder sb, List<PatchLine> body, int oldStart, int newStart, string? annotation,
        int oldStartWhenEmpty, int newStartWhenEmpty)
    {
        int oldCount = 0, newCount = 0;
        foreach (PatchLine line in body)
        {
            if (line.Marker != '+')
            {
                oldCount++;
            }

            if (line.Marker != '-')
            {
                newCount++;
            }
        }

        sb.Append("@@ -")
            .Append(FormatRange(oldCount > 0 ? Math.Max(1, oldStart) : oldStartWhenEmpty, oldCount))
            .Append(" +")
            .Append(FormatRange(newCount > 0 ? Math.Max(1, newStart) : newStartWhenEmpty, newCount))
            .Append(" @@")
            .Append(annotation)
            .Append('\n');

        foreach (PatchLine line in body)
        {
            sb.Append(line.Marker).Append(line.Text).Append('\n');
            if (line.NoNewline)
            {
                sb.Append('\\').Append(string.IsNullOrEmpty(line.NoNewlineText) ? NoNewlineText : line.NoNewlineText).Append('\n');
            }
        }
    }

    private static string FormatRange(int start, int count)
        => count == 1
            ? start.ToString(CultureInfo.InvariantCulture)
            : start.ToString(CultureInfo.InvariantCulture) + "," + count.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the start positions, and whatever follows the closing <c>@@</c>, from a
    /// hunk header such as <c>@@ -3,0 +4,2 @@ void Main()</c>. The header keeps the
    /// position of an empty side, which the model's zero start does not.
    /// </summary>
    private static (int? OldStart, int? NewStart, string? Trailer) ParseHeader(string? header)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith("@@ -", StringComparison.Ordinal))
        {
            return (null, null, null);
        }

        var pos = 4;
        var oldStart = ReadNumber(header, ref pos);
        SkipCount(header, ref pos);
        if (oldStart is null || pos + 1 >= header.Length || header[pos] != ' ' || header[pos + 1] != '+')
        {
            return (null, null, null);
        }

        pos += 2;
        var newStart = ReadNumber(header, ref pos);
        SkipCount(header, ref pos);
        if (newStart is null || !header.AsSpan(pos).StartsWith(" @@", StringComparison.Ordinal))
        {
            return (null, null, null);
        }

        pos += 3;
        var trailer = pos < header.Length ? header[pos..].TrimEnd('\r', '\n') : null;
        return (oldStart, newStart, string.IsNullOrEmpty(trailer) ? null : trailer);
    }

    private static int? ReadNumber(string text, ref int pos)
    {
        var start = pos;
        while (pos < text.Length && char.IsAsciiDigit(text[pos]))
        {
            pos++;
        }

        return pos > start && int.TryParse(text.AsSpan(start, pos - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static void SkipCount(string text, ref int pos)
    {
        if (pos < text.Length && text[pos] == ',')
        {
            pos++;
            _ = ReadNumber(text, ref pos);
        }
    }

    /// <summary>
    /// Formats a mode for a header line. The model stores modes as integers; a value
    /// of at least 100000 is taken to hold the octal digits as written (100644), a
    /// smaller positive value to be the numeric mode (0o100644 = 33188). Zero means unknown.
    /// </summary>
    private static string? FormatMode(int mode)
    {
        if (mode <= 0)
        {
            return null;
        }

        return mode >= 100000
            ? mode.ToString(CultureInfo.InvariantCulture)
            : Convert.ToString(mode, 8).PadLeft(6, '0');
    }

    /// <summary>
    /// C-quotes a path the way git does with <c>core.quotePath</c> enabled: the whole
    /// name is wrapped in double quotes when it contains a control character, a double
    /// quote, a backslash or any non-ASCII byte, which is written as a three-digit
    /// octal escape of its UTF-8 encoding.
    /// </summary>
    private static string Quote(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (!bytes.Any(NeedsEscape))
        {
            return name;
        }

        var sb = new StringBuilder(bytes.Length + 8);
        sb.Append('"');
        foreach (var b in bytes)
        {
            switch (b)
            {
                case 0x07: sb.Append("\\a"); break;
                case 0x08: sb.Append("\\b"); break;
                case 0x09: sb.Append("\\t"); break;
                case 0x0A: sb.Append("\\n"); break;
                case 0x0B: sb.Append("\\v"); break;
                case 0x0C: sb.Append("\\f"); break;
                case 0x0D: sb.Append("\\r"); break;
                case (byte)'"': sb.Append("\\\""); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                default:
                    if (b is < 0x20 or >= 0x7F)
                    {
                        sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        sb.Append((char)b);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();

        static bool NeedsEscape(byte b) => b is < 0x20 or >= 0x7F or (byte)'"' or (byte)'\\';
    }

    /// <summary>One output line of a hunk body.</summary>
    private readonly record struct PatchLine(char Marker, string Text, bool NoNewline = false, string? NoNewlineText = null);
}
