using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Avgit.App.Views;

public partial class WorkingCopyView : UserControl
{
    public WorkingCopyView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
