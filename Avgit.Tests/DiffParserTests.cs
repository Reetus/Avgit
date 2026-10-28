using Avgit.Core.Diff;
using Avgit.Core.Model;

namespace Avgit.Tests;

public class DiffParserTests
{
    private static readonly DiffSetup Env = new()
    {
        Subject = DiffSubject.PendingFiles,
        Scope = DiffScope.WorkTreeVsIndex
    };

    [Fact]
    public void Parses_modified_file_with_hunk_line_numbers()
    {
        const string diff = """
diff --git a/readme.txt b/readme.txt
index 4cb29ea..6addb9b 100644
--- a/readme.txt
+++ b/readme.txt
@@ -1,3 +1,4 @@
 one
-two
+TWO
 three
+four
""";

        List<FileDiff> files = UnifiedDiffParser.Parse(diff, new DiffParseOptions { Setup = Env });
        FileDiff file = Assert.Single(files);
        Assert.Equal("readme.txt", file.FileName);
        Assert.Equal(PathState.Edited, file.State);
        Assert.Equal(2, file.AddedLines);
        Assert.Equal(1, file.RemovedLines);

        IHunk hunk = Assert.Single(file.Hunks);
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(3, hunk.OldEnd);
        Assert.Equal(1, hunk.NewStart);
        Assert.Equal(4, hunk.NewEnd);
        Assert.Equal(5, hunk.LineItems.Count);
        Assert.Equal("-", hunk.LineItems[1].Marker.ToString());
        Assert.Equal("two", hunk.LineItems[1].Text);
        Assert.Equal("TWO", hunk.LineItems[2].Text);
        Assert.Equal("4", hunk.LineItems[4].NewLineNo);
    }

    [Fact]
    public void Parses_rename_and_binary_markers()
    {
        const string diff = """
diff --git a/old.txt b/new.txt
similarity index 92%
rename from old.txt
rename to new.txt
--- a/old.txt
+++ b/new.txt
@@ -1 +1 @@
-old
+new
diff --git a/logo.png b/logo.png
new file mode 100644
Binary files /dev/null and b/logo.png differ
""";

        List<FileDiff> files = UnifiedDiffParser.Parse(diff, new DiffParseOptions { Setup = Env });
        Assert.Equal(2, files.Count);
        Assert.Equal(PathState.Moved, files[0].State);
        Assert.Equal("old.txt", files[0].OldPath);
        Assert.Equal("new.txt", files[0].FileName);
        Assert.Equal(PathState.New, files[1].State);
        Assert.True(files[1].Binary);
    }

    [Fact]
    public void Builds_stage_patch_for_hunk()
    {
        const string diff = """
diff --git a/readme.txt b/readme.txt
--- a/readme.txt
+++ b/readme.txt
@@ -1,3 +1,4 @@
 one
-two
+TWO
 three
+four
""";
        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, new DiffParseOptions { Setup = Env }));
        IHunk hunk = file.Hunks[0];
        var patch = PatchBuilder.CreatePatchFromHunk(hunk, 0, hunk.LineItems.Count, isStaging: true);

        Assert.NotNull(patch);
        Assert.Contains("diff --git a/readme.txt b/readme.txt", patch);
        Assert.Contains("@@ -1,3 +1,4 @@", patch);
        Assert.Contains("+TWO", patch);
        Assert.Contains("-two", patch);
    }

    [Fact]
    public void Honors_max_files_and_lines()
    {
        var diff = string.Concat(Enumerable.Range(0, 5).Select(i => $"""
diff --git a/f{i}.txt b/f{i}.txt
--- a/f{i}.txt
+++ b/f{i}.txt
@@ -1 +1 @@
-old
+new
""" + "\n"));
        List<FileDiff> files = UnifiedDiffParser.Parse(diff, new DiffParseOptions { Setup = Env, MaxFiles = 2 });
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void Blank_context_lines_advance_line_numbers()
    {
        const string diff = "diff --git a/x.txt b/x.txt\n--- a/x.txt\n+++ b/x.txt\n@@ -1,4 +1,4 @@\n alpha\n \n-beta\n+BETA\n delta\n";
        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, new DiffParseOptions { Setup = Env }));
        IHunk hunk = Assert.Single(file.Hunks);

        Assert.Equal("1", hunk.LineItems[0].OldLineNo);
        Assert.Equal("2", hunk.LineItems[1].OldLineNo);
        Assert.Equal("2", hunk.LineItems[1].NewLineNo);
        Assert.Equal("3", hunk.LineItems[2].OldLineNo);
        Assert.Equal(string.Empty, hunk.LineItems[2].NewLineNo);
        Assert.Equal("3", hunk.LineItems[3].NewLineNo);
        Assert.Equal("4", hunk.LineItems[4].OldLineNo);
        Assert.Equal("4", hunk.LineItems[4].NewLineNo);
    }

    [Fact]
    public void Per_hunk_limit_does_not_drop_later_files()
    {
        const string diff = """
diff --git a/one.txt b/one.txt
--- a/one.txt
+++ b/one.txt
@@ -1,5 +1,5 @@
 a
 b
 c
 d
 e
diff --git a/two.txt b/two.txt
--- a/two.txt
+++ b/two.txt
@@ -1 +1 @@
-old
+new
""";

        List<FileDiff> files = UnifiedDiffParser.Parse(diff, new DiffParseOptions
        {
            Setup = Env,
            MaxLinesPerHunk = 3
        });

        Assert.Equal(2, files.Count);
        Assert.True(files[0].Truncated);
        Assert.True(files[0].Hunks[0].Truncated);
        Assert.False(files[1].Truncated);
        Assert.False(files[1].Hunks[0].Truncated);
    }

    [Fact]
    public void Global_line_limit_marks_file_truncated()
    {
        const string diff = "diff --git a/x.txt b/x.txt\n--- a/x.txt\n+++ b/x.txt\n@@ -1,5 +1,5 @@\n a\n b\n c\n d\n e\n";
        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, new DiffParseOptions
        {
            Setup = Env,
            MaxLines = 3
        }));

        Assert.True(file.Truncated);
    }

    [Fact]
    public void Decodes_c_quoted_paths()
    {
        const string diff = "diff --git \"a/ta\\tb.txt\" \"b/ta\\tb.txt\"\n--- \"a/ta\\tb.txt\"\n+++ \"b/ta\\tb.txt\"\n@@ -1 +1 @@\n-old\n+new\n";
        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, new DiffParseOptions { Setup = Env }));
        Assert.Equal("ta\tb.txt", file.FileName);
    }

    [Fact]
    public void Paths_containing_b_prefix_are_parsed_from_file_markers()
    {
        const string diff = "diff --git a/x b/y.txt b/x b/y.txt\n--- a/x b/y.txt\n+++ b/x b/y.txt\n@@ -1 +1 @@\n-old\n+new\n";
        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, new DiffParseOptions { Setup = Env }));
        Assert.Equal("x b/y.txt", file.FileName);
        Assert.Equal(PathState.Edited, file.State);
        Assert.Null(file.OldPath);
    }

    [Fact]
    public void Pure_rename_uses_from_and_to_markers()
    {
        const string diff = "diff --git a/x b/old.txt b/x b/new.txt\nsimilarity index 100%\nrename from x b/old.txt\nrename to x b/new.txt\n";
        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, new DiffParseOptions { Setup = Env }));
        Assert.Equal("x b/new.txt", file.FileName);
        Assert.Equal("x b/old.txt", file.OldPath);
        Assert.Equal(PathState.Moved, file.State);
    }

    [Fact]
    public void File_patch_uses_dev_null_and_zero_ranges()
    {
        const string diff = """
diff --git a/new.txt b/new.txt
new file mode 100644
--- /dev/null
+++ b/new.txt
@@ -0,0 +1,2 @@
+one
+two
""";
        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, new DiffParseOptions { Setup = Env }));
        var patch = PatchBuilder.CreatePatchFromFile(file);
        Assert.Contains("new file mode 100644", patch);
        Assert.Contains("--- /dev/null", patch);
        Assert.Contains("@@ -0,0 +1,2 @@", patch);
        Assert.Contains("+++ b/new.txt", patch);
    }

    [Fact]
    public void Hunk_patch_keeps_rename_paths()
    {
        const string diff = """
diff --git a/old.txt b/new.txt
similarity index 90%
rename from old.txt
rename to new.txt
--- a/old.txt
+++ b/new.txt
@@ -1 +1 @@
-old
+new
""";
        FileDiff file = Assert.Single(UnifiedDiffParser.Parse(diff, new DiffParseOptions { Setup = Env }));
        var patch = PatchBuilder.CreatePatchFromHunk(file.Hunks[0], 0, file.Hunks[0].LineItems.Count, isStaging: true);
        Assert.NotNull(patch);
        Assert.Contains("diff --git a/old.txt b/new.txt", patch);
        Assert.Contains("rename from old.txt", patch);
        Assert.Contains("rename to new.txt", patch);
        Assert.Contains("--- a/old.txt", patch);
        Assert.Contains("+++ b/new.txt", patch);
    }
}

public class GitPathQuotingTests
{
    [Theory]
    [InlineData("""
                "caf\303\251.txt"
                """, "café.txt")]
    [InlineData("""
                "\346\227\245\346\234\254/a\tb.txt"
                """, "日本/a\tb.txt")]
    [InlineData("""
                "quote\"d\\path"
                """, """quote"d\path""")]
    [InlineData("plain.txt", "plain.txt")]
    public void Unquotes_octal_escapes_as_utf8(string quoted, string expected)
        => Assert.Equal(expected, TextUtil.UnquoteGitPath(quoted));
}
