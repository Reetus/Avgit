using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.App.Views;
using Avgit.Git;

namespace Avgit.Tests;

/// <summary>
/// Builds a fictional repository with several authors, branches, merges, tags, a remote,
/// a worktree, a stash and pending changes, and captures the README screenshots from it.
/// Runs only when AVGIT_SCREENSHOTS names the output folder; AVGIT_DEMO_DIR picks where the
/// demo repository is created (its path is shown in the app, so keep it short and neutral).
/// </summary>
public class ReadmeScreenshots
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 9, 12, 0, TimeSpan.FromHours(2));

    private static readonly (string Name, string Email)[] Authors =
    [
        ("Maya Chen", "maya@example.com"),
        ("Liam Okafor", "liam@example.com"),
        ("Priya Natarajan", "priya@example.com"),
        ("Jonas Weber", "jonas@example.com"),
        ("Sofia Alvarez", "sofia@example.com")
    ];

    private const int Maya = 0, Liam = 1, Priya = 2, Jonas = 3, Sofia = 4;

    [AvaloniaFact]
    [Trait("Category", "Screenshots")]
    public async Task Capture_readme_screenshots()
    {
        var output = Environment.GetEnvironmentVariable("AVGIT_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output) || !await TempRepo.GitAvailableAsync())
        {
            return;
        }

        var demoRoot = Environment.GetEnvironmentVariable("AVGIT_DEMO_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "avgit-demo");
        DeleteFolder(demoRoot);
        Directory.CreateDirectory(output);
        try
        {
            var repo = Path.Combine(demoRoot, "orbit");
            await BuildDemoRepositoryAsync(demoRoot, repo);

            var settings = new AppSettingsService(Path.Combine(demoRoot, "settings.json"));
            settings.Settings.AutoRefresh = false;
            var vm = new MainWindowViewModel(settings, new FakeDialogService());
            var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
            window.Show();
            await vm.OpenPathAsync(repo);
            RepositoryViewModel repository = vm.Repository!;
            await WaitForAsync(() => repository.LogRows.Count > 20 && repository.Worktrees.Count == 2);

            // History: a commit with a readable code change.
            CommitRowViewModel commit = repository.LogRows.First(r => r.Message == "Refresh expired tokens before each request");
            repository.SetContextCommit(commit);
            await WaitForAsync(() => repository.CommitInfo?.Sha == commit.Row.CommitId
                                     && repository.CommitDiff.DisplayItems.Count > 5);
            await SettleAsync();
            Save(window, Path.Combine(output, "history.png"));

            // File Status: staged and unstaged changes with a diff.
            TabControl tabs = window.GetVisualDescendants().OfType<TabControl>()
                .First(t => t.Items.OfType<TabItem>().Any(i => i.Header as string == "File Status"));
            tabs.SelectedIndex = 1;
            repository.SelectedUnstagedFile = repository.UnstagedFiles.First(f => f.RelativePath.EndsWith("ApiClient.cs", StringComparison.Ordinal));
            await WaitForAsync(() => repository.WorkingDiff.DisplayItems.Count > 5);
            repository.CommitMessage = "Back off exponentially between retries";
            await SettleAsync();
            Save(window, Path.Combine(output, "file-status.png"));

            window.Close();
        }
        finally
        {
            DeleteFolder(demoRoot);
        }
    }

    private static async Task BuildDemoRepositoryAsync(string root, string repo)
    {
        Directory.CreateDirectory(repo);
        var step = 0;

        async Task Git(string cwd, params string[] args)
        {
            ProcessResult result = await ProcessRunner.RunAsync("git", cwd, args);
            if (!result.Success)
            {
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Combined}");
            }
        }

        // Each commit is a few hours after the previous one, so lanes interleave by date.
        async Task Commit(int author, string message, params (string Path, string Text)[] files)
        {
            foreach ((string path, string text) in files)
            {
                var full = Path.Combine(repo, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, text.ReplaceLineEndings("\n"));
            }

            await Git(repo, "add", "-A");
            var when = Start.AddHours(++step * 9 + (step % 3) * 2).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
            (string name, string email) = Authors[author];
            var env = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = name, ["GIT_AUTHOR_EMAIL"] = email, ["GIT_AUTHOR_DATE"] = when,
                ["GIT_COMMITTER_NAME"] = name, ["GIT_COMMITTER_EMAIL"] = email, ["GIT_COMMITTER_DATE"] = when
            };
            ProcessResult result = await ProcessRunner.RunAsync("git", repo, ["commit", "-q", "-m", message], null, env);
            if (!result.Success)
            {
                throw new InvalidOperationException($"commit '{message}' failed: {result.Combined}");
            }
        }

        async Task Merge(int author, string branch)
        {
            var when = Start.AddHours(++step * 9).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
            (string name, string email) = Authors[author];
            var env = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = name, ["GIT_AUTHOR_EMAIL"] = email, ["GIT_AUTHOR_DATE"] = when,
                ["GIT_COMMITTER_NAME"] = name, ["GIT_COMMITTER_EMAIL"] = email, ["GIT_COMMITTER_DATE"] = when
            };
            ProcessResult result = await ProcessRunner.RunAsync("git", repo,
                ["merge", "--no-ff", "-q", "-m", $"Merge branch '{branch}'", branch], null, env);
            if (!result.Success)
            {
                throw new InvalidOperationException($"merge {branch} failed: {result.Combined}");
            }
        }

        await Git(repo, "init", "-q", "-b", "main");
        await Git(repo, "config", "user.name", "Demo");
        await Git(repo, "config", "user.email", "demo@example.com");
        await Git(repo, "config", "commit.gpgsign", "false");
        await Git(repo, "config", "core.autocrlf", "false");

        await Commit(Maya, "Initial project scaffold",
            ("README.md", "# Orbit\n\nA tiny client for the Orbit API.\n"),
            (".gitignore", "bin/\nobj/\n"),
            ("src/Orbit/Orbit.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n"),
            ("src/Orbit/Program.cs", "var config = Config.Load(\"orbit.json\");\nConsole.WriteLine($\"Orbit {config.Endpoint}\");\n"));
        await Commit(Maya, "Load settings from orbit.json",
            ("src/Orbit/Config.cs", Code("Config", "public string Endpoint { get; init; } = \"https://api.example.com\";\n\n    public static Config Load(string path)\n        => JsonSerializer.Deserialize<Config>(File.ReadAllText(path))!;")));
        await Commit(Liam, "Build and test on every push",
            (".github/workflows/ci.yml", "name: CI\non: [push, pull_request]\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n      - run: dotnet test\n"));
        await Commit(Priya, "Add an HTTP client wrapper",
            ("src/Orbit/Net/ApiClient.cs", ApiClient(retries: false, backoff: false)));
        await Git(repo, "tag", "-a", "v0.1.0", "-m", "First preview");

        // feature/auth (Priya) alongside logging work on main.
        await Git(repo, "checkout", "-q", "-b", "feature/auth");
        await Commit(Priya, "Store access tokens in the OS keychain",
            ("src/Orbit/Auth/TokenStore.cs", TokenStore(refresh: false)));
        await Git(repo, "checkout", "-q", "main");
        await Commit(Jonas, "Add structured logging",
            ("src/Orbit/Logging/Log.cs", Code("Log", "public static void Info(string message, params object[] args)\n        => Console.WriteLine($\"[info] {string.Format(message, args)}\");")));
        await Git(repo, "checkout", "-q", "feature/auth");
        await Commit(Priya, "Refresh expired tokens before each request",
            ("src/Orbit/Auth/TokenStore.cs", TokenStore(refresh: true)));
        await Git(repo, "checkout", "-q", "main");
        await Commit(Liam, "Fix crash when orbit.json is missing",
            ("src/Orbit/Config.cs", Code("Config", "public string Endpoint { get; init; } = \"https://api.example.com\";\n\n    public static Config Load(string path)\n        => File.Exists(path)\n            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path))!\n            : new Config();")));
        await Git(repo, "checkout", "-q", "feature/auth");
        await Commit(Priya, "Cover token refresh with tests",
            ("tests/Orbit.Tests/TokenStoreTests.cs", Code("TokenStoreTests", "[Fact]\n    public void Expired_tokens_are_refreshed()\n        => Assert.False(new TokenStore().Current.IsExpired);")));
        await Git(repo, "checkout", "-q", "main");
        await Merge(Maya, "feature/auth");
        await Commit(Sofia, "Document setup and sign-in",
            ("README.md", "# Orbit\n\nA tiny client for the Orbit API.\n\n## Setup\n\n1. Install the .NET 10 SDK.\n2. Run `dotnet run --project src/Orbit`.\n3. Sign in when prompted; tokens are kept in your keychain.\n"));
        await Git(repo, "tag", "-a", "v1.0.0", "-m", "Orbit 1.0");

        // feature/export (Jonas) and feature/dark-mode (Sofia) run in parallel.
        await Git(repo, "checkout", "-q", "-b", "feature/export");
        await Commit(Jonas, "Export results as CSV",
            ("src/Orbit/Export/CsvExporter.cs", Code("CsvExporter", "public static string ToCsv(IEnumerable<string[]> rows)\n        => string.Join('\\n', rows.Select(r => string.Join(',', r)));")));
        await Git(repo, "checkout", "-q", "-b", "feature/dark-mode", "main");
        await Commit(Sofia, "Add theme tokens",
            ("src/Orbit/Ui/Theme.cs", Code("Theme", "public static string Background { get; set; } = \"#FFFFFF\";\n    public static string Foreground { get; set; } = \"#1E1E1E\";")));
        await Git(repo, "checkout", "-q", "feature/export");
        await Commit(Jonas, "Quote fields that contain commas",
            ("src/Orbit/Export/CsvExporter.cs", Code("CsvExporter", "public static string ToCsv(IEnumerable<string[]> rows)\n        => string.Join('\\n', rows.Select(r => string.Join(',', r.Select(Quote))));\n\n    private static string Quote(string field)\n        => field.Contains(',') ? $\"\\\"{field.Replace(\"\\\"\", \"\\\"\\\"\")}\\\"\" : field;")));
        await Git(repo, "checkout", "-q", "feature/dark-mode");
        await Commit(Sofia, "Dark palette",
            ("src/Orbit/Ui/Theme.cs", Code("Theme", "public static string Background { get; set; } = \"#1E1E1E\";\n    public static string Foreground { get; set; } = \"#F1F1F1\";\n    public static string Accent { get; set; } = \"#0E639C\";")));
        await Git(repo, "checkout", "-q", "main");
        await Commit(Liam, "Retry requests that time out",
            ("src/Orbit/Net/ApiClient.cs", ApiClient(retries: true, backoff: false)));
        await Merge(Jonas, "feature/export");
        await Git(repo, "checkout", "-q", "feature/dark-mode");
        await Commit(Sofia, "Remember the chosen theme",
            ("src/Orbit/Ui/ThemeStore.cs", Code("ThemeStore", "public static void Save(string theme)\n        => File.WriteAllText(\"theme.txt\", theme);")));
        await Git(repo, "checkout", "-q", "main");
        await Commit(Maya, "Bump version to 1.1",
            ("src/Orbit/Orbit.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Version>1.1.0</Version>\n  </PropertyGroup>\n</Project>\n"));
        await Git(repo, "tag", "-a", "v1.1.0", "-m", "Orbit 1.1");

        // feature/search (Priya) gets its own worktree.
        await Git(repo, "checkout", "-q", "-b", "feature/search");
        await Commit(Priya, "Add a search index",
            ("src/Orbit/Search/SearchIndex.cs", Code("SearchIndex", "private readonly Dictionary<string, List<int>> _terms = [];\n\n    public void Add(int id, string text)\n    {\n        foreach (var term in text.Split(' '))\n        {\n            (_terms.TryGetValue(term, out var ids) ? ids : _terms[term] = []).Add(id);\n        }\n    }")));
        await Commit(Priya, "Rank results by recency",
            ("src/Orbit/Search/Ranking.cs", Code("Ranking", "public static IEnumerable<int> ByRecency(IEnumerable<int> ids)\n        => ids.OrderByDescending(id => id);")));
        await Git(repo, "checkout", "-q", "main");

        // fix/startup (Liam), merged quickly.
        await Git(repo, "checkout", "-q", "-b", "fix/startup");
        await Commit(Liam, "Cache the parsed config",
            ("src/Orbit/Config.cs", Code("Config", "private static Config? _cached;\n\n    public string Endpoint { get; init; } = \"https://api.example.com\";\n\n    public static Config Load(string path)\n        => _cached ??= File.Exists(path)\n            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path))!\n            : new Config();")));
        await Git(repo, "checkout", "-q", "main");
        await Merge(Liam, "fix/startup");
        await Git(repo, "branch", "-q", "-d", "fix/startup");

        // A remote: origin has main (two commits behind local), dark-mode, and a branch only it has.
        var origin = Path.Combine(root, "origin.git");
        await Git(root, "init", "-q", "--bare", origin);
        await Git(repo, "remote", "add", "origin", "https://github.com/example/orbit.git");
        await Git(repo, "remote", "set-url", "--push", "origin", origin);
        await Git(repo, "push", "-q", origin, "main~2:refs/heads/main", "feature/dark-mode", "v1.0.0", "v1.1.0");
        await Git(repo, "push", "-q", origin, "feature/export:refs/heads/feature/telemetry");
        await Git(repo, "fetch", "-q", origin, "+refs/heads/*:refs/remotes/origin/*");
        await Git(repo, "branch", "-q", "--set-upstream-to=origin/main", "main");
        await Git(repo, "branch", "-q", "--set-upstream-to=origin/feature/dark-mode", "feature/dark-mode");
        await Commit(Jonas, "Expose request metrics",
            ("src/Orbit/Net/Metrics.cs", Code("Metrics", "public static int Requests { get; private set; }\n\n    public static void Count() => Requests++;")));
        await Commit(Maya, "Tidy up README badges",
            ("README.md", "# Orbit\n\n![CI](https://github.com/example/orbit/actions/workflows/ci.yml/badge.svg)\n\nA tiny client for the Orbit API.\n\n## Setup\n\n1. Install the .NET 10 SDK.\n2. Run `dotnet run --project src/Orbit`.\n3. Sign in when prompted; tokens are kept in your keychain.\n"));

        await Git(repo, "worktree", "add", "-q", Path.Combine(root, "orbit-search"), "feature/search");

        // A stash, then pending work: one staged file, one modified, one untracked.
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "Orbit", "Logging", "Log.cs"),
            Code("Log", "public static void Info(string message, params object[] args)\n        => Console.WriteLine($\"[info] {DateTime.Now:t} {string.Format(message, args)}\");"));
        await Git(repo, "stash", "push", "-q", "-m", "WIP: timestamps in log lines");
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "Orbit", "Net", "ApiClient.cs"), ApiClient(retries: true, backoff: true));
        await File.WriteAllTextAsync(Path.Combine(repo, "CHANGELOG.md"), "# Changelog\n\n## 1.2.0\n\n- Exponential backoff between retries.\n");
        await Git(repo, "add", "CHANGELOG.md");
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        await File.WriteAllTextAsync(Path.Combine(repo, "docs", "retries.md"), "# Retries\n\nRequests that time out are retried up to three times.\n");
    }

    private static string Code(string type, string body) =>
        $"namespace Orbit;\n\npublic sealed class {type}\n{{\n    {body}\n}}\n";

    private static string TokenStore(bool refresh) => refresh
        ? "namespace Orbit.Auth;\n\npublic sealed class TokenStore\n{\n    private Token _current = Keychain.Load(\"orbit\");\n\n    public Token Current\n    {\n        get\n        {\n            if (_current.IsExpired)\n            {\n                _current = Refresh(_current);\n                Keychain.Save(\"orbit\", _current);\n            }\n\n            return _current;\n        }\n    }\n\n    private static Token Refresh(Token token)\n        => AuthServer.Exchange(token.RefreshToken);\n}\n"
        : "namespace Orbit.Auth;\n\npublic sealed class TokenStore\n{\n    private Token _current = Keychain.Load(\"orbit\");\n\n    public Token Current => _current;\n}\n";

    private static string ApiClient(bool retries, bool backoff)
    {
        var send = !retries
            ? "        return await _http.SendAsync(request);\n"
            : "        for (var attempt = 1; ; attempt++)\n        {\n            try\n            {\n                return await _http.SendAsync(request);\n            }\n            catch (TaskCanceledException) when (attempt < 3)\n            {\n" +
              (backoff
                  ? "                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)));\n"
                  : "                await Task.Delay(500);\n") +
              "            }\n        }\n";
        return "namespace Orbit.Net;\n\npublic sealed class ApiClient(HttpClient http)\n{\n    private readonly HttpClient _http = http;\n\n    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)\n    {\n" + send + "    }\n}\n";
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Save(Window window, string path)
        => window.CaptureRenderedFrame()!.Save(path, PngBitmapEncoderOptions.Default);

    private static void DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(folder, recursive: true);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 20000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
