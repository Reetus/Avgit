using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avgit.App.ViewModels;

namespace Avgit.App.Views;

/// <summary>Checklist of branches to trim; the window is also the confirmation.</summary>
public partial class TrimBranchesWindow : Window
{
    public TrimBranchesWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrimBranchesViewModel { CanDelete: true } vm)
        {
            vm.Accepted = true;
            Close();
        }
    }
}
