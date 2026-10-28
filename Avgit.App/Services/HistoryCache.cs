using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avgit.Core.Model;

namespace Avgit.App.Services;

/// <summary>
/// Stores the first page of each repository's history on disk so a tab can show
/// its last-known history immediately while git produces the current one. Only
/// display data is cached; every action still runs against the live repository.
/// </summary>
public sealed class HistoryCache(string directory)
{
    private const int FormatVersion = 2;
    private readonly string _directory = directory;

    public static HistoryCache ForSettings(AppSettingsService settings)
        => new(Path.Combine(Path.GetDirectoryName(settings.SettingsPath) ?? Path.GetTempPath(), "cache"));

    public IReadOnlyList<HistoryRow>? TryLoad(string repositoryPath)
    {
        try
        {
            var file = FileFor(repositoryPath);
            if (!File.Exists(file))
            {
                return null;
            }

            CachedHistory? cached = JsonSerializer.Deserialize(File.ReadAllText(file), HistoryCacheJsonContext.Default.CachedHistory);
            if (cached is null || cached.Version != FormatVersion
                || !string.Equals(cached.RepositoryPath, repositoryPath, StringComparison.Ordinal))
            {
                return null;
            }

            return [.. cached.Rows.Select(row => new HistoryRow
            {
                Id = row.Sha,
                CommitId = row.Sha,
                ShortId = row.ShortSha,
                ParentIds = row.Parents,
                Timestamp = new DateTime(row.DateTicks, DateTimeKind.Local),
                Author = row.Author,
                Committer = row.Committer,
                BranchName = row.Branch,
                Title = row.Message,
                Refs = [.. row.Labels.Select(l => new RefBadge(l.Label, l.Type, string.Empty, string.Empty)
                {
                    IsCheckedOut = l.Current
                })]
            })];
        }
        catch
        {
            // A corrupt or unreadable cache is ignored; the live load follows anyway.
            return null;
        }
    }

    public void Save(string repositoryPath, IEnumerable<HistoryRow> rows, int maxRows = 500)
    {
        var snapshot = new CachedHistory
        {
            Version = FormatVersion,
            RepositoryPath = repositoryPath,
            Rows = [.. rows.Take(maxRows).Select(row => new CachedRow
            {
                Sha = row.CommitId,
                ShortSha = row.ShortId,
                Parents = row.ParentIds,
                DateTicks = row.Timestamp.Ticks,
                Author = row.Author,
                Committer = row.Committer,
                Branch = row.BranchName,
                Message = row.Title,
                Labels = [.. row.Refs.Select(l => new CachedLabel { Label = l.Name, Type = l.Kind, Current = l.IsCheckedOut })]
            })]
        };

        // Serialize on the caller's thread (rows may change later), write in the background.
        var json = JsonSerializer.Serialize(snapshot, HistoryCacheJsonContext.Default.CachedHistory);
        var file = FileFor(repositoryPath);
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var temp = file + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, file, overwrite: true);
            }
            catch
            {
                // Caching is best effort.
            }
        });
    }

    private string FileFor(string repositoryPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(repositoryPath));
        return Path.Combine(_directory, Convert.ToHexString(hash, 0, 12).ToLowerInvariant() + ".json");
    }
}

public sealed class CachedHistory
{
    public int Version { get; set; }
    public string RepositoryPath { get; set; } = string.Empty;
    public List<CachedRow> Rows { get; set; } = [];
}

public sealed class CachedRow
{
    public string Sha { get; set; } = string.Empty;
    public string ShortSha { get; set; } = string.Empty;
    public string[] Parents { get; set; } = [];
    public long DateTicks { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Committer { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<CachedLabel> Labels { get; set; } = [];
}

public sealed class CachedLabel
{
    public string Label { get; set; } = string.Empty;
    public RefKind Type { get; set; }
    public bool Current { get; set; }
}

[JsonSerializable(typeof(CachedHistory))]
internal sealed partial class HistoryCacheJsonContext : JsonSerializerContext
{
}
