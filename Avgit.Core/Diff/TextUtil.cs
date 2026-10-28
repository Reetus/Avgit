using System.Text;

namespace Avgit.Core.Diff;

/// <summary>
/// Text helpers for diffs and git paths: cursor-based line reading, tab
/// expansion, bidi-control stripping and git's C-style path quoting.
/// </summary>
public static class TextUtil
{
    /// <summary>
    /// Reads the line that starts at <paramref name="cursor"/> and advances the
    /// cursor past its terminator.
    /// </summary>
    /// <remarks>
    /// Only <c>\n</c> ends a line, as in git. A <c>\r</c> directly before the
    /// <c>\n</c> is reported through <paramref name="ending"/> as <c>"\r\n"</c>
    /// and is not part of the returned text. A lone <c>\r</c> anywhere else is
    /// ordinary content and stays in the line. At the end of the text (no
    /// terminator) <paramref name="ending"/> is empty. When the cursor is already
    /// at or past the end, the result is empty and the cursor does not move.
    /// </remarks>
    public static string ReadNextLine(string text, ref int cursor, out string ending)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (cursor >= text.Length)
        {
            ending = string.Empty;
            return string.Empty;
        }

        var start = Math.Max(cursor, 0);
        var newline = text.IndexOf('\n', start);
        if (newline < 0)
        {
            cursor = text.Length;
            ending = string.Empty;
            return text[start..];
        }

        cursor = newline + 1;
        if (newline > start && text[newline - 1] == '\r')
        {
            ending = "\r\n";
            return text[start..(newline - 1)];
        }

        ending = "\n";
        return text[start..newline];
    }

    /// <summary>
    /// Splits text into lines using <see cref="ReadNextLine"/>'s rules. A final
    /// terminator does not produce a trailing empty element; empty text yields
    /// no lines.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        var cursor = 0;
        while (cursor < text.Length)
        {
            lines.Add(ReadNextLine(text, ref cursor, out _));
        }

        return lines;
    }

    /// <summary>
    /// Expands tabs to spaces using tab stops every <paramref name="tabSize"/>
    /// columns. The column restarts after each <c>\n</c>.
    /// </summary>
    public static string ConvertTabsToSpaces(string text, int tabSize)
    {
        if (string.IsNullOrEmpty(text) || tabSize <= 0 || !text.Contains('\t'))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        var column = 0;
        foreach (var c in text)
        {
            if (c == '\t')
            {
                var spaces = tabSize - (column % tabSize);
                builder.Append(' ', spaces);
                column += spaces;
            }
            else
            {
                builder.Append(c);
                column = c == '\n' ? 0 : column + 1;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Removes Unicode bidirectional control characters (embeddings, overrides,
    /// isolates and directional marks) so diff text cannot visually reorder
    /// itself ("Trojan Source").
    /// </summary>
    public static string StripBidiControls(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var first = IndexOfBidiControl(text);
        if (first < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, first);
        for (var i = first + 1; i < text.Length; i++)
        {
            if (!IsBidiControl(text[i]))
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    /// <summary>True for the characters <see cref="StripBidiControls"/> removes.</summary>
    public static bool IsBidiControl(char c)
        => c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') or '‎' or '‏' or '؜';

    private static int IndexOfBidiControl(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (IsBidiControl(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Decodes a path that git wrote in C-style quotes (<c>"..."</c> with
    /// backslash escapes and <c>\ooo</c> octal bytes forming UTF-8). Text that is
    /// not wrapped in double quotes is returned unchanged.
    /// </summary>
    public static string UnquoteGitPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2 || path[0] != '"' || path[^1] != '"')
        {
            return path;
        }

        return DecodeQuoted(path, 1, path.Length - 1);
    }

    /// <summary>
    /// Decodes the quoted string that starts at <paramref name="start"/> (which
    /// must be a <c>"</c>) and reports the index just past its closing quote
    /// through <paramref name="end"/>. Returns null when there is no closing quote.
    /// </summary>
    internal static string? ReadQuoted(string text, int start, out int end)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                end = i + 1;
                return DecodeQuoted(text, start + 1, i);
            }
        }

        end = text.Length;
        return null;
    }

    private static string DecodeQuoted(string text, int from, int to)
    {
        var bytes = new List<byte>(to - from);
        for (var i = from; i < to; i++)
        {
            var c = text[i];
            if (c != '\\' || i + 1 >= to)
            {
                AppendUtf8(bytes, text, ref i);
                continue;
            }

            i++;
            switch (text[i])
            {
                case 'a': bytes.Add(0x07); break;
                case 'b': bytes.Add(0x08); break;
                case 't': bytes.Add(0x09); break;
                case 'n': bytes.Add(0x0A); break;
                case 'v': bytes.Add(0x0B); break;
                case 'f': bytes.Add(0x0C); break;
                case 'r': bytes.Add(0x0D); break;
                case >= '0' and <= '7':
                    var value = 0;
                    var digits = 0;
                    while (digits < 3 && i < to && text[i] is >= '0' and <= '7')
                    {
                        value = (value * 8) + (text[i] - '0');
                        i++;
                        digits++;
                    }

                    i--;
                    bytes.Add((byte)(value & 0xFF));
                    break;
                default:
                    // \" and \\ (and any unknown escape) stand for the character itself.
                    AppendUtf8(bytes, text, ref i);
                    break;
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void AppendUtf8(List<byte> bytes, string text, ref int i)
    {
        var c = text[i];
        if (c < 0x80)
        {
            bytes.Add((byte)c);
            return;
        }

        var length = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
        bytes.AddRange(Encoding.UTF8.GetBytes(text, i, length));
        i += length - 1;
    }

    /// <summary>
    /// Quotes a path the way git does with <c>core.quotePath=true</c>: paths with
    /// control characters, <c>"</c>, <c>\</c> or non-ASCII characters are wrapped
    /// in double quotes, with named escapes for common controls and <c>\ooo</c>
    /// octal for every other byte that needs it (non-ASCII is escaped per UTF-8
    /// byte). Other paths are returned unchanged.
    /// </summary>
    public static string QuoteGitPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !NeedsQuoting(path))
        {
            return path;
        }

        var builder = new StringBuilder(path.Length + 8);
        builder.Append('"');
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            switch (b)
            {
                case 0x07: builder.Append("\\a"); break;
                case 0x08: builder.Append("\\b"); break;
                case 0x09: builder.Append("\\t"); break;
                case 0x0A: builder.Append("\\n"); break;
                case 0x0B: builder.Append("\\v"); break;
                case 0x0C: builder.Append("\\f"); break;
                case 0x0D: builder.Append("\\r"); break;
                case (byte)'"': builder.Append("\\\""); break;
                case (byte)'\\': builder.Append("\\\\"); break;
                case < 0x20 or >= 0x7F:
                    builder.Append('\\')
                        .Append((char)('0' + ((b >> 6) & 7)))
                        .Append((char)('0' + ((b >> 3) & 7)))
                        .Append((char)('0' + (b & 7)));
                    break;
                default:
                    builder.Append((char)b);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static bool NeedsQuoting(string path)
    {
        foreach (var c in path)
        {
            if (c is < ' ' or >= '\u007F' or '"' or '\\')
            {
                return true;
            }
        }

        return false;
    }
}
