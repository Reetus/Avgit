using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.Git;

namespace Avgit.Tests;

public class AmendMessageTests
{
    private const string LastMessage = "Fix the parser\n\nHandle quoted paths in diff headers.";

    private static async Task<(TempRepo Repo, RepositoryViewModel Vm, ConfirmingDialogService Dialogs)> OpenAsync()
    {
        TempRepo repo = await TempRepo.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "parser.txt"), "v1\n");
        await repo.StageAllAsync();
        ProcessResult commit = await repo.Git.CommitAsync(repo.Path, LastMessage);
        Assert.True(commit.Success, commit.Combined);

        var settings = new AppSettingsService(Path.Combine(Path.GetTempPath(), "avgit-headless", "settings-amend.json"));
        settings.Settings.AutoRefresh = false;
        var dialogs = new ConfirmingDialogService();
        var vm = new RepositoryViewModel(repo.Git, repo.Path, settings, dialogs);
        await vm.LoadAsync();
        return (repo, vm, dialogs);
    }

    [AvaloniaFact]
    public async Task Ticking_amend_copies_the_last_message_and_unticking_restores_the_draft()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        (TempRepo? repo, RepositoryViewModel? vm, ConfirmingDialogService? dialogs) = await OpenAsync();
        using TempRepo _ = repo;
        using RepositoryViewModel __ = vm;

        // Empty box: filled without asking, with the full multi-line message.
        vm.Amend = true;
        await WaitForAsync(() => vm.CommitMessage == LastMessage);
        Assert.Equal(0, dialogs.ConfirmCount);
        vm.Amend = false;
        Assert.Equal(string.Empty, vm.CommitMessage);

        // A typed draft is only replaced after confirmation, and comes back on untick.
        vm.CommitMessage = "my draft";
        vm.Amend = true;
        await WaitForAsync(() => vm.CommitMessage == LastMessage);
        Assert.Equal(1, dialogs.ConfirmCount);
        vm.Amend = false;
        Assert.Equal("my draft", vm.CommitMessage);

        // Declining keeps the draft (amend stays ticked).
        dialogs.Answer = false;
        vm.Amend = true;
        await WaitForAsync(() => dialogs.ConfirmCount == 2);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("my draft", vm.CommitMessage);
        Assert.True(vm.Amend);
        vm.Amend = false;
        dialogs.Answer = true;

        // An edited amended message is kept when amend is unticked.
        vm.CommitMessage = string.Empty;
        vm.Amend = true;
        await WaitForAsync(() => vm.CommitMessage == LastMessage);
        vm.CommitMessage = LastMessage + "\n\nAlso handle tabs.";
        vm.Amend = false;
        Assert.Equal(LastMessage + "\n\nAlso handle tabs.", vm.CommitMessage);
    }

    [AvaloniaFact]
    public async Task Amending_with_the_copied_message_rewrites_the_last_commit()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        (TempRepo? repo, RepositoryViewModel? vm, ConfirmingDialogService _) = await OpenAsync();
        using TempRepo r = repo;
        using RepositoryViewModel v = vm;

        var before = (await repo.Git.RunAsync(repo.Path, ["rev-list", "--count", "HEAD"])).Output.Trim();
        vm.Amend = true;
        await WaitForAsync(() => vm.CommitMessage == LastMessage && vm.CanCommit);
        vm.CommitCommand.Execute(null);
        await WaitForAsync(() => !vm.Amend && !vm.IsBusy);

        var after = (await repo.Git.RunAsync(repo.Path, ["rev-list", "--count", "HEAD"])).Output.Trim();
        var message = (await repo.Git.RunAsync(repo.Path, ["log", "-1", "--format=%B"])).Output.TrimEnd();
        Assert.Equal(before, after);
        Assert.Equal(LastMessage, message.Replace("\r\n", "\n"));
        Assert.Equal(string.Empty, vm.CommitMessage);
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

    private sealed class ConfirmingDialogService : IDialogService
    {
        private readonly FakeDialogService _inner = new();
        public bool Answer { get; set; } = true;
        public int ConfirmCount { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCount++;
            return Task.FromResult(Answer);
        }

        public Task<string?> PickFolderAsync(string title) => _inner.PickFolderAsync(title);
        public Task<string?> PickFileAsync(string title) => _inner.PickFileAsync(title);
        public Task<string?> PickSaveFileAsync(string title, string defaultName) => _inner.PickSaveFileAsync(title, defaultName);
        public Task ShowTextAsync(string title, string text) => _inner.ShowTextAsync(title, text);
        public Task<string?> PromptAsync(string title, string label, string initial = "") => _inner.PromptAsync(title, label, initial);
        public Task<(string? Value, bool Flag)?> PromptWithCheckBoxAsync(string title, string label, string initial,
            string checkLabel, bool checkDefault = false)
            => _inner.PromptWithCheckBoxAsync(title, label, initial, checkLabel, checkDefault);
        public Task<string?> EditTextAsync(string title, string label, string initial) => _inner.EditTextAsync(title, label, initial);
        public Task<IReadOnlyList<RebasePlanItem>?> EditRebasePlanAsync(string title, IReadOnlyList<RebasePlanItem> commits)
            => _inner.EditRebasePlanAsync(title, commits);
        public Task ShowMessageAsync(string title, string message) => _inner.ShowMessageAsync(title, message);
        public Task<string?> ChooseAsync(string title, string message, IReadOnlyList<string> options)
            => _inner.ChooseAsync(title, message, options);
        public Task<bool> TrimBranchesAsync(TrimBranchesViewModel viewModel) => _inner.TrimBranchesAsync(viewModel);
    }
}
