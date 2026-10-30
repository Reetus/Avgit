using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avgit.App.ViewModels;
using Avgit.App.Views;

namespace Avgit.App.Services;

public interface IDialogService
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickFileAsync(string title);
    Task<string?> PickSaveFileAsync(string title, string defaultName);
    Task ShowTextAsync(string title, string text);
    Task<string?> PromptAsync(string title, string label, string initial = "");
    Task<(string? Value, bool Flag)?> PromptWithCheckBoxAsync(string title, string label, string initial, string checkLabel, bool checkDefault = false);
    Task<string?> EditTextAsync(string title, string label, string initial);
    Task<IReadOnlyList<RebasePlanItem>?> EditRebasePlanAsync(string title, IReadOnlyList<RebasePlanItem> commits);
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>Offers several actions; returns the chosen option, or null when cancelled.</summary>
    Task<string?> ChooseAsync(string title, string message, IReadOnlyList<string> options);
    Task ShowMessageAsync(string title, string message);

    /// <summary>
    /// Shows the trim-branches checklist (which loads itself); true when the user
    /// chose Delete, with the ticked branches in <see cref="TrimBranchesViewModel.SelectedRows"/>.
    /// </summary>
    Task<bool> TrimBranchesAsync(TrimBranchesViewModel viewModel);
}

/// <summary>Small programmatic dialogs; avoids a dependency on a dialog package.</summary>
public sealed class DialogService : IDialogService
{
    private static readonly FontFamily MonoFont =
        new("Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono, Liberation Mono, Noto Sans Mono, monospace");

    public Window? Owner { get; set; }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner?.StorageProvider is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFolder> folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title)
    {
        if (Owner?.StorageProvider is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string defaultName)
    {
        if (Owner?.StorageProvider is null)
        {
            return null;
        }

        IStorageFile? file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultName,
            ShowOverwritePrompt = true
        });
        return file?.TryGetLocalPath();
    }

    public async Task ShowTextAsync(string title, string text)
    {
        if (Owner is null)
        {
            return;
        }

        Window window = CreateWindow(title, 900, 620);
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 10 };
        var editor = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = MonoFont,
            FontSize = 12,
            Height = 520
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        panel.Children.Add(editor);
        var close = new Button { Content = Strings.Common_Close, Width = 90, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => window.Close();
        panel.Children.Add(close);
        window.Content = panel;
        await window.ShowDialog(Owner);
    }

    public Task<string?> PromptAsync(string title, string label, string initial = "")
        => ShowPromptAsync(title, label, initial, null, false);

    public async Task<(string? Value, bool Flag)?> PromptWithCheckBoxAsync(string title, string label, string initial, string checkLabel, bool checkDefault = false)
    {
        (string? Value, bool Flag)? result = await ShowPromptWithFlagAsync(title, label, initial, checkLabel, checkDefault);
        return result;
    }

    public async Task<string?> EditTextAsync(string title, string label, string initial)
    {
        if (Owner is null)
        {
            return null;
        }

        Window window = CreateWindow(title, 720, 540);
        string? value = null;
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });

        var editor = new TextBox
        {
            Text = initial,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = MonoFont,
            FontSize = 12,
            Height = 360
        };
        panel.Children.Add(editor);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = new Button { Content = Strings.Common_Cancel, Width = 90 };
        cancel.Click += (_, _) => window.Close();
        var ok = new Button { Content = Strings.Dialog_RunRebase, Width = 110, Classes = { "accent" }, IsDefault = true };
        ok.Click += (_, _) =>
        {
            value = editor.Text;
            window.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        window.Content = panel;
        await window.ShowDialog(Owner);
        return value;
    }

    public async Task<IReadOnlyList<RebasePlanItem>?> EditRebasePlanAsync(string title, IReadOnlyList<RebasePlanItem> commits)
    {
        if (Owner is null)
        {
            return null;
        }

        var viewModel = new InteractiveRebaseWindowViewModel(commits);
        var window = new InteractiveRebaseWindow { DataContext = viewModel };
        await window.ShowDialog(Owner);
        return viewModel.Accepted ? viewModel.ToPlan() : null;
    }

    public async Task<bool> TrimBranchesAsync(TrimBranchesViewModel viewModel)
    {
        if (Owner is null)
        {
            return false;
        }

        var window = new TrimBranchesWindow { DataContext = viewModel };
        await window.ShowDialog(Owner);
        return viewModel.Accepted;
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        if (Owner is null)
        {
            return false;
        }

        Window window = CreateWindow(title, 480, 190);
        var result = false;
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var no = new Button { Content = Strings.Common_Cancel, Width = 90 };
        no.Click += (_, _) => window.Close();
        var yes = new Button { Content = Strings.Common_OK, Width = 90, Classes = { "accent" } };
        yes.Click += (_, _) => { result = true; window.Close(); };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        panel.Children.Add(buttons);
        window.Content = panel;
        await window.ShowDialog(Owner);
        return result;
    }

    public async Task<string?> ChooseAsync(string title, string message, IReadOnlyList<string> options)
    {
        if (Owner is null)
        {
            return null;
        }

        Window window = CreateWindow(title, 520, 150 + 40 * (options.Count + 1));
        string? choice = null;
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 8 };
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 140,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White },
            Margin = new Thickness(0, 0, 0, 6)
        });

        foreach (var option in options)
        {
            var button = new Button
            {
                Content = option,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            // The first option is the recommended one.
            if (ReferenceEquals(option, options[0]))
            {
                button.Classes.Add("accent");
            }

            button.Click += (_, _) => { choice = option; window.Close(); };
            panel.Children.Add(button);
        }

        var cancel = new Button { Content = Strings.Common_Cancel, Width = 90, HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => window.Close();
        panel.Children.Add(cancel);

        window.Content = panel;
        await window.ShowDialog(Owner);
        return choice;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (Owner is null)
        {
            return;
        }

        Window window = CreateWindow(title, 560, 260);
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 160,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White }
        });
        var ok = new Button { Content = Strings.Common_OK, Width = 90, HorizontalAlignment = HorizontalAlignment.Right, Classes = { "accent" } };
        ok.Click += (_, _) => window.Close();
        panel.Children.Add(ok);
        window.Content = panel;
        await window.ShowDialog(Owner);
    }

    private async Task<string?> ShowPromptAsync(string title, string label, string initial, string? checkLabel, bool checkDefault)
    {
        (string? Value, bool Flag)? result = await ShowPromptWithFlagAsync(title, label, initial, checkLabel, checkDefault);
        return result?.Value;
    }

    private async Task<(string? Value, bool Flag)?> ShowPromptWithFlagAsync(string title, string label, string initial, string? checkLabel, bool checkDefault)
    {
        if (Owner is null)
        {
            return null;
        }

        Window window = CreateWindow(title, 500, checkLabel is null ? 200 : 240);
        string? value = null;
        var flag = checkDefault;

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var textBox = new TextBox { Text = initial, PlaceholderText = label };
        panel.Children.Add(textBox);

        CheckBox? checkBox = null;
        if (checkLabel is not null)
        {
            checkBox = new CheckBox { Content = checkLabel, IsChecked = checkDefault, Foreground = Brushes.White };
            panel.Children.Add(checkBox);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = new Button { Content = Strings.Common_Cancel, Width = 90 };
        cancel.Click += (_, _) => window.Close();
        var ok = new Button { Content = Strings.Common_OK, Width = 90, Classes = { "accent" }, IsDefault = true };
        ok.Click += (_, _) =>
        {
            value = textBox.Text?.Trim();
            flag = checkBox?.IsChecked == true;
            window.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        window.Content = panel;
        textBox.AttachedToVisualTree += (_, _) => textBox.Focus();
        await window.ShowDialog(Owner);

        return value is null ? null : (value, flag);
    }

    private static Window CreateWindow(string title, double width, double height)
    {
        return new Window
        {
            Title = title,
            Width = width,
            Height = height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#252526")),
            Content = new Panel()
        };
    }
}

