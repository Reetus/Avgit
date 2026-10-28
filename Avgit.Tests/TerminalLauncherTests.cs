using System.Diagnostics;
using Avgit.App.Services;

namespace Avgit.Tests;

public class TerminalLauncherTests
{
    private const string Repo = "/work/repo";

    private static Func<string, string?> OnPath(params string[] names)
        => name => names.Contains(name) ? "/usr/bin/" + name : null;

    private static Func<string, string?> Env(string? shell = null, string? terminal = null)
        => name => name switch { "SHELL" => shell, "TERMINAL" => terminal, _ => null };

    [Fact]
    public void Windows_prefers_PowerShell_7_then_Windows_PowerShell()
    {
        ProcessStartInfo pwsh = TerminalLauncher.BuildStartInfo(TerminalPlatform.Windows, @"C:\repo", null,
            name => name == "pwsh.exe" ? @"C:\Program Files\PowerShell\7\pwsh.exe" : null, Env())!;
        Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", pwsh.FileName);
        Assert.Equal(@"C:\repo", pwsh.WorkingDirectory);
        Assert.True(pwsh.UseShellExecute);
        Assert.Equal(["-NoLogo"], pwsh.ArgumentList);

        ProcessStartInfo legacy = TerminalLauncher.BuildStartInfo(TerminalPlatform.Windows, @"C:\repo", null, OnPath(), Env())!;
        Assert.Equal("powershell.exe", legacy.FileName);

        ProcessStartInfo custom = TerminalLauncher.BuildStartInfo(TerminalPlatform.Windows, @"C:\repo", "cmd.exe", OnPath(), Env())!;
        Assert.Equal("cmd.exe", custom.FileName);
        Assert.Empty(custom.ArgumentList);
    }

    [Fact]
    public void Linux_runs_the_login_shell_in_a_terminal_emulator()
    {
        ProcessStartInfo gnome = TerminalLauncher.BuildStartInfo(TerminalPlatform.Linux, Repo, null,
            OnPath("gnome-terminal", "xterm"), Env(shell: "/usr/bin/fish"))!;
        Assert.Equal("/usr/bin/gnome-terminal", gnome.FileName);
        Assert.Equal(["--working-directory=" + Repo, "--", "/usr/bin/fish"], gnome.ArgumentList);
        Assert.Equal(Repo, gnome.WorkingDirectory);

        // Without $SHELL the default is bash.
        ProcessStartInfo xterm = TerminalLauncher.BuildStartInfo(TerminalPlatform.Linux, Repo, null,
            OnPath("xterm", "bash"), Env())!;
        Assert.Equal(["-e", "/usr/bin/bash"], xterm.ArgumentList);

        // $TERMINAL wins over the built-in list; the configured shell wins over $SHELL.
        ProcessStartInfo preferred = TerminalLauncher.BuildStartInfo(TerminalPlatform.Linux, Repo, "/bin/zsh",
            OnPath("gnome-terminal", "konsole"), Env(shell: "/bin/bash", terminal: "konsole"))!;
        Assert.Equal("/usr/bin/konsole", preferred.FileName);
        Assert.Equal(["--workdir", Repo, "-e", "/bin/zsh"], preferred.ArgumentList);

        Assert.Null(TerminalLauncher.BuildStartInfo(TerminalPlatform.Linux, Repo, null, OnPath(), Env()));
    }

    [Fact]
    public void MacOS_opens_Terminal_app_in_the_repository()
    {
        ProcessStartInfo open = TerminalLauncher.BuildStartInfo(TerminalPlatform.MacOS, Repo, null, OnPath(), Env())!;
        Assert.Equal("open", open.FileName);
        Assert.Equal(["-a", "Terminal", Repo], open.ArgumentList);

        ProcessStartInfo custom = TerminalLauncher.BuildStartInfo(TerminalPlatform.MacOS, "/work/it's here", "/opt/homebrew/bin/fish",
            OnPath(), Env())!;
        Assert.Equal("osascript", custom.FileName);
        Assert.Contains(custom.ArgumentList,
            a => a == "do script \"cd '/work/it'\\\\''s here' && exec /opt/homebrew/bin/fish\"");
    }
}
