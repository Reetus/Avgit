using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.Core.Model;
using Avgit.Git;

namespace Avgit.Tests;

public class CommitSelectionRaceTests
{
    [AvaloniaFact]
    public async Task Clicking_a_commit_while_a_refresh_loads_history_shows_that_commit()
    {
        if (!await TempRepo.GitAvailableAsync())
        {
            return;
        }

        using TempRepo repo = await TempRepo.CreateAsync();
        IGitEngine handler = GatedLogHandler.Wrap(repo.Git);
        var settings = new AppSettingsService(Path.Combine(Path.GetTempPath(), "avgit-headless", "settings-race.json"));
        settings.Settings.AutoRefresh = false;
        using var vm = new RepositoryViewModel(handler, repo.Path, settings, new FakeDialogService());
        await vm.LoadAsync();
        Assert.True(vm.LogRows.Count >= 3);

        var shaB = vm.LogRows[1].Row.CommitId;
        var shaC = vm.LogRows[2].Row.CommitId;
        vm.SetContextCommit(vm.LogRows[1]);
        await WaitForAsync(() => vm.CommitInfo?.Sha == shaB);

        // A refresh (what the file watcher triggers) is held inside git log...
        GatedLogHandler.LogGate gate = GatedLogHandler.Gate(handler);
        Task refresh = vm.RefreshAsync();
        await WaitForAsync(() => gate.Entered);

        // ...while the user clicks another commit.
        vm.SetContextCommit(vm.LogRows[2]);
        await WaitForAsync(() => vm.CommitInfo?.Sha == shaC);

        gate.Release();
        await refresh;
        Dispatcher.UIThread.RunJobs();

        // The refresh must keep the user's click, and what is shown must match it.
        Assert.Equal(shaC, vm.SelectedCommit?.Row.CommitId);
        Assert.Equal(shaC, vm.CommitInfo?.Sha);

        // Clicking another commit afterwards switches the details and diff to it.
        CommitRowViewModel rowB = vm.LogRows.First(r => r.Row.CommitId == shaB);
        vm.SetContextCommit(rowB);
        await WaitForAsync(() => vm.CommitInfo?.Sha == shaB);
        await WaitForAsync(() => vm.CommitDiff.Title.Contains(rowB.Row.ShortId, StringComparison.Ordinal));
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

/// <summary>Forwards to a real handler but can hold GetLogPageAsync (history loads) until released.</summary>
public class GatedLogHandler : DispatchProxy
{
    private IGitEngine _inner = null!;
    private LogGate? _gate;

    public sealed class LogGate
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Entered;
        public Task WaitAsync() => _release.Task;
        public void Release() => _release.TrySetResult();
    }

    public static IGitEngine Wrap(IGitEngine inner)
    {
        IGitEngine proxy = Create<IGitEngine, GatedLogHandler>();
        ((GatedLogHandler)(object)proxy)._inner = inner;
        return proxy;
    }

    /// <summary>Holds the next GetLogPageAsync call until <see cref="LogGate.Release"/>.</summary>
    public static LogGate Gate(IGitEngine proxy)
    {
        var gate = new LogGate();
        ((GatedLogHandler)(object)proxy)._gate = gate;
        return gate;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IGitEngine.GetLogPageAsync) && _gate is { } gate)
        {
            _gate = null;
            return GatedLogAsync(gate, (string)args![0]!, (HistoryFilter)args[1]!, (CancellationToken)args[2]!);
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private async Task<LogPage> GatedLogAsync(LogGate gate, string path, HistoryFilter query, CancellationToken ct)
    {
        gate.Entered = true;
        await gate.WaitAsync();
        return await _inner.GetLogPageAsync(path, query, ct);
    }
}
