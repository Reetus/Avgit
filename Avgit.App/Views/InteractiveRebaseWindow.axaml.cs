using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avgit.App.ViewModels;

namespace Avgit.App.Views;

public partial class InteractiveRebaseWindow : Window
{
    public InteractiveRebaseWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InteractiveRebaseWindowViewModel vm)
        {
            vm.Accepted = true;
            Close();
        }
    }
}
