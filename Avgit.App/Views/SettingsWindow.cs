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
        Title = Strings.Common_Settings;
        Width = 640;
        Height = 590;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#252526"));

        var gitBox = new TextBox { Text = settings.Settings.GitExecutable };
        var sshBox = new TextBox { Text = settings.Settings.GitSshCommand, PlaceholderText = Strings.Settings_SshCommandPlaceholder };
        var generateKey = new Button { Content = Strings.Settings_GenerateKeyEllipsis, Margin = new Thickness(6, 0, 0, 0) };
        var sshRow = new DockPanel();
        DockPanel.SetDock(generateKey, Dock.Right);
        sshRow.Children.Add(generateKey);
        sshRow.Children.Add(sshBox);
        var credentialBox = new TextBox { Text = settings.Settings.GitCredentialHelper, PlaceholderText = Strings.Settings_CredentialHelperPlaceholder };
        var diffBox = new TextBox { Text = settings.Settings.ExternalDiffCommand, PlaceholderText = Strings.Settings_ExternalDiffPlaceholder };
        var mergeBox = new TextBox { Text = settings.Settings.ExternalMergeCommand, PlaceholderText = Strings.Settings_ExternalMergePlaceholder };
        var autoRefreshBox = new CheckBox { Content = Strings.Settings_AutoRefreshOnFileChanges, IsChecked = settings.Settings.AutoRefresh };
        var untrackedBox = new CheckBox
        {
            Content = Strings.Settings_ShowFilesInUntrackedFolders,
            IsChecked = settings.Settings.ShowFilesInUntrackedFolders
        };
        var commitGraphBox = new CheckBox
        {
            Content = Strings.Settings_MaintainCommitGraph,
            IsChecked = settings.Settings.MaintainCommitGraph
        };
        var updatesBox = new CheckBox
        {
            Content = Strings.Settings_DownloadUpdatesAutomatically,
            IsChecked = settings.Settings.CheckForUpdates
        };
        var terminalBox = new TextBox
        {
            Text = settings.Settings.TerminalShell,
            PlaceholderText = OperatingSystem.IsWindows()
                ? Strings.Settings_TerminalPlaceholderWindows
                : Strings.Settings_TerminalPlaceholderUnix
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            Margin = new Thickness(18),
            RowSpacing = 10,
            ColumnSpacing = 10
        };

        AddRow(grid, 0, Strings.Settings_GitExecutable, gitBox);
        AddRow(grid, 1, Strings.Settings_SSHCommand, sshRow);
        AddRow(grid, 2, Strings.Settings_CredentialHelper, credentialBox);
        AddRow(grid, 3, Strings.Settings_ExternalDiff, diffBox);
        AddRow(grid, 4, Strings.Settings_ExternalMerge, mergeBox);
        AddRow(grid, 5, Strings.Settings_TerminalShell, terminalBox);
        AddRow(grid, 6, Strings.Settings_AutoRefresh, autoRefreshBox);
        AddRow(grid, 7, Strings.Settings_UntrackedFiles, untrackedBox);
        AddRow(grid, 8, Strings.Settings_Performance, commitGraphBox);
        AddRow(grid, 9, Strings.Settings_Updates, updatesBox);

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
                    Title = Strings.Settings_CreateSSHKey,
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
                    if (!await dialogs.ConfirmAsync(Strings.Settings_OverwriteSSHKey,
                            string.Format(Strings.Settings_OverwriteSshKeyConfirm, path)))
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
                        await dialogs.ShowMessageAsync(Strings.Settings_SSHKey, ex.Message);
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
                    await dialogs.ShowMessageAsync(Strings.Settings_SSHKey, result.Combined);
                    return;
                }

                sshBox.Text = $"ssh -i \"{path}\"";
            }
            catch (Exception ex)
            {
                await dialogs.ShowMessageAsync(Strings.Settings_SSHKey, ex.Message);
            }
        };

        var cancel = new Button { Content = Strings.Common_Cancel, Width = 90 };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = Strings.Settings_Save, Width = 90, Classes = { "accent" }, IsDefault = true };
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

