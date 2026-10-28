using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.Git;

namespace Avgit.App.Views;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppSettingsService settings, MainWindowViewModel vm)
    {
        Title = "Settings";
        Width = 640;
        Height = 590;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#252526"));

        var gitBox = new TextBox { Text = settings.Settings.GitExecutable };
        var sshBox = new TextBox { Text = settings.Settings.GitSshCommand, PlaceholderText = "e.g. ssh -i C:\\keys\\id_ed25519" };
        var generateKey = new Button { Content = "Generate key...", Margin = new Thickness(6, 0, 0, 0) };
        var sshRow = new DockPanel();
        DockPanel.SetDock(generateKey, Dock.Right);
        sshRow.Children.Add(generateKey);
        sshRow.Children.Add(sshBox);
        var credentialBox = new TextBox { Text = settings.Settings.GitCredentialHelper, PlaceholderText = "e.g. manager-core or store" };
        var diffBox = new TextBox { Text = settings.Settings.ExternalDiffCommand, PlaceholderText = "command with {repo}, {file}, {sha}" };
        var mergeBox = new TextBox { Text = settings.Settings.ExternalMergeCommand, PlaceholderText = "command with {repo}, {file}, {base}, {local}, {remote}" };
        var autoRefreshBox = new CheckBox { Content = "Auto-refresh on file changes", IsChecked = settings.Settings.AutoRefresh };
        var untrackedBox = new CheckBox
        {
            Content = "Show files inside untracked folders (off: one entry per folder, faster)",
            IsChecked = settings.Settings.ShowFilesInUntrackedFolders
        };
        var commitGraphBox = new CheckBox
        {
            Content = "Write a commit-graph when missing (much faster history in large repositories)",
            IsChecked = settings.Settings.MaintainCommitGraph
        };
        var updatesBox = new CheckBox
        {
            Content = "Download new versions automatically (installed on restart)",
            IsChecked = settings.Settings.CheckForUpdates
        };
        var terminalBox = new TextBox
        {
            Text = settings.Settings.TerminalShell,
            PlaceholderText = OperatingSystem.IsWindows()
                ? "default: PowerShell (pwsh if installed); e.g. cmd.exe"
                : "default: login shell ($SHELL); e.g. /bin/zsh"
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            Margin = new Thickness(18),
            RowSpacing = 10,
            ColumnSpacing = 10
        };

        AddRow(grid, 0, "Git executable", gitBox);
        AddRow(grid, 1, "SSH command", sshRow);
        AddRow(grid, 2, "Credential helper", credentialBox);
        AddRow(grid, 3, "External diff", diffBox);
        AddRow(grid, 4, "External merge", mergeBox);
        AddRow(grid, 5, "Terminal shell", terminalBox);
        AddRow(grid, 6, "Auto refresh", autoRefreshBox);
        AddRow(grid, 7, "Untracked files", untrackedBox);
        AddRow(grid, 8, "Performance", commitGraphBox);
        AddRow(grid, 9, "Updates", updatesBox);

        var pathText = new TextBlock
        {
            Text = settings.SettingsPath,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#A0A0A0")),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(pathText, 10);
        Grid.SetColumnSpan(pathText, 2);
        grid.Children.Add(pathText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        generateKey.Click += async (_, _) =>
        {
            var dialogs = new DialogService { Owner = this };
            try
            {
                IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Create SSH key",
                    SuggestedFileName = "id_ed25519",
                    ShowOverwritePrompt = true
                });
                if (file is null)
                {
                    return;
                }

                var path = file.Path.LocalPath;
                var directory = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                if (File.Exists(path))
                {
                    if (!await dialogs.ConfirmAsync("Overwrite SSH key",
                            $"'{path}' already exists. Replace it? The existing private key cannot be recovered."))
                    {
                        return;
                    }

                    try
                    {
                        File.Delete(path);
                        if (File.Exists(path + ".pub"))
                        {
                            File.Delete(path + ".pub");
                        }
                    }
                    catch (Exception ex)
                    {
                        await dialogs.ShowMessageAsync("SSH key", ex.Message);
                        return;
                    }
                }

                ProcessResult result = await ProcessRunner.RunAsync("ssh-keygen", directory,
                [
                    "-t", "ed25519", "-f", path, "-N", string.Empty,
                    "-C", $"{Environment.UserName}@avgit"
                ]);
                if (!result.Success)
                {
                    await dialogs.ShowMessageAsync("SSH key", result.Combined);
                    return;
                }

                sshBox.Text = $"ssh -i \"{path}\"";
            }
            catch (Exception ex)
            {
                await dialogs.ShowMessageAsync("SSH key", ex.Message);
            }
        };

        var cancel = new Button { Content = "Cancel", Width = 90 };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = "Save", Width = 90, Classes = { "accent" }, IsDefault = true };
        save.Click += (_, _) =>
        {
            settings.Settings.GitExecutable = string.IsNullOrWhiteSpace(gitBox.Text) ? "git" : gitBox.Text!.Trim();
            settings.Settings.GitSshCommand = sshBox.Text?.Trim() ?? string.Empty;
            settings.Settings.GitCredentialHelper = credentialBox.Text?.Trim() ?? string.Empty;
            settings.Settings.ExternalDiffCommand = diffBox.Text?.Trim() ?? string.Empty;
            settings.Settings.ExternalMergeCommand = mergeBox.Text?.Trim() ?? string.Empty;
            settings.Settings.AutoRefresh = autoRefreshBox.IsChecked == true;
            settings.Settings.ShowFilesInUntrackedFolders = untrackedBox.IsChecked == true;
            settings.Settings.MaintainCommitGraph = commitGraphBox.IsChecked == true;
            settings.Settings.CheckForUpdates = updatesBox.IsChecked == true;
            settings.Settings.TerminalShell = terminalBox.Text?.Trim() ?? string.Empty;
            settings.Save();
            vm.ReloadGit();

            Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 12);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
    }

    private static void AddRow(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
            FontSize = 12
        };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }
}

