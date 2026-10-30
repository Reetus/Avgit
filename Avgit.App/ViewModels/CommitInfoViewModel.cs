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
    public string ParentsText => Parents.Length == 0 ? Strings.CommitInfo_RootCommit : string.Join(", ", Parents.Select(p => p.Length > 8 ? p[..8] : p));
    public string TagsText => Tags.Length == 0 ? string.Empty : string.Join("  ", Tags.Select(t => string.Format(Strings.CommitInfo_Tag, t)));
    public bool HasMoreMessage => Message.Trim() != Title.Trim() && Message.Contains('\n');
}

public sealed class StashEntry(StashRecord stash)
{
    /// <summary><c>stash@{n}</c>, used by apply, pop and drop; it shifts as stashes come and go.</summary>
    public string Name { get; } = stash.Id;
    public string Date { get; } = stash.Created;
    public string Description { get; } = stash.Text;

    /// <summary>The stash commit, which identifies the stash across refreshes.</summary>
    public string Sha { get; } = stash.Sha;
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
    public string BaseShortSha { get; } = stash.BaseShortSha;
    public string Display => string.IsNullOrEmpty(Description) ? Name : $"{Name}  {Description}";

    // git describes a stash as "On <branch>: <message>", or "WIP on <branch>: <sha> <subject>"
    // when it was made without a message.
    private int BranchStart => Description.StartsWith("WIP on ", StringComparison.Ordinal) ? 7
        : Description.StartsWith("On ", StringComparison.Ordinal) ? 3 : -1;

    private int BranchEnd => BranchStart < 0 ? -1 : Description.IndexOf(": ", BranchStart, StringComparison.Ordinal);

    /// <summary>The stash message without git's "On &lt;branch&gt;:" prefix.</summary>
    public string Title => BranchEnd < 0 || Description.StartsWith("WIP ", StringComparison.Ordinal)
        ? (Description.Length > 0 ? Description : Name)
        : Description[(BranchEnd + 2)..];

    /// <summary>"On &lt;branch&gt; at &lt;sha&gt;": where the stash was made.</summary>
    public string BaseText => BranchEnd < 0
        ? string.Format(Strings.StashInfo_At, BaseShortSha)
        : string.Format(Strings.StashInfo_OnBranchAt, Description[BranchStart..BranchEnd], BaseShortSha);
}
