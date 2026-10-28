using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>
/// Opt-in timings against a real (ideally large) repository:
///   AVGIT_PERF_REPO=/path/to/repo dotnet test --filter PerfBenchmarks
/// Results are appended to AVGIT_PERF_OUT (default: %TEMP%/avgit-perf.txt).
/// </summary>
public class PerfBenchmarks
{
    private static readonly string? RepoPath = Environment.GetEnvironmentVariable("AVGIT_PERF_REPO");

    [AvaloniaFact]
    public async Task Measure_open_click_load_more_and_refresh()
    {
        if (string.IsNullOrWhiteSpace(RepoPath) || !Directory.Exists(RepoPath))
        {
            return;
        }

        var report = new StringBuilder();
        var commands = new ConcurrentQueue<(string Command, TimeSpan Elapsed)>();
        void OnCompleted(string command, TimeSpan elapsed, int _)
        {
            commands.Enqueue((command, elapsed));
        }

        ProcessRunner.Completed += OnCompleted;
        try
        {
            var label = Environment.GetEnvironmentVariable("AVGIT_PERF_LABEL") ?? "run";
            report.AppendLine($"== {label}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {RepoPath}");

            var settingsPath = Path.Combine(Path.GetTempPath(), "avgit-perf-settings-" + Guid.NewGuid().ToString("N") + ".json");
            var settings = new AppSettingsService(settingsPath);
            settings.Settings.AutoRefresh = false;
            var vm = new MainWindowViewModel(settings, new FakeDialogService());

            // Open: time until history rows are on screen, and until everything is loaded.
            double firstRowsMs = 0;
            (var openMs, var openProcs) = await MeasureAsync(commands, async () =>
            {
                var stopwatch = Stopwatch.StartNew();
                Task open = vm.OpenPathAsync(RepoPath);
                await WaitForAsync(() => vm.Repository is { LogRows.Count: > 0 });
                firstRowsMs = stopwatch.Elapsed.TotalMilliseconds;
                await open;
                await WaitForAsync(() => vm.Repository is { IsBusy: false } r && r.LogRows.Count > 0);
            });
            RepositoryViewModel repo = vm.Repository!;
            report.AppendLine($"open: first rows visible      {firstRowsMs,8:N0} ms");
            report.AppendLine($"open (history+status loaded)  {openMs,8:N0} ms  {openProcs,3} git processes  {repo.LogRows.Count} rows");

            // Commit clicks: details, file list and first file diff.
            var clickTimes = new List<double>();
            var clickProcs = new List<long>();
            foreach (var index in (int[])[3, 25, 120, 260, 480])
            {
                if (index >= repo.LogRows.Count)
                {
                    break;
                }

                CommitRowViewModel row = repo.LogRows[index];
                (var ms, var procs) = await MeasureAsync(commands, async () =>
                {
                    repo.SetContextCommit(row);
                    await WaitForAsync(() => repo.CommitInfo?.Sha == row.Row.CommitId
                                             && (repo.CommitFiles.Count == 0 || repo.CommitDiff.Title.Contains(row.Row.ShortId)));
                });
                clickTimes.Add(ms);
                clickProcs.Add(procs);
            }
            report.AppendLine($"commit click (avg of {clickTimes.Count})      {clickTimes.Average(),8:N0} ms  {clickProcs.Average(),3:N1} git processes");

            // Load more: next page of history.
            var before = repo.LogRows.Count;
            (var moreMs, var moreProcs) = await MeasureAsync(commands, async () =>
            {
                repo.LoadMoreLogCommand.Execute(null);
                await WaitForAsync(() => repo.LogRows.Count > before && !repo.IsBusy);
            });
            report.AppendLine($"load more                     {moreMs,8:N0} ms  {moreProcs,3} git processes  {before} -> {repo.LogRows.Count} rows");

            // Full refresh (toolbar Refresh).
            (var refreshMs, var refreshProcs) = await MeasureAsync(commands, repo.RefreshAsync);
            report.AppendLine($"refresh                       {refreshMs,8:N0} ms  {refreshProcs,3} git processes");

            report.AppendLine("slowest git commands:");
            foreach ((var command, TimeSpan elapsed) in commands.OrderByDescending(c => c.Elapsed).Take(8))
            {
                report.AppendLine($"  {elapsed.TotalMilliseconds,7:N0} ms  {Shorten(command)}");
            }

            vm.Repositories.ToList().ForEach(r => r.Dispose());

            // Reopen with the same settings, as after an app restart: history comes
            // from the on-disk cache first, then the live load replaces it.
            var reopened = new MainWindowViewModel(settings, new FakeDialogService());
            var reopenWatch = Stopwatch.StartNew();
            Task reopen = reopened.OpenPathAsync(RepoPath);
            await WaitForAsync(() => reopened.Repository is { LogRows.Count: > 0 });
            var cachedRowsMs = reopenWatch.Elapsed.TotalMilliseconds;
            var fromCache = reopened.Repository!.IsShowingCachedHistory;
            await reopen;
            report.AppendLine($"reopen: first rows visible    {cachedRowsMs,8:N0} ms  (from cache: {fromCache}); fully loaded {reopenWatch.Elapsed.TotalMilliseconds:N0} ms");
            reopened.Repositories.ToList().ForEach(r => r.Dispose());

            try { File.Delete(settingsPath); } catch { }
        }
        finally
        {
            ProcessRunner.Completed -= OnCompleted;
        }

        var output = Environment.GetEnvironmentVariable("AVGIT_PERF_OUT")
                     ?? Path.Combine(Path.GetTempPath(), "avgit-perf.txt");
        await File.AppendAllTextAsync(output, report + Environment.NewLine);
    }

    private static async Task<(double Ms, long Processes)> MeasureAsync(
        ConcurrentQueue<(string, TimeSpan)> commands, Func<Task> action)
    {
        var processes = ProcessRunner.StartedCount;
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        return (stopwatch.Elapsed.TotalMilliseconds, ProcessRunner.StartedCount - processes);
    }

    private static string Shorten(string command)
    {
        // Drop the fixed -c/--no-pager prefix so the interesting part shows.
        var text = command.Replace("-c core.quotepath=false -c color.ui=false --no-pager ", string.Empty);
        return text.Length > 140 ? text[..140] + "..." : text;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 300000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
