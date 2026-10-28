namespace Avgit.Git;

/// <summary>Why a push was refused, as far as it changes what the user can do next.</summary>
public enum PushRejection
{
    /// <summary>The push succeeded (or nothing was rejected).</summary>
    None,

    /// <summary>The remote has commits this repository has not fetched (<c>fetch first</c>).</summary>
    FetchFirst,

    /// <summary>The branch is behind or has diverged from commits already fetched (<c>non-fast-forward</c>).</summary>
    NonFastForward,

    /// <summary>A force push with lease was refused because the remote moved since the last fetch (<c>stale info</c>).</summary>
    StaleLease,

    /// <summary>The server refused the update (hook, protected branch).</summary>
    RemoteRejected,

    /// <summary>The branch has no upstream and none was given.</summary>
    NoUpstream,

    /// <summary>Credentials or SSH keys were missing or wrong.</summary>
    Authentication,

    /// <summary>Any other failure; the raw output explains it.</summary>
    Other
}

/// <summary>One line of <c>git push --porcelain</c> output: <c>flag TAB src:dst TAB summary</c>.</summary>
public sealed record PushRefResult(char Flag, string Source, string Destination, string Summary, string? Reason)
{
    public bool Rejected => Flag == '!';
}

/// <summary>
/// Classifies the result of <c>git push --porcelain</c> so the UI can offer the
/// right follow-up (pull, force with lease, fetch) instead of raw git text.
/// </summary>
public sealed record PushOutcome(PushRejection Rejection, IReadOnlyList<PushRefResult> Refs, string? RemoteMessage, string RawOutput)
{
    public PushRefResult? RejectedRef => Refs.FirstOrDefault(r => r.Rejected);

    public static PushOutcome Parse(ProcessResult result)
    {
        var refs = new List<PushRefResult>();
        foreach (var raw in result.Output.Split('\n'))
        {
            // flag \t src:dst \t summary   e.g. "!\trefs/heads/main:refs/heads/main\t[rejected] (fetch first)"
            var parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length < 3 || parts[0].Length != 1)
            {
                continue;
            }

            var colon = parts[1].IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var summary = parts[2].Trim();
            string? reason = null;
            var open = summary.LastIndexOf('(');
            if (open >= 0 && summary.EndsWith(')'))
            {
                reason = summary[(open + 1)..^1].Trim();
            }

            refs.Add(new PushRefResult(parts[0][0], parts[1][..colon], parts[1][(colon + 1)..], summary, reason));
        }

        // Messages from the server's hooks arrive on stderr as "remote: ..." lines.
        var remoteLines = result.Error.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("remote:", StringComparison.Ordinal))
            .Select(line => line["remote:".Length..].Trim())
            .Where(line => line.Length > 0)
            .ToList();
        var remoteMessage = remoteLines.Count > 0 ? string.Join('\n', remoteLines) : null;

        return new PushOutcome(Classify(result, refs), refs, remoteMessage, result.Combined);
    }

    private static PushRejection Classify(ProcessResult result, IReadOnlyList<PushRefResult> refs)
    {
        if (result.Success)
        {
            return PushRejection.None;
        }

        PushRefResult? rejected = refs.FirstOrDefault(r => r.Rejected);
        if (rejected is not null)
        {
            if (rejected.Summary.StartsWith("[remote rejected]", StringComparison.Ordinal))
            {
                return PushRejection.RemoteRejected;
            }

            return rejected.Reason switch
            {
                "fetch first" => PushRejection.FetchFirst,
                "non-fast-forward" => PushRejection.NonFastForward,
                "stale info" => PushRejection.StaleLease,
                _ => PushRejection.Other
            };
        }

        var error = result.Error;
        if (Contains(error, "has no upstream branch") || Contains(error, "--set-upstream"))
        {
            return PushRejection.NoUpstream;
        }

        if (Contains(error, "Authentication failed")
            || Contains(error, "could not read Username")
            || Contains(error, "could not read Password")
            || Contains(error, "terminal prompts disabled")
            || Contains(error, "Permission denied (publickey")
            || Contains(error, "Host key verification failed")
            || Contains(error, "The requested URL returned error: 401")
            || Contains(error, "The requested URL returned error: 403"))
        {
            return PushRejection.Authentication;
        }

        return PushRejection.Other;
    }

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
