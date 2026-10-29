using System.Diagnostics;

namespace Avgit.App.Services;

public enum TerminalPlatform
{
    Windows,
    MacOS,
    Linux
}

/// <summary>
/// Opens an interactive shell in a repository folder: PowerShell on Windows
/// (PowerShell 7 when installed), the user's login shell in Terminal.app on
/// macOS (zsh by default), and the user's login shell (falling back to bash)
/// inside a terminal emulator on Linux. A configured shell overrides the default.
/// </summary>
public static class TerminalLauncher
{
    /// <summary>Linux terminal emulators, in preference order, with how each takes a directory and a command.</summary>
    private static readonly (string Name, Func<string, string, string[]> Args)[] LinuxTerminals =
    [
        ("x-terminal-emulator", (_, shell) => [ "-e", shell ]),
        ("gnome-terminal", (dir, shell) => [ "--working-directory=" + dir, "--", shell ]),
        ("konsole", (dir, shell) => [ "--workdir", dir, "-e", shell ]),
        ("xfce4-terminal", (dir, shell) => [ "--working-directory=" + dir, "-x", shell ]),
        ("kitty", (dir, shell) => [ "--directory", dir, shell ]),
        ("alacritty", (dir, shell) => [ "--working-directory", dir, "-e", shell ]),
        ("wezterm", (dir, shell) => [ "start", "--cwd", dir, "--", shell ]),
        ("foot", (dir, shell) => [ "--working-directory=" + dir, shell ]),
        ("xterm", (_, shell) => [ "-e", shell ])
    ];

    public static TerminalPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? TerminalPlatform.Windows
        : OperatingSystem.IsMacOS() ? TerminalPlatform.MacOS
        : TerminalPlatform.Linux;

    /// <summary>Starts the terminal; returns an error message, or null on success.</summary>
    public static string? Launch(string directory, string? shellOverride)
    {
        ProcessStartInfo? psi = BuildStartInfo(CurrentPlatform, directory, shellOverride,
            FindOnPath, Environment.GetEnvironmentVariable);
        if (psi is null)
        {
            return Strings.Terminal_NoEmulatorFound;
        }

        try
        {
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return string.Format(Strings.Terminal_CouldNotStart, psi.FileName, ex.Message);
        }
    }

    internal static ProcessStartInfo? BuildStartInfo(TerminalPlatform platform, string directory, string? shellOverride,
        Func<string, string?> findOnPath, Func<string, string?> getEnvironment)
    {
        var shell = string.IsNullOrWhiteSpace(shellOverride) ? null : shellOverride.Trim();

        switch (platform)
        {
            case TerminalPlatform.Windows:
                {
                    // A console program started through the shell gets its own window,
                    // hosted by the user's default terminal (Windows Terminal on Windows 11).
                    var executable = shell ?? findOnPath("pwsh.exe") ?? "powershell.exe";
                    var psi = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = true };
                    if (IsPowerShell(executable))
                    {
                        psi.ArgumentList.Add("-NoLogo");
                    }

                    return psi;
                }

            case TerminalPlatform.MacOS:
                {
                    if (shell is null)
                    {
                        // Terminal.app opens a window running the login shell (zsh by default).
                        var open = new ProcessStartInfo("open") { UseShellExecute = false };
                        open.ArgumentList.Add("-a");
                        open.ArgumentList.Add("Terminal");
                        open.ArgumentList.Add(directory);
                        return open;
                    }

                    // `open` cannot pass a command to Terminal.app; AppleScript can. The
                    // shell setting is a command line, so it is deliberately not quoted.
                    var command = $"cd {ShellQuote(directory)} && exec {shell}";
                    var script = new ProcessStartInfo("osascript") { UseShellExecute = false };
                    foreach (var line in new[]
                             {
                             "tell application \"Terminal\"",
                             "activate",
                             $"do script \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
                             "end tell"
                         })
                    {
                        script.ArgumentList.Add("-e");
                        script.ArgumentList.Add(line);
                    }
                    return script;
                }

            default:
                {
                    var loginShell = shell ?? getEnvironment("SHELL");
                    if (string.IsNullOrWhiteSpace(loginShell))
                    {
                        loginShell = findOnPath("bash") ?? "/bin/sh";
                    }

                    // $TERMINAL is the user's explicit choice; most emulators accept -e.
                    var preferred = getEnvironment("TERMINAL");
                    if (!string.IsNullOrWhiteSpace(preferred) && findOnPath(preferred.Trim()) is { } preferredPath)
                    {
                        (var Name, Func<string, string, string[]> Args) = LinuxTerminals.FirstOrDefault(t =>
                            string.Equals(t.Name, Path.GetFileName(preferredPath), StringComparison.Ordinal));
                        var args = Args?.Invoke(directory, loginShell) ?? ["-e", loginShell];
                        return LinuxStartInfo(preferredPath, directory, args);
                    }

                    foreach ((var name, Func<string, string, string[]>? buildArgs) in LinuxTerminals)
                    {
                        if (findOnPath(name) is { } path)
                        {
                            return LinuxStartInfo(path, directory, buildArgs(directory, loginShell));
                        }
                    }

                    return null;
                }
        }
    }

    private static ProcessStartInfo LinuxStartInfo(string executable, string directory, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static bool IsPowerShell(string executable)
    {
        // Split on both separators: Path.GetFileName ignores '\' on non-Windows hosts.
        var name = executable[(executable.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
               || name.Equals("powershell", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindOnPath(string name)
    {
        if (Path.IsPathRooted(name))
        {
            return File.Exists(name) ? name : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
