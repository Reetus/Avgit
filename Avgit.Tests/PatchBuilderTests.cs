using System.Text;
using Avgit.Core.Diff;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>
/// Patch builder tests. Models are built by hand (independent of the diff parser)
/// and every patch that should apply is proven against the real git CLI.
/// </summary>
public class PatchBuilderTests
{
    private const string NoEol = "\\ No newline at end of file";

    private static readonly DiffSetup PendingSetup = new()
    {
        Subject = DiffSubject.PendingFiles,
        Scope = DiffScope.WorkTreeVsIndex
    };

    // ---- line staging -------------------------------------------------------

    [Fact]
    public async Task Stages_one_added_line_out_of_several()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "a\nb\nc\n");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,3 +1,6 @@", " a", "+X", " b", "+Y", " c", "+Z");
        var patch = Build(hunk, true, 3);

        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal("a\nb\nY\nc\n", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public async Task Stages_one_removed_line_out_of_several()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "a\nb\nc\nd\n");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,4 +1,2 @@", " a", "-b", "-c", " d");
        var patch = Build(hunk, true, 2);

        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal("a\nb\nd\n", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public async Task Stages_a_mix_of_selected_and_unselected_lines()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "1\n2\n3\n4\n");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,4 +1,5 @@",
            " 1", "-2", "+TWO", " 3", "-4", "+FOUR", "+5");
        // -2, +FOUR and +5 are selected; +TWO and -4 are not.
        var patch = Build(hunk, true, 1, 5, 6);

        Assert.Contains("@@ -1,4 +1,5 @@\n 1\n-2\n 3\n 4\n+FOUR\n+5\n", patch);
        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal("1\n3\n4\nFOUR\n5\n", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public async Task Stages_lines_from_the_middle_of_a_hunk()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        var original = string.Concat(Enumerable.Range(1, 12).Select(i => $"l{i}\n"));
        await repo.CommitAsync("f.txt", original);

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -2,9 +2,9 @@ section",
            " l2", "-l3", "+L3", " l4", "-l5", "+L5", " l6", "-l7", "+L7", " l8", " l9", " l10");
        var patch = Build(hunk, true, 4, 5);

        Assert.Contains("@@ -2,9 +2,9 @@ section\n", patch);
        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal(original.Replace("l5\n", "L5\n"), await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public async Task Non_contiguous_selection_becomes_one_patch()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "l1\nl2\nl3\nl4\nl5\n");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,5 +1,7 @@",
            "+top", " l1", "-l2", " l3", "+mid", " l4", "-l5", "+L5");
        // +top, -l2 and +L5 (three separate runs).
        var patch = Build(hunk, true, 0, 2, 7);

        Assert.Equal(1, CountOccurrences(patch, "@@ -"));
        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal("top\nl1\nl3\nl4\nl5\nL5\n", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public void Whole_hunk_range_overload_selects_every_line()
    {
        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,3 +1,4 @@", " one", "-two", "+TWO", " three", "+four");
        var patch = PatchBuilder.CreatePatchFromHunk(hunk, 0, hunk.LineItems.Count, isStaging: true);

        Assert.Equal(
            "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,3 +1,4 @@\n one\n-two\n+TWO\n three\n+four\n",
            patch);
    }

    // ---- unstage / discard --------------------------------------------------

    [Fact]
    public async Task Unstages_selected_lines_of_a_staged_change()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "a\nb\nc\n");
        await repo.StageAsync("f.txt", "a\nB\nc\nd\n");

        // Index vs HEAD.
        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,3 +1,4 @@", " a", "-b", "+B", " c", "+d");

        var unstageAdded = Build(hunk, false, 4);
        Assert.Contains("@@ -1,3 +1,4 @@\n a\n B\n c\n+d\n", unstageAdded);
        await repo.ApplyAsync(unstageAdded, cached: true, reverse: true);
        Assert.Equal("a\nB\nc\n", await repo.IndexAsync("f.txt"));

        await repo.StageAsync("f.txt", "a\nB\nc\nd\n");
        var unstageEdit = Build(hunk, false, 1, 2);
        await repo.ApplyAsync(unstageEdit, cached: true, reverse: true);
        Assert.Equal("a\nb\nc\nd\n", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public async Task Discards_selected_lines_in_the_working_tree()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "a\nb\nc\nd\n");
        await repo.WriteAsync("f.txt", "a\nB\nc\nD\ne\n");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,4 +1,5 @@",
            " a", "-b", "+B", " c", "-d", "+D", "+e");
        // Discard the b→B change and the added e; keep d→D.
        var patch = Build(hunk, false, 1, 2, 6);

        await repo.ApplyAsync(patch, cached: false, reverse: true);
        Assert.Equal("a\nb\nc\nD\n", await repo.WorkTreeAsync("f.txt"));
        Assert.Equal("a\nb\nc\nd\n", await repo.IndexAsync("f.txt"));
    }

    // ---- CRLF / missing newline --------------------------------------------

    [Fact]
    public async Task Crlf_file_keeps_its_carriage_returns()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "a\r\nb\r\nc\r\n");
        await repo.WriteAsync("f.txt", "a\r\nB\r\nc\r\nd\r\n");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,3 +1,4 @@", " a\r", "-b\r", "+B\r", " c\r", "+d\r");

        var stage = Build(hunk, true, 4);
        Assert.Contains("+d\r\n", stage);
        await repo.ApplyAsync(stage, cached: true, reverse: false);
        Assert.Equal("a\r\nb\r\nc\r\nd\r\n", await repo.IndexAsync("f.txt"));

        var discard = Build(hunk, false, 1, 2);
        await repo.ApplyAsync(discard, cached: false, reverse: true);
        Assert.Equal("a\r\nb\r\nc\r\nd\r\n", await repo.WorkTreeAsync("f.txt"));
    }

    [Fact]
    public async Task Added_last_line_without_newline_in_both_directions()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "a\nb\n");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,2 +1,3 @@", " a", " b", "+c", NoEol);
        var patch = Build(hunk, true, 2);
        Assert.EndsWith("+c\n" + NoEol + "\n", patch);

        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal("a\nb\nc", await repo.IndexAsync("f.txt"));

        await repo.ApplyAsync(Build(hunk, false, 2), cached: true, reverse: true);
        Assert.Equal("a\nb\n", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public async Task Removed_last_line_without_newline_in_both_directions()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "a\nb\nc");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,3 +1,2 @@", " a", " b", "-c", NoEol);

        await repo.ApplyAsync(Build(hunk, true, 2), cached: true, reverse: false);
        Assert.Equal("a\nb\n", await repo.IndexAsync("f.txt"));

        await repo.ApplyAsync(Build(hunk, false, 2), cached: true, reverse: true);
        Assert.Equal("a\nb\nc", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public async Task Context_last_line_without_newline_in_both_directions()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "a\nb\nc");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,3 +1,3 @@", "-a", "+A", " b", " c", NoEol);
        var stage = Build(hunk, true, 0, 1);
        Assert.EndsWith(" c\n" + NoEol + "\n", stage);

        await repo.ApplyAsync(stage, cached: true, reverse: false);
        Assert.Equal("A\nb\nc", await repo.IndexAsync("f.txt"));

        await repo.ApplyAsync(Build(hunk, false, 0, 1), cached: true, reverse: true);
        Assert.Equal("a\nb\nc", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public async Task Unselected_removal_without_newline_converted_to_context_keeps_the_file_valid()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "a\nlast");

        // "a\nlast" -> "a\nlast\nextra" as git reports it.
        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,2 +1,3 @@", " a", "-last", NoEol, "+last", "+extra", NoEol);

        // Stage only "+extra": "last" stays, so it has to gain its newline.
        await repo.ApplyAsync(Build(hunk, true, 4), cached: true, reverse: false);
        Assert.Equal("a\nlast\nextra", await repo.IndexAsync("f.txt"));

        // Unstage only "+extra": the newline change on "last" stays staged.
        await repo.ApplyAsync(Build(hunk, false, 4), cached: true, reverse: true);
        Assert.Equal("a\nlast\n", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public async Task Unselected_addition_without_newline_converted_to_context_in_reverse()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "x");
        await repo.StageAsync("f.txt", "y");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1 +1 @@", "-x", NoEol, "+y", NoEol);

        // Unstage only the removal of x: x comes back, y stays.
        await repo.ApplyAsync(Build(hunk, false, 0), cached: true, reverse: true);
        Assert.Equal("x\ny", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public async Task Discards_added_last_line_without_newline()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "a\nb\n");
        await repo.WriteAsync("f.txt", "a\nb\nc");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,2 +1,3 @@", " a", " b", "+c", NoEol);
        await repo.ApplyAsync(Build(hunk, false, 2), cached: false, reverse: true);
        Assert.Equal("a\nb\n", await repo.WorkTreeAsync("f.txt"));
    }

    // ---- headers and paths --------------------------------------------------

    [Fact]
    public async Task Rename_with_modification_keeps_the_rename_headers()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("old.txt", "a\nb\nc\n");

        FileDiff file = Edited("new.txt");
        file.State = PathState.Moved;
        file.OldPath = "old.txt";
        Hunk hunk = MakeHunk(file, "@@ -1,3 +1,4 @@", " a", "-b", "+B", " c", "+d");
        var patch = Build(hunk, true, 4);

        Assert.StartsWith("diff --git a/old.txt b/new.txt\nrename from old.txt\nrename to new.txt\n--- a/old.txt\n+++ b/new.txt\n", patch);
        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal("a\nb\nc\nd\n", await repo.IndexAsync("new.txt"));
        ProcessResult old = await repo.GitAsync(["ls-files", "--cached", "--", "old.txt"]);
        Assert.Equal(string.Empty, old.Output.Trim());
    }

    [Fact]
    public void Mode_change_and_copy_headers()
    {
        FileDiff file = Edited("copy.sh");
        file.State = PathState.Duplicated;
        file.OldPath = "orig.sh";
        file.OldMode = 100644;
        file.NewMode = 100755;
        Hunk hunk = MakeHunk(file, "@@ -1 +1,2 @@", " x", "+y");

        var patch = Build(hunk, true, 1);
        Assert.StartsWith(
            "diff --git a/orig.sh b/copy.sh\nold mode 100644\nnew mode 100755\ncopy from orig.sh\ncopy to copy.sh\n--- a/orig.sh\n+++ b/copy.sh\n@@ -1 +1,2 @@\n",
            patch);

        // Numeric (already-converted) modes are formatted as octal.
        file.OldMode = Convert.ToInt32("100644", 8);
        file.NewMode = Convert.ToInt32("100755", 8);
        Assert.Contains("old mode 100644\nnew mode 100755\n", Build(hunk, true, 1));
    }

    [Fact]
    public async Task Path_with_a_space_applies()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("dir x/my file.txt", "a\nb\n");

        Hunk hunk = MakeHunk(Edited("dir x/my file.txt"), "@@ -1,2 +1,3 @@", " a", "+mid", " b", "+end");
        var patch = Build(hunk, true, 1);

        Assert.Contains("diff --git a/dir x/my file.txt b/dir x/my file.txt\n", patch);
        Assert.Contains("--- a/dir x/my file.txt\t\n+++ b/dir x/my file.txt\t\n", patch);
        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal("a\nmid\nb\n", await repo.IndexAsync("dir x/my file.txt"));
    }

    [Fact]
    public async Task Non_ascii_path_is_c_quoted_and_applies()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("café.txt", "a\nb\n");

        Hunk hunk = MakeHunk(Edited("café.txt"), "@@ -1,2 +1,2 @@", " a", "-b", "+B");
        var patch = Build(hunk, true, 1, 2);

        Assert.Contains("diff --git \"a/caf\\303\\251.txt\" \"b/caf\\303\\251.txt\"\n", patch);
        Assert.Contains("--- \"a/caf\\303\\251.txt\"\n+++ \"b/caf\\303\\251.txt\"\n", patch);
        Assert.All(patch, c => Assert.True(c < 0x80));
        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal("a\nB\n", await repo.IndexAsync("café.txt"));
    }

    [Fact]
    public void Control_characters_quotes_and_backslashes_are_escaped()
    {
        Hunk hunk = MakeHunk(Edited("t\tq\"b\\.txt"), "@@ -1 +1 @@", "-a", "+b");
        var patch = Build(hunk, true, 0, 1);
        Assert.StartsWith("diff --git \"a/t\\tq\\\"b\\\\.txt\" \"b/t\\tq\\\"b\\\\.txt\"\n", patch);
    }

    // ---- new files ----------------------------------------------------------

    [Fact]
    public async Task New_file_stage_selected_lines_then_unstage_some()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("other.txt", "x\n");
        await repo.WriteAsync("n.txt", "1\n2\n3\n4");

        FileDiff untracked = Edited("n.txt");
        untracked.State = PathState.Untracked;
        Hunk hunk = MakeHunk(untracked, "@@ -0,0 +1,4 @@", "+1", "+2", "+3", "+4", NoEol);
        hunk.LineItems[1].Selected = true;
        hunk.LineItems[3].Selected = true;

        var stage = PatchBuilder.CreateNewFileSelectionPatch(hunk, forUnstage: false);
        Assert.NotNull(stage);
        Assert.Contains("new file mode 100644\n--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1,2 @@\n+2\n+4\n" + NoEol + "\n", stage);
        await repo.ApplyAsync(stage!, cached: true, reverse: false);
        Assert.Equal("2\n4", await repo.IndexAsync("n.txt"));

        // Now the file is new in the index; unstage "2".
        FileDiff staged = Edited("n.txt");
        staged.State = PathState.New;
        Hunk stagedHunk = MakeHunk(staged, "@@ -0,0 +1,2 @@", "+2", "+4", NoEol);
        stagedHunk.LineItems[0].Selected = true;

        var unstage = PatchBuilder.CreateNewFileSelectionPatch(stagedHunk, forUnstage: true);
        Assert.NotNull(unstage);
        await repo.ApplyAsync(unstage!, cached: true, reverse: true);
        Assert.Equal("4", await repo.IndexAsync("n.txt"));

        // Unstaging every remaining line leaves an empty index copy.
        Hunk lastHunk = MakeHunk(staged, "@@ -0,0 +1 @@", "+4", NoEol);
        lastHunk.LineItems[0].Selected = true;
        var unstageAll = PatchBuilder.CreateNewFileSelectionPatch(lastHunk, forUnstage: true);
        Assert.NotNull(unstageAll);
        await repo.ApplyAsync(unstageAll!, cached: true, reverse: true);
        Assert.Equal(string.Empty, await repo.IndexAsync("n.txt"));
    }

    [Fact]
    public async Task New_file_selection_not_at_the_end_gets_newlines()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("other.txt", "x\n");

        FileDiff untracked = Edited("n.txt");
        untracked.State = PathState.Untracked;
        untracked.NewMode = 100755;
        Hunk hunk = MakeHunk(untracked, "@@ -0,0 +1,3 @@", "+1", "+2", "+3", NoEol);

        var stage = PatchBuilder.CreateNewFileSelectionPatch(hunk, [0, 1], forUnstage: false);
        Assert.NotNull(stage);
        Assert.Contains("new file mode 100755\n", stage);
        Assert.DoesNotContain(NoEol, stage);
        await repo.ApplyAsync(stage!, cached: true, reverse: false);
        Assert.Equal("1\n2\n", await repo.IndexAsync("n.txt"));

        // Unstage the middle of the index copy.
        FileDiff staged = Edited("n.txt");
        staged.State = PathState.New;
        Hunk stagedHunk = MakeHunk(staged, "@@ -0,0 +1,2 @@", "+1", "+2");
        var unstage = PatchBuilder.CreateNewFileSelectionPatch(stagedHunk, [0], forUnstage: true);
        await repo.ApplyAsync(unstage!, cached: true, reverse: true);
        Assert.Equal("2\n", await repo.IndexAsync("n.txt"));
    }

    [Fact]
    public void New_file_selection_without_added_lines_is_null()
    {
        FileDiff untracked = Edited("n.txt");
        untracked.State = PathState.Untracked;
        Hunk hunk = MakeHunk(untracked, "@@ -0,0 +1,2 @@", "+1", "+2");
        Assert.Null(PatchBuilder.CreateNewFileSelectionPatch(hunk, forUnstage: false));
        Assert.Null(PatchBuilder.CreateNewFileSelectionPatch(hunk, forUnstage: true));
    }

    // ---- empty sides ----------------------------------------------------------

    [Fact]
    public async Task Empty_old_side_gets_a_start_when_lines_become_context()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", string.Empty);
        await repo.StageAsync("f.txt", "a\nb\n");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -0,0 +1,2 @@", "+a", "+b");

        // Unstage only "+a": "b" becomes context on the (previously empty) old side.
        var unstage = Build(hunk, false, 0);
        Assert.Contains("@@ -1 +1,2 @@\n+a\n b\n", unstage);
        await repo.ApplyAsync(unstage, cached: true, reverse: true);
        Assert.Equal("b\n", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public async Task New_side_becomes_empty_when_every_line_is_removed()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("f.txt", "a\nb\n");

        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,2 +1 @@", "-a", "-b", "+c");
        var patch = Build(hunk, true, 0, 1);
        Assert.Contains("@@ -1,2 +0,0 @@\n-a\n-b\n", patch);
        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal(string.Empty, await repo.IndexAsync("f.txt"));
    }

    // ---- null results ---------------------------------------------------------

    [Fact]
    public void Selection_without_changes_is_null()
    {
        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,3 +1,3 @@", " a", "-b", "+B", " c");

        Assert.Null(PatchBuilder.CreateSelectionPatch(hunk, [0, 3], isStaging: true));
        Assert.Null(PatchBuilder.CreateSelectionPatch(hunk, [0, 3], isStaging: false));
        Assert.Null(PatchBuilder.CreateSelectionPatch(hunk, [], isStaging: true));
        Assert.Null(PatchBuilder.CreateSelectionPatch(hunk, [42], isStaging: true));
        Assert.Null(PatchBuilder.CreatePatchFromHunk(hunk, 3, 1, isStaging: true));
    }

    [Fact]
    public void Truncated_hunk_is_null()
    {
        Hunk hunk = MakeHunk(Edited("f.txt"), "@@ -1,3 +1,3 @@", " a", "-b", "+B");
        hunk.Truncated = true;

        Assert.Null(PatchBuilder.CreateSelectionPatch(hunk, [1, 2], isStaging: true));
        Assert.Null(PatchBuilder.CreatePatchFromHunk(hunk, 0, hunk.LineItems.Count, isStaging: false));
        Assert.Null(PatchBuilder.CreateNewFileSelectionPatch(hunk, [1, 2], forUnstage: false));
    }

    // ---- whole file -----------------------------------------------------------

    [Fact]
    public async Task Whole_file_patch_for_new_file_applies()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        await repo.CommitAsync("other.txt", "x\n");

        FileDiff file = Edited("new.txt");
        file.State = PathState.New;
        file.NewMode = 100644;
        MakeHunk(file, "@@ -0,0 +1,2 @@", "+one", "+two", NoEol);

        var patch = PatchBuilder.CreatePatchFromFile(file);
        Assert.Equal(
            "diff --git a/new.txt b/new.txt\nnew file mode 100644\n--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1,2 @@\n+one\n+two\n" + NoEol + "\n",
            patch);
        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal("one\ntwo", await repo.IndexAsync("new.txt"));
    }

    [Fact]
    public async Task Whole_file_patch_with_several_hunks_applies()
    {
        using PatchRepo repo = await PatchRepo.CreateAsync();
        if (repo.Skip) { return; }
        var original = string.Concat(Enumerable.Range(1, 20).Select(i => $"l{i}\n"));
        await repo.CommitAsync("f.txt", original);

        FileDiff file = Edited("f.txt");
        MakeHunk(file, "@@ -1,4 +1,4 @@ first", "-l1", "+L1", " l2", " l3", " l4");
        MakeHunk(file, "@@ -17,4 +17,5 @@", " l17", " l18", " l19", " l20", "+l21");

        var patch = PatchBuilder.CreatePatchFromFile(file);
        Assert.Contains("@@ -1,4 +1,4 @@ first\n", patch);
        await repo.ApplyAsync(patch, cached: true, reverse: false);
        Assert.Equal(original.Replace("l1\n", "L1\n", StringComparison.Ordinal) + "l21\n", await repo.IndexAsync("f.txt"));
    }

    [Fact]
    public void Whole_file_patch_for_deleted_and_binary_files()
    {
        FileDiff removed = Edited("gone.txt");
        removed.State = PathState.Removed;
        removed.OldMode = 100644;
        MakeHunk(removed, "@@ -1 +0,0 @@", "-bye");
        Assert.Equal(
            "diff --git a/gone.txt b/gone.txt\ndeleted file mode 100644\n--- a/gone.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-bye\n",
            PatchBuilder.CreatePatchFromFile(removed));

        var binary = new FileDiff(PendingSetup) { FileName = "logo.png", State = PathState.New, Binary = true };
        Assert.Equal(
            "diff --git a/logo.png b/logo.png\nnew file mode 100644\nBinary files /dev/null and b/logo.png differ\n",
            PatchBuilder.CreatePatchFromFile(binary));
    }

    [Fact]
    public void Whole_file_patch_round_trips_through_the_parser()
    {
        var files = new List<FileDiff>();

        FileDiff edited = Edited("dir/a file.txt");
        MakeHunk(edited, "@@ -1,3 +1,4 @@ fn", " a\r", "-b", "+B", " c", "+d", NoEol);
        MakeHunk(edited, "@@ -10,2 +11,2 @@", " x", "-y", "+Y");
        files.Add(edited);

        FileDiff added = Edited("café.txt");
        added.State = PathState.New;
        MakeHunk(added, "@@ -0,0 +1,2 @@", "+one", "+two");
        files.Add(added);

        FileDiff removed = Edited("gone.txt");
        removed.State = PathState.Removed;
        MakeHunk(removed, "@@ -1 +0,0 @@", "-bye");
        files.Add(removed);

        FileDiff moved = Edited("new.txt");
        moved.State = PathState.Moved;
        moved.OldPath = "old.txt";
        MakeHunk(moved, "@@ -1 +1 @@", "-old", "+new");
        files.Add(moved);

        foreach (FileDiff original in files)
        {
            var text = PatchBuilder.CreatePatchFromFile(original);
            FileDiff parsed = Assert.Single(UnifiedDiffParser.Parse(text, new DiffParseOptions { Setup = PendingSetup }));

            Assert.Equal(original.FileName, parsed.FileName);
            Assert.Equal(original.State, parsed.State);
            Assert.Equal(original.Hunks.Count, parsed.Hunks.Count);
            for (var h = 0; h < original.Hunks.Count; h++)
            {
                IHunk expected = original.Hunks[h];
                IHunk actual = parsed.Hunks[h];
                Assert.Equal(expected.LineItems.Select(l => (l.Marker, l.RawText)), actual.LineItems.Select(l => (l.Marker, l.RawText)));
            }
        }
    }

    // ---- helpers --------------------------------------------------------------

    private static FileDiff Edited(string path) => new(PendingSetup) { FileName = path, State = PathState.Edited };

    /// <summary>
    /// Builds a hunk from its header and body lines (marker first, as in a diff),
    /// filling line numbers the way the parser does, and adds it to the owner.
    /// </summary>
    private static Hunk MakeHunk(FileDiff owner, string header, params string[] lines)
    {
        var parts = header.Split(' ');
        (var oldStart, var oldCount) = ParseRange(parts[1]);
        (var newStart, var newCount) = ParseRange(parts[2]);
        var hunk = new Hunk(owner)
        {
            Number = owner.Hunks.Count + 1,
            RawText = header,
            OldStart = oldCount == 0 ? 0 : oldStart,
            OldEnd = oldCount == 0 ? 0 : oldStart + oldCount - 1,
            NewStart = newCount == 0 ? 0 : newStart,
            NewEnd = newCount == 0 ? 0 : newStart + newCount - 1
        };

        int oldNo = oldStart, newNo = newStart;
        foreach (var line in lines)
        {
            var marker = line[0];
            var raw = line[1..];
            var item = new HunkLine { Marker = marker, RawText = raw, Text = raw.TrimEnd('\r') };
            switch (marker)
            {
                case ' ':
                    item.OldLineNo = (oldNo++).ToString();
                    item.NewLineNo = (newNo++).ToString();
                    break;
                case '-':
                    item.OldLineNo = (oldNo++).ToString();
                    break;
                case '+':
                    item.NewLineNo = (newNo++).ToString();
                    break;
            }

            hunk.LineItems.Add(item);
        }

        owner.Hunks.Add(hunk);
        return hunk;

        static (int Start, int Count) ParseRange(string range)
        {
            var nums = range[1..].Split(',');
            return (int.Parse(nums[0]), nums.Length > 1 ? int.Parse(nums[1]) : 1);
        }
    }

    private static string Build(IHunk hunk, bool isStaging, params int[] selection)
    {
        var patch = PatchBuilder.CreateSelectionPatch(hunk, selection, isStaging);
        Assert.NotNull(patch);
        Assert.EndsWith("\n", patch);
        return patch!;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>A temporary repository with byte-exact files (no CRLF conversion).</summary>
    private sealed class PatchRepo : IDisposable
    {
        private readonly TempRepo? _repo;

        private PatchRepo(TempRepo? repo) => _repo = repo;

        public bool Skip => _repo is null;

        private TempRepo Repo => _repo ?? throw new InvalidOperationException("git is not available");

        public static async Task<PatchRepo> CreateAsync()
        {
            if (!await TempRepo.GitAvailableAsync())
            {
                return new PatchRepo(null);
            }

            TempRepo repo = await TempRepo.CreateAsync(withHistory: false);
            await repo.Git.RunAsync(repo.Path, ["config", "core.autocrlf", "false"]);
            await repo.Git.RunAsync(repo.Path, ["config", "core.safecrlf", "false"]);
            return new PatchRepo(repo);
        }

        public async Task WriteAsync(string path, string content)
        {
            var full = Path.Combine(Repo.Path, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, new UTF8Encoding(false).GetBytes(content));
        }

        public async Task StageAsync(string path, string content)
        {
            await WriteAsync(path, content);
            await CheckAsync(["add", "--", path]);
        }

        public async Task CommitAsync(string path, string content)
        {
            await StageAsync(path, content);
            await CheckAsync(["commit", "-q", "-m", "commit " + path]);
        }

        public Task<ProcessResult> GitAsync(IReadOnlyList<string> args) => Repo.Git.RunAsync(Repo.Path, args);

        public async Task<string> IndexAsync(string path) => (await CheckAsync(["cat-file", "blob", ":" + path])).Output;

        public async Task<string> WorkTreeAsync(string path)
            => new UTF8Encoding(false).GetString(await File.ReadAllBytesAsync(Path.Combine(Repo.Path, path.Replace('/', Path.DirectorySeparatorChar))));

        /// <summary>Checks the patch with <c>git apply --check</c>, then applies it.</summary>
        public async Task ApplyAsync(string patch, bool cached, bool reverse)
        {
            var check = new List<string> { "apply", "--check", "--whitespace=nowarn" };
            if (cached) { check.Add("--cached"); }
            if (reverse) { check.Add("-R"); }
            ProcessResult checkResult = await Repo.Git.RunAsync(Repo.Path, check, patch);
            Assert.True(checkResult.Success, "git apply --check failed:\n" + checkResult.Combined + "\n--- patch ---\n" + patch);

            ProcessResult result = await Repo.Git.ApplyPatchAsync(Repo.Path, patch, cached, reverse, allowFuzzy: false);
            Assert.True(result.Success, "git apply failed:\n" + result.Combined + "\n--- patch ---\n" + patch);
        }

        private async Task<ProcessResult> CheckAsync(IReadOnlyList<string> args)
        {
            ProcessResult result = await Repo.Git.RunAsync(Repo.Path, args);
            Assert.True(result.Success, $"git {string.Join(' ', args)} failed: {result.Combined}");
            return result;
        }

        public void Dispose() => _repo?.Dispose();
    }
}
