using System.Diagnostics;
using System.Text;

namespace Avgit.Git;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;

    /// <summary>The caller cancelled the run (not a timeout); not an error to report.</summary>
    public bool Cancelled { get; init; }

    public string Combined => string.IsNullOrWhiteSpace(Error) ? Output : $"{Output}\n{Error}".Trim();

    public override string ToString() => $"exit={ExitCode}\n{Combined}";
}

/// <summary>
/// Runs the git command line tool asynchronously without a shell, using explicit
/// argument lists. Shell commands are run separately with platform-correct
/// quoting so user-supplied commands may contain quotes and spaces.
/// </summary>
public static class ProcessRunner
{
    private static long _startedCount;

    /// <summary>Number of processes started so far (diagnostics and tests).</summary>
    public static long StartedCount => Interlocked.Read(ref _startedCount);

    /// <summary>
    /// Raised after every process exits with its command line (executable plus
    /// arguments, space-joined) and wall-clock duration. Used for timing logs.
    /// </summary>
    public static event Action<string, TimeSpan, int>? Completed;

    public static Task<ProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IEnumerable<string> arguments,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo psi = CreateStartInfo(executable, workingDirectory, environment);
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.RedirectStandardInput = standardInput is not null;
        return ExecuteAsync(psi, standardInput, cancellationToken);
    }

    /// <summary>
    /// Runs a user-supplied command through the platform shell. On Windows the
    /// command is passed as a raw <c>cmd.exe /d /s /c "..."</c> argument because
    /// ArgumentList would escape embedded quotes and break paths with spaces.
    /// </summary>
    public static Task<ProcessResult> RunShellAsync(
        string workingDirectory,
        string command,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return RunAsync("/bin/sh", workingDirectory, ["-c", command], null, environment, cancellationToken);
        }

        var shell = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = "cmd.exe";
        }

        ProcessStartInfo psi = CreateStartInfo(shell, workingDirectory, environment);
        psi.Arguments = "/d /s /c \"" + command + "\"";
        return ExecuteAsync(psi, null, cancellationToken);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        // Keep tools non-interactive. Without this a fetch/pull can hang forever
        // waiting for credentials on a detached desktop process.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_EDITOR"] = "true";
        psi.Environment["GIT_SEQUENCE_EDITOR"] = "true";
        psi.Environment["GIT_MERGE_AUTOEDIT"] = "no";
        psi.Environment["LANG"] = "en_US.UTF-8";
        // Some failures are recognised by their English text. LANGUAGE takes
        // precedence over LC_ALL/LC_MESSAGES for gettext message catalogues, so
        // this keeps git output English even when the user's locale is not.
        psi.Environment["LANGUAGE"] = "en";

        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> pair in environment)
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        return psi;
    }

    private static async Task<ProcessResult> ExecuteAsync(
        ProcessStartInfo psi,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = psi };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        CancellationToken token = timeout.Token;
        var stopwatch = Stopwatch.StartNew();
        var exitCode = -1;

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, $"Could not start {psi.FileName}.");
            }

            Interlocked.Increment(ref _startedCount);

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(token);

            if (standardInput is not null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), token);
                    await process.StandardInput.FlushAsync(token);
                }
                catch (IOException)
                {
                    // The process exited (or closed stdin) before reading all
                    // input; its exit code and stderr describe the failure.
                }
                finally
                {
                    try { process.StandardInput.Close(); } catch (IOException) { }
                }
            }

            await process.WaitForExitAsync(token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            exitCode = process.ExitCode;
            return new ProcessResult(exitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new ProcessResult(-1, string.Empty, $"{Path.GetFileName(psi.FileName)} was cancelled or timed out.")
            {
                Cancelled = cancellationToken.IsCancellationRequested
            };
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new ProcessResult(-1, string.Empty, ex.Message);
        }
        finally
        {
            if (Completed is { } completed)
            {
                var commandLine = psi.ArgumentList.Count > 0
                    ? Path.GetFileName(psi.FileName) + " " + string.Join(' ', psi.ArgumentList)
                    : Path.GetFileName(psi.FileName) + " " + psi.Arguments;
                try { completed(commandLine, stopwatch.Elapsed, exitCode); } catch { /* diagnostics only */ }
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may never have started or may already have exited.
        }
    }
}
