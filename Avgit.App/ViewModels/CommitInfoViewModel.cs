using Avgit.Core.Model;

namespace Avgit.App.ViewModels;

public sealed class CommitInfoViewModel(CommitRecord details)
{
    public string Sha { get; } = details.Sha ?? string.Empty;
    public string ShortSha { get; } = details.ShortSha ?? string.Empty;
    public string Author { get; } = details.Author ?? string.Empty;
    public string Committer { get; } = details.Committer ?? string.Empty;
    public DateTime AuthorDate { get; } = details.AuthorDate;
    public DateTime CommitDate { get; } = details.CommitDate;
    public string Message { get; } = details.Message ?? string.Empty;
    public string Title { get; } = details.Title ?? string.Empty;
    public string[] Parents { get; } = details.ParentIds ?? [];
    public string[] Tags { get; } = details.Tags ?? [];
    public string AuthorDateText => AuthorDate.ToString("yyyy-MM-dd HH:mm:ss");
    public string ParentsText => Parents.Length == 0 ? "(root commit)" : string.Join(", ", Parents.Select(p => p.Length > 8 ? p[..8] : p));
    public string TagsText => Tags.Length == 0 ? string.Empty : string.Join("  ", Tags.Select(t => "tag: " + t));
    public bool HasMoreMessage => Message.Trim() != Title.Trim() && Message.Contains('\n');
}

public sealed class StashEntry(StashRecord stash)
{
    public string Name { get; } = stash.Id;
    public string Date { get; } = stash.Created;
    public string Description { get; } = stash.Text;
    public string Display => string.IsNullOrEmpty(Description) ? Name : $"{Name}  {Description}";
}
