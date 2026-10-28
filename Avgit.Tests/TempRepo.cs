using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

public sealed class TempRepo : IDisposable
{
    private TempRepo(string path, GitRepoHandler git)
    {
        Path = path;
        Git = git;
    }

    public string Path { get; }
    public GitRepoHandler Git { get; }

    public static async Task<bool> GitAvailableAsync()
    {
        ProcessResult result = await ProcessRunner.RunAsync("git", System.IO.Path.GetTempPath(), ["--version"]);
        return result.Success;
    }

    public static async Task<TempRepo> CreateAsync(bool withHistory = true)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "st-av-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var git = new GitRepoHandler();
        ProcessResult init = await git.InitAsync(path);
        if (!init.Success)
        {
            throw new InvalidOperationException("git init failed: " + init.Combined);
        }

        await git.RunAsync(path, ["config", "user.name", "Test User"]);
        await git.RunAsync(path, ["config", "user.email", "test@example.com"]);
        await git.RunAsync(path, ["config", "commit.gpgsign", "false"]);

        var repo = new TempRepo(path, git);
        if (withHistory)
        {
            await repo.CreateHistoryAsync();
        }

        return repo;
    }

    private async Task CreateHistoryAsync()
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(Path, "readme.txt"), "one\ntwo\nthree\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(Path, "program.cs"), "class Program { }\n");
        await StageAllAsync();
        ProcessResult first = await Git.CommitAsync(Path, "Initial commit");
        if (!first.Success)
        {
            throw new InvalidOperationException("initial commit failed: " + first.Combined);
        }

        await File.AppendAllTextAsync(System.IO.Path.Combine(Path, "readme.txt"), "four\nfive\n");
        await StageAllAsync();
        await Git.CommitAsync(Path, "Add more lines");

        await File.WriteAllTextAsync(System.IO.Path.Combine(Path, "graph.txt"), "graph\nrendering\n");
        await StageAllAsync();
        await Git.CommitAsync(Path, "Add graph file");
    }

    public async Task AddWorkingChangesAsync()
    {
        await File.AppendAllTextAsync(System.IO.Path.Combine(Path, "readme.txt"), "working change\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(Path, "untracked.txt"), "untracked\ncontent\n");
    }

    public async Task StageAllAsync()
    {
        StatusSnapshot status = await Git.GetStatusAsync(Path);
        var files = status.Unstaged.Concat(status.Staged).ToList();
        if (files.Count > 0)
        {
            await Git.StageAsync(Path, files);
        }
    }

    public async Task<string> CreateCommitAsync(string message, string file, string content)
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(Path, file), content);
        await StageAllAsync();
        ProcessResult result = await Git.CommitAsync(Path, message);
        if (!result.Success)
        {
            throw new InvalidOperationException("commit failed: " + result.Combined);
        }

        return (await Git.GetCurrentRevisionAsync(Path));
    }

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
            // Test temp folders are best-effort cleanup only.
        }
    }
}
