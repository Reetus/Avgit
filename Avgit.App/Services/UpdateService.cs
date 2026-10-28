using Velopack;
using Velopack.Sources;

namespace Avgit.App.Services;

/// <summary>
/// Checks GitHub Releases for a newer Avgit and downloads it in the background.
/// A downloaded update is applied on restart, either straight away through
/// <see cref="ApplyAndRestart"/> or automatically the next time Avgit starts.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/Reetus/Avgit";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, null, false));
    private UpdateInfo? _pending;

    /// <summary>
    /// False when Avgit runs from a build or publish folder rather than from a
    /// Velopack install (Setup.exe or the AppImage); updates are unavailable then.
    /// </summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    /// <summary>Version that has been downloaded and is waiting for a restart, if any.</summary>
    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    /// <summary>
    /// Downloads the latest release when it is newer than this one. Returns its
    /// version, or null when Avgit is up to date or not installed.
    /// </summary>
    public async Task<string?> DownloadLatestAsync(CancellationToken cancellationToken = default)
    {
        if (!_manager.IsInstalled)
        {
            return null;
        }

        if (_pending is not null)
        {
            return PendingVersion;
        }

        UpdateInfo? update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            return null;
        }

        await _manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken).ConfigureAwait(false);
        _pending = update;
        return PendingVersion;
    }

    /// <summary>Exits, installs the downloaded update and starts the new version.</summary>
    public void ApplyAndRestart()
    {
        if (_pending is not null)
        {
            _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
        }
    }
}
