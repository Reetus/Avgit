using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.App.Views;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>"Push after committing" and the toolbar's Remote button.</summary>
public class CommitPushTests
{
    private static readonly string ArtifactDirectory =
        Path.Combine(Path.GetTempPath(), "avgit-headless");

    [Theory]
    [InlineData("https://github.com/Reetus/Avgit.git", "https://github.com/Reetus/Avgit")]
    [InlineData("https://user:token@github.com/Reetus/Avgit.git/", "https://github.com/Reetus/Avgit")]
    [InlineData("http://git.example.com:8080/team/repo", "http://git.example.com:8080/team/repo")]
    [InlineData("  https://gitlab.com/group/sub/project.GIT  ", "https://gitlab.com/group/sub/project")]
    [InlineData("git@github.com:Reetus/Avgit.git", null)]
    [InlineData("ssh://git@github.com/Reetus/Avgit.git", null)]
    [InlineData("D:/repos/remote.git", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Remote_web_address_comes_from_http_urls_only(string? remoteUrl, string? expected)
        => Assert.Equal(expected, RepositoryViewModel.WebUrlFor(remoteUrl));

    [AvaloniaFact]
    public async Task Push_after_committing_pushes_the_new_commit()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var remote = repo.Path + "-remote.git";
        try
        {
            await ProcessRunner.RunAsync("git", Path.GetTempPath(), ["init", "--bare", remote]);
            await repo.Git.AddRemoteAsync(repo.Path, "origin", remote);

            var settingsFile = Path.Combine(ArtifactDirectory, $"settings-commitpush-{Guid.NewGuid():N}.json");
            var vm = new MainWindowViewModel(new AppSettingsService(settingsFile), new FakeDialogService());
            await vm.OpenPathAsync(repo.Path);
            RepositoryViewModel repository = vm.Repository!;
            await WaitForAsync(() => repository.Remotes.Count == 1);
            // A local path is not a web page.
            Assert.False(repository.HasRemoteWebUrl);

            Assert.Equal("Commit", repository.CommitButtonText);
            repository.PushAfterCommit = true;
            Assert.Equal("Commit & Push", repository.CommitButtonText);
            Assert.True(new AppSettingsService(settingsFile).Settings.CommitPushAfter);

            await File.WriteAllTextAsync(Path.Combine(repo.Path, "pushed.txt"), "pushed\n");
            await repo.StageAllAsync();
            await repository.RefreshAsync();
            repository.CommitMessage = "Commit and push";
            await WaitForAsync(() => repository.CanCommit);
            repository.CommitCommand.Execute(null);

            // The branch had no upstream; the push sets it.
            await WaitForAsync(() => !repository.IsBusy && repository.StatusMessage == "Push completed.");
            var head = await repo.Git.GetCurrentRevisionAsync(repo.Path);
            Assert.Equal("Commit and push", repository.LogRows[0].Message);
            ProcessResult remoteHead = await ProcessRunner.RunAsync("git", remote, ["rev-parse", repository.CurrentBranch]);
            Assert.Equal(head, remoteHead.Output.Trim());
        }
        finally
        {
            if (Directory.Exists(remote))
            {
                foreach (var file in Directory.EnumerateFiles(remote, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(remote, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task Push_after_committing_without_remotes_only_commits_and_https_origin_shows_the_remote_button()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        var settingsFile = Path.Combine(ArtifactDirectory, $"settings-commitpush-{Guid.NewGuid():N}.json");
        var dialogs = new FakeDialogService();
        var vm = new MainWindowViewModel(new AppSettingsService(settingsFile), dialogs);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        await vm.OpenPathAsync(repo.Path);
        RepositoryViewModel repository = vm.Repository!;
        repository.PushAfterCommit = true;

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "local.txt"), "local\n");
        await repo.StageAllAsync();
        await repository.RefreshAsync();
        repository.CommitMessage = "Local only";
        await WaitForAsync(() => repository.CanCommit);
        repository.CommitCommand.Execute(null);
        await WaitForAsync(() => !repository.IsBusy && repository.StatusMessage.StartsWith("Commit created", StringComparison.Ordinal));
        Assert.Equal("Commit created. Not pushed: no remotes are configured.", repository.StatusMessage);
        Assert.Empty(dialogs.Messages);

        await repo.Git.AddRemoteAsync(repo.Path, "origin", "https://user:secret@example.com/team/project.git");
        await repository.RefreshAsync();
        Assert.True(repository.HasRemoteWebUrl);
        Assert.Equal("https://example.com/team/project", repository.RemoteWebUrl);

        Button remoteButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Remote");
        Assert.True(remoteButton.IsEffectivelyVisible);
        Assert.Same(repository.OpenRemoteCommand, remoteButton.Command);

        TabControl tabs = window.GetVisualDescendants().OfType<TabControl>()
            .First(t => t.Items.OfType<TabItem>().Any(i => i.Header as string == "File Status"));
        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ArtifactDirectory);
        window.CaptureRenderedFrame()!.Save(Path.Combine(ArtifactDirectory, "commit-push.png"), PngBitmapEncoderOptions.Default);
        window.Close();
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
