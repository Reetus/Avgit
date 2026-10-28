using Avgit.App.ViewModels;
using Avgit.Git;

namespace Avgit.Tests;

public class CloneOptionsTests
{
    [Fact]
    public void Splits_clone_options_honouring_quotes()
    {
        Assert.Equal(["--depth", "1", "--branch", "my branch"],
            MainWindowViewModel.SplitCommandLine("  --depth 1 --branch \"my branch\" "));
        Assert.Empty(MainWindowViewModel.SplitCommandLine("   "));
        Assert.Equal([""], MainWindowViewModel.SplitCommandLine("\"\""));
    }

    [Fact]
    public async Task Clone_accepts_trailing_separator_and_extra_options()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var temp = Path.Combine(Path.GetTempPath(), "avgit-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var destination = Path.Combine(temp, "copy") + Path.DirectorySeparatorChar;
            ProcessResult result = await new GitRepoHandler().CloneAsync(repo.Path, destination, partial: false,
                ["--no-checkout"]);
            Assert.True(result.Success, result.Combined);

            var clone = Path.Combine(temp, "copy");
            Assert.True(Directory.Exists(Path.Combine(clone, ".git")));
            // --no-checkout leaves the working tree empty apart from .git.
            Assert.Single(Directory.EnumerateFileSystemEntries(clone));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
