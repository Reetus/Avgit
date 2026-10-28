using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avgit.App.ViewModels;
using Avgit.Core.Model;

namespace Avgit.App.Views;

public partial class RepositoryView : UserControl
{
    public RepositoryView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnBranchDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: BranchRef branch } && DataContext is RepositoryViewModel vm)
        {
            vm.CheckoutBranchCommand.Execute(branch);
        }
    }

    private void OnTagDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string tag } && DataContext is RepositoryViewModel vm)
        {
            vm.CheckoutReferenceCommand.Execute(tag);
        }
    }

    private void OnWorktreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: WorktreeEntry worktree } && DataContext is RepositoryViewModel vm)
        {
            vm.OpenWorktreeCommand.Execute(worktree);
        }
    }

    private void OnStashDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: StashEntry stash } && DataContext is RepositoryViewModel vm)
        {
            vm.ApplyStashCommand.Execute(stash);
        }
    }
}
