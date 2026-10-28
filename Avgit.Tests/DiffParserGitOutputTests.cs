using System.Text;
using Avgit.Core.Diff;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>A throwaway repository driven by the git CLI only, for parser tests on real git output.</summary>
internal sealed class DiffGitScenario : IDisposable
{
    private DiffGitScenario(string path) => Path = path;

    public string Path { get; }

    public static async Task<DiffGitScenario> CreateAsync()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "avgit-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var scenario = new DiffGitScenario(path);
        await scenario.GitAsync("init", "-q");
        await scenario.GitAsync("config", "user.name", "Test User");
        await scenario.GitAsync("config", "user.email", "test@example.com");
        await scenario.GitAsync("config", "commit.gpgsign", "false");
        await scenario.GitAsync("config", "core.autocrlf", "false");
        await scenario.GitAsync("config", "core.quotePath", "true");
        return scenario;
    }

    public async Task<string> GitAsync(params string[] args)
    {
        ProcessResult result = await ProcessRunner.RunAsync("git", Path, args);
        Assert.True(result.Success, $"git {string.Join(' ', args)} failed: {result.Combined}");
        return result.Output;
    }

    public void Write(string relativePath, string content) => WriteBytes(relativePath, new UTF8Encoding(false).GetBytes(content));

    public void WriteBytes(string relativePath, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    public async Task CommitAllAsync(string message = "commit")
    {
        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", message);
    }

    /// <summary>Staged changes, requested the way the app asks for diffs.</summary>
    public Task<string> DiffCachedAsync(params string[] extra)
        => GitAsync(["diff", "--cached", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", "-M", "-C", .. extra]);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }

            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temp folder.
        }
    }
}

public class DiffParserGitOutputTests
{
    private static readonly DiffSetup Env = new()
    {
        Subject = DiffSubject.PendingFiles,
        Scope = DiffScope.IndexVsHead
    };

    private static List<FileDiff> Parse(string diff, int maxFiles = 50, int maxLines = 100_000, int maxLinesPerHunk = 10_000)
        => UnifiedDiffParser.Parse(diff, new DiffParseOptions
        {
            Setup = Env,
            MaxFiles = maxFiles,
            MaxLines = maxLines,
            MaxLinesPerHunk = maxLinesPerHunk
        });

    [Fact]
    public async Task New_file_has_zero_old_range_and_new_mode()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        repo.Write("base.txt", "base\n");
        await repo.CommitAllAsync();
        repo.Write("dir/new.txt", "one\ntwo\n");
        await repo.GitAsync("add", "-A");

        FileDiff file = Assert.Single(Parse(await repo.DiffCachedAsync()));
        Assert.Equal("dir/new.txt", file.FileName);
        Assert.Equal(PathState.New, file.State);
        Assert.Null(file.OldPath);
        Assert.Equal(0, file.OldMode);
        Assert.Equal(100644, file.NewMode);
        Assert.Equal(2, file.AddedLines);
        Assert.Equal(Env, file.Setup);
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.Equal("@@ -0,0 +1,2 @@", hunk.RawText);
        Assert.Equal((0, 0, 1, 2), (hunk.OldStart, hunk.OldEnd, hunk.NewStart, hunk.NewEnd));
        Assert.Equal(1, hunk.Number);
        Assert.Same(file, hunk.Owner);
        Assert.All(hunk.LineItems, l => Assert.Equal('+', l.Marker));
        Assert.Equal(["1", "2"], hunk.LineItems.Select(l => l.NewLineNo));
        Assert.All(hunk.LineItems, l => Assert.Equal(string.Empty, l.OldLineNo));
        Assert.Contains("New file", file.Caption);
        Assert.Contains("Whole file", hunk.Caption);
        Assert.Equal(string.Empty, file.EmptyMessage);
    }

    [Fact]
    public async Task Deleted_file_is_named_by_its_old_path()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        repo.Write("gone.txt", "a\nb\nc\n");
        await repo.CommitAllAsync();
        await repo.GitAsync("rm", "-q", "gone.txt");

        FileDiff file = Assert.Single(Parse(await repo.DiffCachedAsync()));
        Assert.Equal("gone.txt", file.FileName);
        Assert.Equal(PathState.Removed, file.State);
        Assert.Equal(100644, file.OldMode);
        Assert.Equal(0, file.NewMode);
        Assert.Equal(3, file.RemovedLines);
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.Equal((1, 3, 0, 0), (hunk.OldStart, hunk.OldEnd, hunk.NewStart, hunk.NewEnd));
        Assert.All(hunk.LineItems, l => Assert.Equal('-', l.Marker));
        Assert.Contains("3 deleted", hunk.Caption);
    }

    [Fact]
    public async Task Mode_only_change_takes_the_path_from_the_diff_git_line()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        repo.Write("x b/y.txt", "content\n");
        await repo.CommitAllAsync();
        await repo.GitAsync("update-index", "--chmod=+x", "x b/y.txt");

        var raw = await repo.DiffCachedAsync();
        Assert.Contains("old mode 100644", raw);
        FileDiff file = Assert.Single(Parse(raw));
        Assert.Equal("x b/y.txt", file.FileName);
        Assert.Equal(PathState.Edited, file.State);
        Assert.Equal(100644, file.OldMode);
        Assert.Equal(100755, file.NewMode);
        Assert.Empty(file.Hunks);
        Assert.Contains("100755", file.Caption);
        Assert.Contains("mode", file.EmptyMessage);
    }

    [Fact]
    public async Task Rename_with_edit_keeps_old_path_and_hunks()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        var body = string.Concat(Enumerable.Range(1, 20).Select(i => $"line {i}\n"));
        repo.Write("old name.txt", body);
        await repo.CommitAllAsync();
        await repo.GitAsync("mv", "old name.txt", "new name.txt");
        repo.Write("new name.txt", body.Replace("line 10\n", "line ten\n"));
        await repo.GitAsync("add", "-A");

        var raw = await repo.DiffCachedAsync();
        Assert.Contains("rename from old name.txt", raw);
        FileDiff file = Assert.Single(Parse(raw));
        Assert.Equal(PathState.Moved, file.State);
        Assert.Equal("old name.txt", file.OldPath);
        Assert.Equal("new name.txt", file.FileName);
        Assert.Equal(1, file.AddedLines);
        Assert.Equal(1, file.RemovedLines);
        Assert.Single(file.Hunks);
        Assert.Contains("old name.txt", file.Caption);
    }

    [Fact]
    public async Task Copy_is_reported_as_duplicated()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        var body = string.Concat(Enumerable.Range(1, 20).Select(i => $"copy line {i}\n"));
        repo.Write("source.txt", body);
        await repo.CommitAllAsync();
        repo.Write("copy.txt", body);
        await repo.GitAsync("add", "-A");

        var raw = await repo.DiffCachedAsync("--find-copies-harder");
        Assert.Contains("copy from source.txt", raw);
        FileDiff file = Assert.Single(Parse(raw));
        Assert.Equal(PathState.Duplicated, file.State);
        Assert.Equal("source.txt", file.OldPath);
        Assert.Equal("copy.txt", file.FileName);
        Assert.Empty(file.Hunks);
        Assert.Contains("Copied", file.EmptyMessage);
    }

    [Fact]
    public async Task Binary_files_are_flagged_without_hunks()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        repo.WriteBytes("image.bin", [1, 2, 0, 3, 4]);
        await repo.CommitAllAsync();
        repo.WriteBytes("image.bin", [1, 2, 0, 3, 5, 6]);
        repo.WriteBytes("added.bin", [0, 0, 0]);
        await repo.GitAsync("add", "-A");

        List<FileDiff> files = Parse(await repo.DiffCachedAsync());
        Assert.Equal(2, files.Count);
        FileDiff added = files.Single(f => f.FileName == "added.bin");
        FileDiff edited = files.Single(f => f.FileName == "image.bin");
        Assert.Equal(PathState.New, added.State);
        Assert.Equal(PathState.Edited, edited.State);
        Assert.All(files, f => Assert.True(f.Binary));
        Assert.All(files, f => Assert.Empty(f.Hunks));
        Assert.Contains("binary", edited.Caption, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Binary", edited.EmptyMessage);

        List<FileDiff> binaryPatch = Parse(await repo.DiffCachedAsync("--binary"));
        Assert.Equal(2, binaryPatch.Count);
        Assert.All(binaryPatch, f => Assert.True(f.Binary));
        Assert.All(binaryPatch, f => Assert.Empty(f.Hunks));
    }

    [Fact]
    public async Task Crlf_lines_keep_the_carriage_return_in_raw_text_only()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        repo.Write("dos.txt", "one\r\ntwo\r\nthree\r\n");
        await repo.CommitAllAsync();
        repo.Write("dos.txt", "one\r\nTWO\r\nthree\r\n");
        await repo.GitAsync("add", "-A");

        FileDiff file = Assert.Single(Parse(await repo.DiffCachedAsync()));
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.All(hunk.LineItems, l => Assert.EndsWith("\r", l.RawText));
        Assert.All(hunk.LineItems, l => Assert.DoesNotContain("\r", l.Text));
        HunkLine added = hunk.LineItems.Single(l => l.Marker == '+');
        Assert.Equal("TWO\r", added.RawText);
        Assert.Equal("TWO", added.Text);
    }

    [Fact]
    public async Task No_newline_marker_follows_its_line_without_numbers()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        repo.Write("tail.txt", "a\nb");
        await repo.CommitAllAsync();
        repo.Write("tail.txt", "a\nc");
        await repo.GitAsync("add", "-A");

        FileDiff file = Assert.Single(Parse(await repo.DiffCachedAsync()));
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.Equal([' ', '-', '\\', '+', '\\'], hunk.LineItems.Select(l => l.Marker));
        HunkLine marker = hunk.LineItems[2];
        Assert.Equal(" No newline at end of file", marker.RawText);
        Assert.Equal(string.Empty, marker.OldLineNo);
        Assert.Equal(string.Empty, marker.NewLineNo);
        Assert.Equal("2", hunk.LineItems[3].NewLineNo);
        Assert.Equal(1, file.AddedLines);
        Assert.Equal(1, file.RemovedLines);
    }

    [Fact]
    public async Task Gitlink_change_is_a_submodule_commit_change()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        repo.Write("readme.txt", "readme\n");
        await repo.CommitAllAsync();
        var first = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
        await repo.GitAsync("update-index", "--add", "--cacheinfo", $"160000,{first},sub");
        await repo.GitAsync("commit", "-q", "-m", "add gitlink");

        var show = await repo.GitAsync("show", "--format=", "--no-color", "--src-prefix=a/", "--dst-prefix=b/", "HEAD");
        FileDiff added = Assert.Single(Parse(show));
        Assert.Equal("sub", added.FileName);
        Assert.Equal(PathState.New, added.State);
        Assert.Equal(160000, added.NewMode);

        repo.Write("readme.txt", "readme 2\n");
        await repo.GitAsync("add", "readme.txt");
        await repo.GitAsync("commit", "-q", "-m", "second");
        var second = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
        await repo.GitAsync("update-index", "--cacheinfo", $"160000,{second},sub");

        var raw = await repo.DiffCachedAsync();
        Assert.Contains("Subproject commit", raw);
        FileDiff file = Assert.Single(Parse(raw));
        Assert.Equal("sub", file.FileName);
        Assert.Equal(PathState.SubmoduleCommitChanged, file.State);
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.Equal("Subproject commit " + second, hunk.LineItems.Single(l => l.Marker == '+').Text);
        Assert.Contains("Submodule", file.Caption);
    }

    [Fact]
    public async Task Multiple_hunks_carry_function_annotations()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        var lines = new List<string> { "int first(void)", "{" };
        lines.AddRange(Enumerable.Range(1, 15).Select(i => $"    statement_{i}();"));
        lines.Add("}");
        lines.Add("int second(void)");
        lines.Add("{");
        lines.AddRange(Enumerable.Range(1, 15).Select(i => $"    other_{i}();"));
        lines.Add("}");
        var original = string.Join('\n', lines) + "\n";
        repo.Write("code.c", original);
        await repo.CommitAllAsync();
        repo.Write("code.c", original.Replace("statement_10();", "changed_10();").Replace("other_12();", "changed_12();"));
        await repo.GitAsync("add", "-A");

        FileDiff file = Assert.Single(Parse(await repo.DiffCachedAsync()));
        Assert.Equal(2, file.Hunks.Count);
        Assert.Equal([1, 2], file.Hunks.Select(h => h.Number));
        Assert.Equal("int first(void)", file.Hunks[0].Annotation);
        Assert.Equal("int second(void)", file.Hunks[1].Annotation);
        Assert.EndsWith("@@ int second(void)", file.Hunks[1].RawText);
        Assert.StartsWith("int second(void)", file.Hunks[1].Caption);
        Assert.True(file.Hunks[0].NewEnd < file.Hunks[1].NewStart);

        HunkLine changed = file.Hunks[1].LineItems.Single(l => l.Marker == '+');
        Assert.Equal((lines.IndexOf("    other_12();") + 1).ToString(), changed.NewLineNo);
    }

    [Fact]
    public async Task Count_omitted_header_means_one_line()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        repo.Write("one.txt", "a\n");
        await repo.CommitAllAsync();
        repo.Write("one.txt", "b\n");
        await repo.GitAsync("add", "-A");

        FileDiff file = Assert.Single(Parse(await repo.DiffCachedAsync()));
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.Equal("@@ -1 +1 @@", hunk.RawText);
        Assert.Equal((1, 1, 1, 1), (hunk.OldStart, hunk.OldEnd, hunk.NewStart, hunk.NewEnd));
        Assert.Null(hunk.Annotation);
        Assert.Equal("Line 1", hunk.Caption);
    }

    [Fact]
    public async Task Quoted_non_ascii_and_spaced_paths_are_decoded()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        repo.Write("café.txt", "x\n");
        repo.Write("with space/a b.txt", "x\n");
        await repo.CommitAllAsync();
        repo.Write("café.txt", "y\n");
        repo.Write("with space/a b.txt", "y\n");
        await repo.GitAsync("add", "-A");

        var raw = await repo.DiffCachedAsync();
        Assert.Contains("\"a/caf\\303\\251.txt\"", raw);
        List<FileDiff> files = Parse(raw);
        Assert.Equal(["café.txt", "with space/a b.txt"], files.Select(f => f.FileName).Order(StringComparer.Ordinal));
        Assert.All(files, f => Assert.Equal(PathState.Edited, f.State));
        Assert.All(files, f => Assert.Single(f.Hunks));
    }

    [Fact]
    public async Task Limits_apply_to_real_output()
    {
        using DiffGitScenario repo = await DiffGitScenario.CreateAsync();
        for (var i = 0; i < 4; i++)
        {
            repo.Write($"f{i}.txt", string.Concat(Enumerable.Range(0, 10).Select(n => $"{i}-{n}\n")));
        }

        await repo.GitAsync("add", "-A");
        var raw = await repo.DiffCachedAsync();

        Assert.Equal(4, Parse(raw).Count);
        Assert.Equal(["f0.txt", "f1.txt"], Parse(raw, maxFiles: 2).Select(f => f.FileName));

        List<FileDiff> perHunk = Parse(raw, maxLinesPerHunk: 4);
        Assert.Equal(4, perHunk.Count);
        Assert.All(perHunk, f => Assert.True(f.Truncated));
        Assert.All(perHunk, f => Assert.Equal(4, f.Hunks[0].LineItems.Count));
        Assert.All(perHunk, f => Assert.True(f.Hunks[0].Truncated));
        Assert.Contains("4+", perHunk[0].Caption);

        List<FileDiff> global = Parse(raw, maxLines: 15);
        Assert.Equal(4, global.Count);
        Assert.False(global[0].Truncated);
        Assert.Equal(10, global[0].Hunks[0].LineItems.Count);
        Assert.True(global[1].Truncated);
        Assert.True(global[1].Hunks[0].Truncated);
        Assert.Equal(5, global[1].Hunks[0].LineItems.Count);
        Assert.True(global[2].Truncated);
        Assert.Empty(global[2].Hunks);
        Assert.Equal("f2.txt", global[2].FileName);
        Assert.Contains("too large", global[2].EmptyMessage);
    }
}

public class DiffParserEdgeCaseTests
{
    private static readonly DiffParseOptions Options = new() { Setup = new DiffSetup { Subject = DiffSubject.SingleCommit } };

    [Fact]
    public void Content_lines_that_look_like_headers_stay_in_the_hunk()
    {
        const string diff = "diff --git a/x.txt b/x.txt\n--- a/x.txt\n+++ b/x.txt\n@@ -1,3 +1,3 @@\n--- a/old\n+++ b/new\n diff --git a/q b/q\n-@@ -1 +1 @@\n+@@ -2 +2 @@\n";
        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, Options));
        IHunk hunk = Assert.Single(file.Hunks);
        Assert.Equal(5, hunk.LineItems.Count);
        Assert.Equal("-- a/old", hunk.LineItems[0].RawText);
        Assert.Equal("++ b/new", hunk.LineItems[1].RawText);
        Assert.False(hunk.Truncated);
    }

    [Fact]
    public void Commit_preamble_from_git_log_is_ignored()
    {
        const string diff = "commit 0123456789abcdef\nAuthor: A <a@b>\n\n    --- not a diff\n\ndiff --git a/x.txt b/x.txt\n--- a/x.txt\n+++ b/x.txt\n@@ -1 +1 @@\n-a\n+b\n\ncommit fedcba\n\n    message\n\ndiff --git a/y.txt b/y.txt\n--- a/y.txt\n+++ b/y.txt\n@@ -1 +1 @@\n-c\n+d\n";
        List<FileDiff> files = UnifiedDiffParser.Parse(diff, Options);
        Assert.Equal(["x.txt", "y.txt"], files.Select(f => f.FileName));
        Assert.All(files, f => Assert.Equal(2, f.Hunks[0].LineItems.Count));
        Assert.All(files, f => Assert.False(f.Truncated));
    }

    [Fact]
    public void Hunk_cut_short_is_marked_truncated()
    {
        const string diff = "diff --git a/x.txt b/x.txt\n--- a/x.txt\n+++ b/x.txt\n@@ -1,4 +1,4 @@\n a\n-b\n";
        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, Options));
        Assert.True(file.Truncated);
        Assert.True(file.Hunks[0].Truncated);
    }

    [Fact]
    public void Display_text_strips_bidi_controls_and_expands_tabs()
    {
        const string diff = "diff --git a/x.txt b/x.txt\n--- a/x.txt\n+++ b/x.txt\n@@ -1 +1 @@\n-a\tb\n+x‮y⁦z\r\n";
        IHunk hunk = UnifiedDiffParser.Parse(diff, Options)[0].Hunks[0];
        Assert.Equal("a\tb", hunk.LineItems[0].RawText);
        Assert.Equal("a   b", hunk.LineItems[0].Text);
        Assert.Equal("x‮y⁦z\r", hunk.LineItems[1].RawText);
        Assert.Equal("xyz", hunk.LineItems[1].Text);

        IHunk raw = UnifiedDiffParser.Parse(diff, new DiffParseOptions { ConvertTabsToSpaces = false })[0].Hunks[0];
        Assert.Equal("a\tb", raw.LineItems[0].Text);
    }

    [Fact]
    public void Plain_unified_diff_without_git_header_is_parsed()
    {
        const string diff = "--- a/one.txt\n+++ b/one.txt\n@@ -1 +1 @@\n-a\n+b\n--- a/two.txt\n+++ b/two.txt\n@@ -1 +1 @@\n-c\n+d\n";
        List<FileDiff> files = UnifiedDiffParser.Parse(diff, Options);
        Assert.Equal(["one.txt", "two.txt"], files.Select(f => f.FileName));
    }

    [Fact]
    public void Empty_input_gives_no_files()
        => Assert.Empty(UnifiedDiffParser.Parse(string.Empty, Options));

    [Fact]
    public void Caption_is_cached_and_invalidated()
    {
        var file = new FileDiff(default) { FileName = "a.txt", State = PathState.Edited, AddedLines = 1 };
        var first = file.Caption;
        Assert.Same(first, file.Caption);
        file.Suppressed = true;
        Assert.NotEqual(first, file.Caption);
        Assert.Contains("hidden", file.EmptyMessage);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Parses_a_large_diff_quickly()
    {
        const int files = 200;
        const int pairsPerFile = 334; // 3 lines per pair: ~200k lines in total.
        var builder = new StringBuilder();
        for (var f = 0; f < files; f++)
        {
            builder.Append($"diff --git a/src/file{f}.cs b/src/file{f}.cs\nindex 1111111..2222222 100644\n--- a/src/file{f}.cs\n+++ b/src/file{f}.cs\n");
            builder.Append($"@@ -1,{pairsPerFile * 2} +1,{pairsPerFile * 2} @@ class C{f}\n");
            for (var i = 0; i < pairsPerFile; i++)
            {
                builder.Append("     context line\twith a tab ").Append(i).Append('\n');
                builder.Append("-    removed line ").Append(i).Append('\n');
                builder.Append("+    added line ").Append(i).Append('\n');
            }
        }

        var text = builder.ToString();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<FileDiff> parsed = UnifiedDiffParser.Parse(text, new DiffParseOptions
        {
            MaxFiles = int.MaxValue,
            MaxLines = int.MaxValue,
            MaxLinesPerHunk = int.MaxValue
        });
        stopwatch.Stop();

        Assert.Equal(files, parsed.Count);
        Assert.Equal(files * pairsPerFile * 3, parsed.Sum(f => f.Hunks.Sum(h => h.LineItems.Count)));
        Assert.All(parsed, f => Assert.False(f.Truncated));
        TestContext.Current.SendDiagnosticMessage($"Parsed {files * pairsPerFile * 3:N0} lines ({text.Length:N0} chars) in {stopwatch.ElapsedMilliseconds} ms");
    }
}
