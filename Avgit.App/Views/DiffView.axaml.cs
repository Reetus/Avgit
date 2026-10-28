using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Avgit.App.Views;

public partial class DiffView : UserControl
{
    public DiffView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
