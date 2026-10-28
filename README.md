# Avgit

Avgit is a native **Avalonia 12** Git client for Windows, macOS and Linux. It drives the `git` command line directly, so no native Git library is required.

Inspired by Sourcetree. Avgit is an independent project and is not affiliated with or endorsed by Atlassian; Sourcetree is a trademark of Atlassian.

## Features

- Open, clone (including blobless partial clones) and initialise repositories, with a recent-repository list.
- Multiple repositories open at once in closable tabs above the toolbar; `+` opens the home/recent-repositories screen, `Ctrl+T` returns home, `Ctrl+W` closes the current tab and `Ctrl+Tab` cycles repositories.
- Commit history with a rendered **commit graph** (lanes, merge edges, branch colours).
- Commit details, changed-file list and per-file diffs.
- Unified diff viewer with line numbers, added/removed highlighting, configurable context and whitespace-ignoring.
- Worktrees in the sidebar: each worktree with its branch, Open (in a tab) and Remove, "+ New worktree", and Prune for worktrees whose folders are gone. Branches checked out in another worktree are marked, in the sidebar and on their history pills; checking one out offers to open its worktree. A linked worktree shows an icon beside the branch in the toolbar.
- Image diffs: PNG, JPEG, GIF, BMP, WebP and ICO files show as pictures, with Old File and New File side by side, or a single panel for an added or deleted image. Git LFS images show their real content when Git LFS is installed.
- Staging at **file**, **hunk** and **selected-line** level; discarding at file and hunk level.
- Commit and amend, with `commit.template` prefill, sign-off and GPG signing.
- Commit context menu: checkout, create branch/tag here, merge, rebase onto, **interactive rebase** with per-commit action dropdowns and reordering (including root commits), single or multi-commit cherry-pick/revert, compare with base, hard reset and copy SHA.
- Branches: create with checkout, checkout, delete. Selecting a branch jumps the history to its tip commit; double-click checks it out.
- Remote management: add, edit (rename or change URL) and remove remotes from the sidebar.
- Tags: create, annotated tags, delete.
- Stashes: save (including untracked files), apply, pop, drop and preview.
- Fetch, pull (optionally with rebase), push (including choosing a remote and pushing a detached HEAD to a named branch), force push with lease, fetch-all, merge, rebase, hard reset, cherry-pick and revert.
- Rejected pushes explain why and offer the matching next step:
  - Remote has unfetched commits (`fetch first`): pull with rebase or merge, then push, or fetch only. Force is not offered, because it would discard commits you have never seen.
  - Branch is behind or has diverged from fetched commits (`non-fast-forward`): pull with rebase or merge, then push, or force push with lease. The dialog says how many remote commits would be discarded.
  - Force with lease refused because someone pushed meanwhile (`stale info`): fetch to review.
  - Server-side rejection (hook or protected branch): the server's message is shown.
  - Authentication failure: points to the credential helper and SSH settings.
  - No upstream: retried automatically with `--set-upstream`.
- Sidebar for local branches, remote branches, remotes, tags and stashes.
- Compare a selected commit against a stored compare base, with changed files and range diff.
- File history, blame, reflog, log-message search, working-tree grep, Git notes and sparse checkout.
- Patches, archives, tag push, branch rename and remote-branch deletion.
- Auto-refresh via file watching (including index/ref changes) with a Settings toggle; open tabs are restored on restart, with reopen-closed support.
- Commit sign-off and GPG signing options.
- Conflict banner with merge abort, rebase continue/skip/abort, cherry-pick continue/skip/abort and revert continue/abort; commit is disabled until conflicts are staged.
- History paging ("Load more") beyond the first 500 commits.
- **Terminal** toolbar button (Repository > Open in Terminal, `Ctrl+Shift+C`) opens a shell in the repository folder: PowerShell on Windows (PowerShell 7 when installed), the login shell in Terminal.app on macOS (zsh by default), and the login shell (`$SHELL`, falling back to bash) in the first available terminal emulator on Linux (`$TERMINAL`, then x-terminal-emulator, gnome-terminal, konsole, xfce4-terminal, kitty, alacritty, wezterm, foot, xterm). Settings > Terminal shell overrides the shell.
- Settings for the Git executable, SSH command and key generation, credential helper, external diff/merge commands and the terminal shell; changes apply to open tabs immediately. Saved custom commands live in Advanced Git. Diff context and whitespace options live in the view toolbars.
- **Advanced Git** window: submodules (add/update/sync/deinit/remove), Git LFS (patterns, fetch/pull and file locking), Git Flow, Subtree (with remote/URL pull and push), git-svn, bisect, worktrees, patches/archives, notes, sparse checkout, diagnostics, a basic hooks editor, saved custom commands and external tools (three-way merge placeholders).
- Dark Fluent theme with a compact, dense layout.

The Git engine shells out to `git` using explicit argument lists. Git terminal prompts are disabled and the Git credential manager is forced non-interactive; a credential helper and SSH command are configurable. Every process has a 10-minute timeout. SSH agents/keys may still prompt depending on the configured SSH client. No native Git library is required. Git LFS, Git Flow and git-svn are optional external tools invoked through `git`; their tabs report a clear error when the tool is missing.

## Limitations

- Git only; other version control systems are not supported.
- No hosting-provider integrations (pull requests, accounts).
- Interactive rebase over ranges containing merge commits (linear ranges and root rewrites are supported; non-interactive rebase with `--rebase-merges` is available).
- Drag-and-drop reordering of tabs or rebase rows (rebase rows have up/down controls).
- A full credential/key manager (SSH key generation, an SSH command and a credential helper are supported).
- Repository groups/folders in the home screen; recents remain a flat list.
- Keyboard shortcuts/output panes for custom actions (saved actions are available from the Advanced Git window).
- Exporting a merge commit as a single `format-patch` file (Git omits merges; Avgit reports this instead of writing a parent patch).
- git filter-repo (run it as a custom command if installed).

## Project layout

```
Avgit.Core/            Domain models, unified-diff parser, patch builder, commit-graph builder
Avgit.Git/             Git process runner, repository handler and push-result parsing
Avgit.App/             Avalonia 12 application, views, view models and custom controls
Avgit.Tests/           xUnit tests including Avalonia.Headless UI tests
branding/              Logo source, icon generator, platform icon files
branding/linux/        Install script, AppImage build and desktop entry
branding/windows/      Velopack Setup.exe build
Directory.Build.props  Shared build settings (nullable, warnings as errors, code-style analysis)
.editorconfig          Code style enforced at build time
dotnet-tools.json      Pinned local tools (vpk)
Avgit.slnx             Solution
```

### Layers

- **Avgit.Core** has no UI or process dependencies (only CommunityToolkit.Mvvm for observable models). It owns the models (`HistoryRow`, `CommitRecord`, `RefBadge`, `FileDiff`/`Hunk`/`HunkLine`, `ChangedFile`, `RepoSummary`, `GraphRow`/`GraphEdge`), the diff parser, hunk-patch generation and the commit-graph layout (`CommitGraphBuilder`).
- **Avgit.Git** owns the `IGitEngine` contract, `GitRepoHandler`, `GitManager` (opens a repository from any folder inside it), `ProcessRunner` and `PushOutcome`. All Git commands, status parsing, log formatting, diff invocation and patch application live here.
- **Avgit.App** is MVVM over Avalonia using CommunityToolkit.Mvvm (`[ObservableProperty]` partial properties). It contains no Git parsing logic.

## Requirements

- .NET SDK 10.0 or later
- Git 2.23 or later on `PATH` (configurable in Settings)
- Optional: `git-lfs`, `git-flow` (AVH edition) and/or `git-svn` for the corresponding Advanced Git tabs. git-svn also requires an initialized SVN bridge in the repository.
- Optional: `ssh-keygen` for the Generate key button in Settings.

## Build

Run these commands from this `src` directory.

```powershell
dotnet build Avgit.slnx
```

Warnings are errors, and the code style in `.editorconfig` is enforced during the build (`Directory.Build.props`). Run `dotnet format Avgit.slnx` to apply it.

## Run

```powershell
dotnet run --project Avgit.App
```

You can also pass a repository path directly:

```powershell
dotnet run --project Avgit.App -- "C:\path\to\repo"
```

## Publish

Two publish profiles live in `Avgit.App/Properties/PublishProfiles`. Pass the runtime identifier for the target (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`, ...).

**Native AOT**: a native executable with no .NET runtime dependency, fast startup, and lower memory use. Avalonia's native renderer libraries (`libSkiaSharp`, `libHarfBuzzSharp` and, on Windows, `av_libglesv2.dll`) ship next to the executable.

```powershell
dotnet publish Avgit.App -r win-x64 -p:PublishProfile=NativeAot
dotnet publish Avgit.App -r linux-x64 -p:PublishProfile=NativeAot
```

Native AOT must be built on the target OS. Windows needs the Visual Studio "Desktop development with C++" workload. Linux needs `clang` (or `gcc`) and `zlib1g-dev`. If linking on Windows fails with `'vswhere.exe' is not recognized`, add `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` to `PATH`, or publish from a Developer PowerShell.

**Single file**: one self-contained, trimmed executable with the .NET runtime and native libraries embedded. Native libraries are extracted on first run. This profile can cross-publish to other operating systems.

```powershell
dotnet publish Avgit.App -r win-x64 -p:PublishProfile=SingleFile
dotnet publish Avgit.App -r linux-x64 -p:PublishProfile=SingleFile
```

Output goes to `artifacts/publish/aot/<rid>/` and `artifacts/publish/singlefile/<rid>/`. Both builds use compiled XAML bindings and source-generated JSON, so they produce no trim or AOT warnings. Both also use invariant globalization, so no ICU is needed on Linux.

## Icons

`branding/avgit-logo-original.png` is the source artwork. All icon files are generated from it:

```powershell
dotnet run branding/generate-icons.cs
```

| File | Used for |
|---|---|
| `Avgit.App/Assets/avgit.ico` | Windows executable icon (16–256 px, embedded via `ApplicationIcon`) |
| `Avgit.App/Assets/avgit-256.png` | Window title bar, taskbar and Alt+Tab icon on every OS |
| `branding/linux/hicolor/<size>/apps/avgit.png` | Linux icon theme (16–512 px) |
| `branding/linux/avgit.desktop` | Linux desktop entry (`Icon=avgit`, `Exec=avgit %F`) |
| `branding/macos/avgit.icns` | macOS app bundle icon (16–1024 px with Retina variants) |
| `branding/avgit-1024.png` | Cleaned 1024 px master (opaque tile) for store listings and docs |

Windows and Linux icons are cropped close to the tile so they stay legible at small sizes. The macOS icon keeps the artwork's padding, which matches Apple's icon grid.

The icons in `Avgit.App/Styles/Icons.axaml` are [Font Awesome Free](https://fontawesome.com) 6.7.2 solid icons, licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Each is the icon's SVG path data stored as a `StreamGeometry`. To add one, copy the `d` attribute from `svgs/solid/<name>.svg` in the `@fortawesome/fontawesome-free` package. Set it on a `Button.toolbar.icon` with `controls:ButtonIcon.Data`.

### Installing on Linux

After publishing, `branding/linux/install.sh` installs Avgit for the current user (no root needed):

```bash
dotnet publish Avgit.App -r linux-x64 -p:PublishProfile=NativeAot
sh branding/linux/install.sh                 # or: install.sh /path/to/publish/dir
sh branding/linux/install.sh --uninstall
```

It copies the whole publish folder to `~/.local/lib/avgit/`, because the NativeAOT binary loads `libSkiaSharp.so` and `libHarfBuzzSharp.so` from its own folder. It then links `~/.local/bin/avgit` to the binary, installs the icons into `~/.local/share/icons/hicolor/`, and writes `~/.local/share/applications/avgit.desktop`. The launcher's `Exec` uses the absolute path because `~/.local/bin` is often not on `PATH` in desktop sessions. Use `--prefix DIR` to install elsewhere, for example `--prefix /usr/local` with `sudo`. On WSL, installed launchers also appear in the Windows Start menu.

### Linux AppImage (Velopack)

`branding/linux/build-appimage.sh` publishes the NativeAOT build and packs it with [Velopack](https://velopack.io) into a self-updating `Avgit-linux-x64.AppImage`:

```bash
sh branding/linux/build-appimage.sh                    # version from <Version> in Avgit.App.csproj
sh branding/linux/build-appimage.sh --version 0.2.0    # or --rid linux-arm64, --output DIR, --release-notes FILE
```

Output goes to `artifacts/releases/<rid>/`: `Avgit-<rid>.AppImage` plus the Velopack feed (`releases.<rid>.json`, full and delta `.nupkg` files) to upload for updates. Keep earlier releases in that folder so delta packages can be built. The AppImage carries the same desktop entry and hicolor icon set as the install script; `vpk` is pinned in `dotnet-tools.json` and must match the `Velopack` package version in `Avgit.App.csproj`.

An AppImage does not add itself to the app menu. Install it with `sh branding/linux/install.sh artifacts/releases/linux-x64/Avgit-linux-x64.AppImage` (copied to `~/.local/lib/avgit/Avgit.AppImage`, where Velopack can update it in place), or use AppImageLauncher or Gear Lever.

### Windows installer (Velopack)

`branding/windows/build-setup.ps1` publishes the NativeAOT build and packs it with Velopack into `Avgit-win-x64-Setup.exe`:

```powershell
powershell -ExecutionPolicy Bypass -File branding/windows/build-setup.ps1        # version from <Version> in Avgit.App.csproj
powershell -ExecutionPolicy Bypass -File branding/windows/build-setup.ps1 -Version 0.2.0 -SignParams '/a /fd sha256 /tr http://timestamp.digicert.com /td sha256'
```

Output goes to `artifacts/releases/<rid>/`:

- `Avgit-win-x64-Setup.exe`: per-user installer, no admin rights needed. It installs to `%LocalAppData%\Avgit`, adds Start menu and desktop shortcuts, and registers an uninstaller in Apps & features.
- `Avgit-win-x64-Portable.zip`: runs without installing.
- The Velopack feed (`releases.win-x64.json`, full and delta `.nupkg` files) to upload for updates.

Other options: `-Runtime win-arm64`, `-Output DIR`, `-ReleaseNotes FILE`, `-Profile SingleFile` (no Visual Studio C++ workload needed), and `-AzureTrustedSignFile` as an alternative to `-SignParams`. Unsigned installers trigger a SmartScreen warning on first run. The NativeAOT profile needs the Visual Studio "Desktop development with C++" workload, and the script puts `vswhere.exe` on `PATH` for the linker lookup.

### Velopack channels

Each package uses its runtime identifier as its Velopack channel (`win-x64`, `linux-x64`). Windows and Linux packages share one GitHub release, and with the default channels (`win`, `linux`) their `.nupkg` names would collide. An installed copy updates only from the channel it was installed from, so keep these channel names stable.

### Releases and updates

`.github/workflows/release.yml` builds and publishes a release. To run it, open **Actions → Release → Run workflow** and enter a version, such as `0.2.0`, plus optional release notes. The workflow:

1. Checks that the version is valid and that its tag does not exist yet.
2. Runs the CI workflow.
3. Packages Windows (`windows-latest`) and Linux (`ubuntu-22.04`, so the binary runs on older glibc versions) in parallel, each with `vpk download github` first so a delta package is built against the previous release.
4. Publishes one GitHub release `v<version>` containing both platforms through `vpk upload github`.

A version with a suffix, such as `0.2.0-beta.1`, becomes a GitHub pre-release. Installed copies ignore pre-releases.

Installed copies of Avgit (from Setup.exe or the AppImage) check GitHub Releases at startup and download a newer version in the background. Avgit then offers **Restart to update**; otherwise the update installs the next time Avgit starts. **Help → Check for Updates** checks on demand, and Settings → Updates turns the automatic download off. Copies run from a build or publish folder never update.

### Continuous integration

`.github/workflows/ci.yml` runs on every pull request, on every push to `main`, and from the release workflow. On Windows and on Linux it:

- builds the solution (warnings are errors);
- runs the test suite, skipping the timing-sensitive `Category=Performance` tests;
- runs a NativeAOT publish.

The Windows job also runs `dotnet format --verify-no-changes`.

## Test

```powershell
dotnet test Avgit.slnx
```

The test suite covers:

- Unified-diff parsing against real git output (modified, new, deleted, renamed, copied, mode-only, binary, CRLF, missing final newline, submodules, limits) and quoted/UTF-8 paths; synthetic diffs for untracked and unchanged files.
- Hunk and selected-line patch generation applied with real `git apply` (stage, unstage, discard, new files, renames, CRLF, no final newline).
- Porcelain v2 status and `--name-status` parsing, including every unmerged state and in-progress merge/rebase/cherry-pick/revert detection.
- Commit-graph layout: lane assignment, merges, colours, paging continuity, edge continuity over random histories, and a headless render check.
- End-to-end Git operations against real temporary repositories (status, staging, commits, logs, diffs, branches, tags, stashes, merges, rebases, cherry-picks, clones).
- Push rejection handling: parsing of real `git push --porcelain` output, and recovery flows (pull with rebase or merge, force with lease, stale lease, server rejection) against a real remote.
- Amend message handling, commit-selection races during refresh, and file-status selection stability.
- Large-repository behaviour: lazy tab restore, history paging, history cache, untracked folders, inactive-tab watcher, diff virtualization and commit-graph writing.
- Terminal launcher selection per platform.
- Headless Avalonia rendering of the main views and view-model interactions.

Headless render artifacts are written under the OS temporary directory in `avgit-headless/`.

## Keyboard and interaction notes

- Click a branch in the sidebar to jump the history to that branch's tip commit.
- Double-click a branch in the sidebar to check it out.
- Double-click a tag to check it out (detached HEAD confirmation).
- Double-click a stash to apply it.
- Repositories open in closable tabs above the toolbar; use `+` to return to home/recent repositories and × in the tab header to close one.
- Right-click a commit for checkout, branch/tag, merge, rebase, interactive rebase, reset and copy-SHA actions.
- In the diff viewer, tick line checkboxes and use **Stage selected lines** to stage a partial hunk.
- **Stage hunk** / **Unstage hunk** / **Discard hunk** operate on the whole hunk.
- Diff context and whitespace settings are persisted per user.

## Performance with large repositories

Starting a git process costs tens of milliseconds on Windows (about 3 ms on Linux), often more than the command itself. Avgit therefore batches reads, and loads the parts of a tab independently:

- **Tabs:** restored tabs appear immediately. The tab that was active when Avgit closed is re-selected and loads first; other tabs load the first time they are shown.
- **Parallel loading:** history, branches and tags, and working-copy status load in parallel, and each pane fills in as soon as its data arrives. Thin progress bars show which sections are still loading.
- **History cache:** the first page of each repository's history is cached on disk (next to `settings.json`, in `cache/`). A reopened tab shows it instantly, marked *cached*, until the live history replaces it.
- **Batched commands:**
  - A commit click runs a single `git log -1 --name-status` for details, tags and changed files, plus one `git diff` for the selected file.
  - One `git status` replaces the old status, summary, branch and HEAD calls.
  - One `for-each-ref` lists branches and tags. It avoids `%(refname:short)`, whose ambiguity checks cost about 0.5 s for 1,000 tags.
- **Commit-graph:** without one, `git log --topo-order` walks the entire history before printing anything. After a repository's first load, Avgit writes a commit-graph (`git commit-graph write --reachable`, the same file `git gc` maintains) if one is missing. Turn this off in Settings > Performance.
- **Load more** appends the next page (`--skip`) and continues the graph lanes, instead of reloading from the first commit.
- **UI thread:** graph layout and diff parsing run off the UI thread. The diff view is virtualized, so only visible lines create controls.
- **Cancellation:** superseded lookups, such as a commit click replaced by another click, cancel and kill their git process.
- **File watcher:**
  - Hidden tabs only note that something changed, and refresh when shown.
  - During long bursts of file events (builds, checkouts, package installs), refreshes happen at most every 3 s.
  - Events inside git-ignored top-level folders, and git-ignored folders with build or dependency names (`node_modules`, `bin`, `obj`, ...), are skipped. Each folder is checked once with `git check-ignore`.
- **Untracked folders:** Settings > *Untracked files* can list each untracked folder as one entry (`--untracked-files=normal`) instead of every file inside it.

Measured on git.git (82,605 commits, about 1,000 tags) on Windows; numbers are from the opt-in benchmark below:

| Action | Before | After |
|---|---|---|
| Open repository (fully loaded) | 2.7 s, 17 git processes | 0.7 s, 8 processes (1.4 s before the commit-graph exists) |
| Reopen: history visible | 2.7 s | about 50 ms (cache) |
| Commit click | about 800 ms, 6 processes | about 180 ms, 2 processes |
| Load more (500 commits) | 850 ms | about 220 ms |
| Refresh | 1.3 s, 10 processes | about 300 ms, 5 processes |

Run the benchmark against any repository with:

```powershell
$env:AVGIT_PERF_REPO = 'C:\path\to\large\repo'
dotnet test Avgit.Tests -c Release --filter PerfBenchmarks
```

Results are appended to `%TEMP%/avgit-perf.txt`, or to `AVGIT_PERF_OUT`.

## Design notes

- Git status is read with `git --no-optional-locks status --porcelain=v2 --branch -z`. One run yields files, renames, conflicts, the branch and HEAD. `--no-optional-locks` keeps background refreshes from taking `index.lock` or rewriting the index.
- History uses `git log` with a record-separator format, then the graph builder assigns lanes and colours.
- Diffs use `git diff` / `git diff --cached` / commit diffs, parsed into the same model for display.
- Partial staging writes a computed unified patch to stdin and applies it with `git apply --cached` (or reverse for unstage/discard).
- Pushes use `git push --porcelain`; `PushOutcome` classifies each rejected ref so the UI can offer the right recovery.
- Git terminal prompts are disabled; the credential manager is forced non-interactive, while a configured credential helper still works. SSH agents/keys may still prompt. Each Git process is capped at 10 minutes and killed on timeout.
- Custom commands and external diff/merge tools run through `cmd.exe /d /s /c` on Windows and `/bin/sh -c` elsewhere. The placeholders `{repo}`, `{file}` and `{sha}` are substituted before execution, so normal shell quoting rules for the platform apply.


