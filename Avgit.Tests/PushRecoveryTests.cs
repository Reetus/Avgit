using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.Git;

namespace Avgit.Tests;

public class PushOutcomeTests
{
    // Outputs captured from git 2.39 `git push --porcelain`.
    private const string To = "To ../remote.git\n";

    private static PushOutcome Parse(int exit, string stdout, string stderr = "error: failed to push some refs to '../remote.git'\n")
        => PushOutcome.Parse(new ProcessResult(exit, stdout, exit == 0 ? string.Empty : stderr));

    [Theory]
    [InlineData("[rejected] (fetch first)", PushRejection.FetchFirst)]
    [InlineData("[rejected] (non-fast-forward)", PushRejection.NonFastForward)]
    [InlineData("[rejected] (stale info)", PushRejection.StaleLease)]
    [InlineData("[remote rejected] (pre-receive hook declined)", PushRejection.RemoteRejected)]
    [InlineData("[rejected] (already exists)", PushRejection.Other)]
    public void Classifies_rejected_refs(string summary, PushRejection expected)
    {
        PushOutcome outcome = Parse(1, To + $"!\trefs/heads/main:refs/heads/main\t{summary}\nDone\n");
        Assert.Equal(expected, outcome.Rejection);
        PushRefResult rejected = Assert.Single(outcome.Refs);
        Assert.True(rejected.Rejected);
        Assert.Equal("refs/heads/main", rejected.Destination);
    }

    [Fact]
    public void Successful_pushes_are_not_rejections()
    {
        Assert.Equal(PushRejection.None, Parse(0, To + "+\trefs/heads/main:refs/heads/main\tafc8bc2...1c9da29 (forced update)\nDone\n").Rejection);
        Assert.Equal(PushRejection.None, Parse(0, To + "=\trefs/heads/main:refs/heads/main\t[up to date]\nDone\n").Rejection);
    }

    [Fact]
    public void Collects_the_servers_hook_message()
    {
        PushOutcome outcome = Parse(1,
            To + "!\trefs/heads/main:refs/heads/main\t[remote rejected] (pre-receive hook declined)\nDone\n",
            "remote: branch is protected        \nremote: ask a maintainer\nerror: failed to push some refs\n");
        Assert.Equal(PushRejection.RemoteRejected, outcome.Rejection);
        Assert.Equal("pre-receive hook declined", outcome.RejectedRef?.Reason);
        Assert.Equal("branch is protected\nask a maintainer", outcome.RemoteMessage);
    }

    [Theory]
    [InlineData("fatal: The current branch topic has no upstream branch.\nTo push the current branch and set the remote as upstream, use\n\n    git push --set-upstream origin topic\n", PushRejection.NoUpstream)]
    [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://example.com/repo.git/'\n", PushRejection.Authentication)]
    [InlineData("fatal: could not read Username for 'https://example.com': terminal prompts disabled\n", PushRejection.Authentication)]
    [InlineData("git@example.com: Permission denied (publickey).\nfatal: Could not read from remote repository.\n", PushRejection.Authentication)]
    [InlineData("fatal: unable to access 'https://example.com/repo.git/': The requested URL returned error: 403\n", PushRejection.Authentication)]
    [InlineData("fatal: 'nowhere' does not appear to be a git repository\n", PushRejection.Other)]
    public void Classifies_failures_without_ref_lines(string stderr, PushRejection expected)
        => Assert.Equal(expected, Parse(128, string.Empty, stderr).Rejection);
}

public class PushRecoveryTests
{
    /// <summary>A bare remote, the repository under test (tracking it) and a second clone that pushes competing work.</summary>
    private sealed class Scenario : IDisposable
    {
        public required string Root { get; init; }
        public required string Remote { get; init; }
        public required string Other { get; init; }
        public required TempRepo Repo { get; init; }
        public required string Branch { get; init; }

        public void Dispose()
        {
            Repo.Dispose();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private static async Task<ProcessResult> GitAsync(string directory, params string[] args)
    {
        ProcessResult result = await ProcessRunner.RunAsync("git", directory, ((string[])["-c", "commit.gpgsign=false"]).Concat(args));
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Combined}");
        return result;
    }

    private static async Task<Scenario> CreateAsync()
    {
        TempRepo repo = await TempRepo.CreateAsync();
        var branch = await repo.Git.GetCurrentBranchAsync(repo.Path);
        var root = Path.Combine(Path.GetTempPath(), "avgit-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "remote.git");
        var other = Path.Combine(root, "other");

        await GitAsync(root, "init", "-q", "--bare", remote);
        await GitAsync(repo.Path, "remote", "add", "origin", remote);
        await GitAsync(repo.Path, "push", "-q", "-u", "origin", branch);
        await GitAsync(root, "clone", "-q", "-b", branch, remote, other);
        await GitAsync(other, "config", "user.name", "Other User");
        await GitAsync(other, "config", "user.email", "other@example.com");
        return new Scenario { Root = root, Remote = remote, Other = other, Repo = repo, Branch = branch };
    }

    private static async Task<string> OtherPushesAsync(Scenario s, string file)
    {
        await File.WriteAllTextAsync(Path.Combine(s.Other, file), file + "\n");
        await GitAsync(s.Other, "add", "-A");
        await GitAsync(s.Other, "commit", "-q", "-m", "other: " + file);
        await GitAsync(s.Other, "push", "-q");
        return (await GitAsync(s.Other, "rev-parse", "HEAD")).Output.Trim();
    }

    private static async Task<string> RemoteHeadAsync(Scenario s)
        => (await GitAsync(s.Remote, "rev-parse", "refs/heads/" + s.Branch)).Output.Trim();

    private static async Task<bool> RemoteContainsAsync(Scenario s, string sha)
        => (await ProcessRunner.RunAsync("git", s.Remote, ["merge-base", "--is-ancestor", sha, "refs/heads/" + s.Branch])).Success;

    private static async Task<(RepositoryViewModel Vm, FakeDialogService Dialogs)> OpenAsync(Scenario s)
    {
        var settings = new AppSettingsService(Path.Combine(s.Root, "settings.json"));
        settings.Settings.AutoRefresh = false;
        settings.Settings.MaintainCommitGraph = false;
        var dialogs = new FakeDialogService();
        var vm = new RepositoryViewModel(s.Repo.Git, s.Repo.Path, settings, dialogs);
        await vm.LoadAsync();
        return (vm, dialogs);
    }

    private static async Task RunAsync(RepositoryViewModel vm, System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        var started = Environment.TickCount64;
        while (vm.IsBusy)
        {
            if (Environment.TickCount64 - started > 30000)
            {
                throw new TimeoutException("Push did not finish.");
            }

            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Fetch_first_offers_pull_but_not_force_and_pull_rebase_pushes_linear_history()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        var otherSha = await OtherPushesAsync(s, "theirs.txt");
        await s.Repo.CreateCommitAsync("mine", "mine.txt", "mine\n");
        (RepositoryViewModel? vm, FakeDialogService? dialogs) = await OpenAsync(s);
        using RepositoryViewModel _ = vm;

        dialogs.ChoiceResponses.Enqueue(RepositoryViewModel.PullRebaseThenPush);
        await RunAsync(vm, vm.PushCommand);

        (var message, IReadOnlyList<string>? options) = Assert.Single(dialogs.Choices);
        Assert.Contains("not fetched", message);
        Assert.Equal([RepositoryViewModel.PullRebaseThenPush, RepositoryViewModel.PullMergeThenPush, RepositoryViewModel.FetchOnly], options);
        Assert.Empty(dialogs.Messages);

        var head = (await GitAsync(s.Repo.Path, "rev-parse", "HEAD")).Output.Trim();
        Assert.Equal(head, await RemoteHeadAsync(s));
        Assert.True(await RemoteContainsAsync(s, otherSha));
        var parents = (await GitAsync(s.Remote, "rev-list", "--parents", "-n", "1", "refs/heads/" + s.Branch)).Output.Split(' ');
        Assert.Equal(2, parents.Length); // a single parent: rebased, not merged
    }

    [AvaloniaFact]
    public async Task Pull_merge_then_push_creates_a_merge_commit()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        var otherSha = await OtherPushesAsync(s, "theirs.txt");
        await s.Repo.CreateCommitAsync("mine", "mine.txt", "mine\n");
        (RepositoryViewModel? vm, FakeDialogService? dialogs) = await OpenAsync(s);
        using RepositoryViewModel _ = vm;

        dialogs.ChoiceResponses.Enqueue(RepositoryViewModel.PullMergeThenPush);
        await RunAsync(vm, vm.PushCommand);

        Assert.True(await RemoteContainsAsync(s, otherSha));
        var parents = (await GitAsync(s.Remote, "rev-list", "--parents", "-n", "1", "refs/heads/" + s.Branch)).Output.Trim().Split(' ');
        Assert.Equal(3, parents.Length); // the remote tip is a merge commit
    }

    [Fact]
    public async Task Plain_pull_follows_the_pull_rebase_setting()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        await OtherPushesAsync(s, "theirs.txt");
        await s.Repo.CreateCommitAsync("mine", "mine.txt", "mine\n");
        await GitAsync(s.Repo.Path, "config", "pull.rebase", "true");

        ProcessResult pull = await s.Repo.Git.PullAsync(s.Repo.Path);
        Assert.True(pull.Success, pull.Combined);
        var parents = (await GitAsync(s.Repo.Path, "rev-list", "--parents", "-n", "1", "HEAD")).Output.Trim().Split(' ');
        Assert.Equal(2, parents.Length); // rebased onto theirs, no merge commit
    }

    [AvaloniaFact]
    public async Task Non_fast_forward_offers_force_with_lease_which_replaces_the_remote_branch()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        var otherSha = await OtherPushesAsync(s, "theirs.txt");
        await s.Repo.CreateCommitAsync("mine", "mine.txt", "mine\n");
        await GitAsync(s.Repo.Path, "fetch", "-q"); // their commit is known locally now
        (RepositoryViewModel? vm, FakeDialogService? dialogs) = await OpenAsync(s);
        using RepositoryViewModel _ = vm;

        dialogs.ChoiceResponses.Enqueue(RepositoryViewModel.ForcePushWithLease);
        await RunAsync(vm, vm.PushCommand);

        (var message, IReadOnlyList<string>? options) = Assert.Single(dialogs.Choices);
        Assert.Contains("1 commit that your branch does not contain", message);
        Assert.Contains(RepositoryViewModel.ForcePushWithLease, options);
        Assert.Contains(RepositoryViewModel.PullRebaseThenPush, options);

        var head = (await GitAsync(s.Repo.Path, "rev-parse", "HEAD")).Output.Trim();
        Assert.Equal(head, await RemoteHeadAsync(s));
        Assert.False(await RemoteContainsAsync(s, otherSha));
    }

    [AvaloniaFact]
    public async Task Cancelling_leaves_the_remote_unchanged()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        var otherSha = await OtherPushesAsync(s, "theirs.txt");
        await s.Repo.CreateCommitAsync("mine", "mine.txt", "mine\n");
        (RepositoryViewModel? vm, FakeDialogService? dialogs) = await OpenAsync(s);
        using RepositoryViewModel _ = vm;

        await RunAsync(vm, vm.PushCommand); // no queued answer: the dialog is cancelled

        Assert.Single(dialogs.Choices);
        Assert.Equal(otherSha, await RemoteHeadAsync(s));
        Assert.Contains("rejected", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Stale_lease_only_offers_fetch_and_fetching_updates_the_remote_tracking_branch()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        await OtherPushesAsync(s, "first.txt");
        await s.Repo.CreateCommitAsync("mine", "mine.txt", "mine\n");
        await GitAsync(s.Repo.Path, "fetch", "-q");
        var latest = await OtherPushesAsync(s, "second.txt"); // remote moves after our fetch
        (RepositoryViewModel? vm, FakeDialogService? dialogs) = await OpenAsync(s);
        using RepositoryViewModel _ = vm;

        dialogs.ChoiceResponses.Enqueue(RepositoryViewModel.FetchToReview);
        await RunAsync(vm, vm.ForcePushCommand);

        (var message, IReadOnlyList<string>? options) = Assert.Single(dialogs.Choices);
        Assert.Contains("after your last fetch", message);
        Assert.Equal([RepositoryViewModel.FetchToReview], options);
        Assert.Equal(latest, await RemoteHeadAsync(s));
        var tracking = (await GitAsync(s.Repo.Path, "rev-parse", "refs/remotes/origin/" + s.Branch)).Output.Trim();
        Assert.Equal(latest, tracking);
    }

    [AvaloniaFact]
    public async Task Server_rejections_are_explained_without_retry_options()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using Scenario s = await CreateAsync();
        var hook = Path.Combine(s.Remote, "hooks", "pre-receive");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\necho \"branch is protected\" >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var before = await RemoteHeadAsync(s);
        await s.Repo.CreateCommitAsync("mine", "mine.txt", "mine\n");
        (RepositoryViewModel? vm, FakeDialogService? dialogs) = await OpenAsync(s);
        using RepositoryViewModel _ = vm;

        await RunAsync(vm, vm.PushCommand);

        Assert.Empty(dialogs.Choices);
        var shown = Assert.Single(dialogs.Messages);
        Assert.Contains("pre-receive hook declined", shown);
        Assert.Contains("branch is protected", shown);
        Assert.Equal(before, await RemoteHeadAsync(s));
    }
}
