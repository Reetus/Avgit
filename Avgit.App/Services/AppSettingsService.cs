using System.Text.Json;
using System.Text.Json.Serialization;

namespace Avgit.App.Services;

public sealed class AppSettings
{
    public string GitExecutable { get; set; } = "git";
    public string GitSshCommand { get; set; } = string.Empty;
    public string GitCredentialHelper { get; set; } = string.Empty;
    public string ExternalDiffCommand { get; set; } = string.Empty;
    public string ExternalMergeCommand { get; set; } = string.Empty;
    /// <summary>Shell for the Terminal button; empty uses PowerShell on Windows and the login shell elsewhere.</summary>
    public string TerminalShell { get; set; } = string.Empty;
    public List<CustomActionSetting> CustomActions { get; set; } = [];
    public List<string> RecentRepositories { get; set; } = [];
    public List<string> OpenRepositories { get; set; } = [];
    /// <summary>Tab that was active when the app closed; re-selected on restore.</summary>
    public string? ActiveRepository { get; set; }
    /// <summary>List each file inside untracked folders (git status -uall) instead of one entry per folder.</summary>
    public bool ShowFilesInUntrackedFolders { get; set; } = true;
    /// <summary>Write a commit-graph for repositories without one, which makes history load much faster.</summary>
    public bool MaintainCommitGraph { get; set; } = true;
    public int DiffContext { get; set; } = 3;
    public bool IgnoreWhitespace { get; set; }
    public bool AutoRefresh { get; set; } = true;
    public bool CommitSignOff { get; set; }
    public bool CommitGpgSign { get; set; }
    public bool ShowAllBranches { get; set; } = true;
}

public sealed class CustomActionSetting
{
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Command : Name;
}

/// <summary>Source-generated serializer metadata; reflection-based JSON is unavailable under NativeAOT/trimming.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}

public sealed class AppSettingsService
{
    /// <summary>Repository paths are case-insensitive on Windows and (by default) macOS only.</summary>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer PathComparer { get; } =
        PathComparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public AppSettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Avgit",
            "settings.json");
        Settings = Load();
    }

    public string SettingsPath { get; }
    public AppSettings Settings { get; private set; }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                AppSettings? loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt settings should never prevent the app from starting.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            // Write then move so a crash mid-write cannot truncate the settings file
            // (Load would then silently fall back to defaults and lose recents).
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Settings, AppSettingsJsonContext.Default.AppSettings));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch
        {
            // Ignore settings write failures (read-only profile, etc.).
        }
    }

    public void AddRecent(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Settings.RecentRepositories.RemoveAll(p => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)),
            path,
            PathComparison));
        Settings.RecentRepositories.Insert(0, path);
        if (Settings.RecentRepositories.Count > 12)
        {
            Settings.RecentRepositories.RemoveRange(12, Settings.RecentRepositories.Count - 12);
        }

        Save();
    }

    public void RemoveRecent(string path)
    {
        Settings.RecentRepositories.RemoveAll(p => string.Equals(p, path, PathComparison));
        Save();
    }
}

