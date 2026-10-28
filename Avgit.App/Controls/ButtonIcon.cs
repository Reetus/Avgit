using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Avgit.App.Controls;

/// <summary>
/// Attaches an icon geometry to a button. The <c>Button.toolbar</c> style draws it above the
/// button's text content, so the content stays a plain string.
/// </summary>
public static class ButtonIcon
{
    public static readonly AttachedProperty<Geometry?> DataProperty =
        AvaloniaProperty.RegisterAttached<Button, Geometry?>("Data", typeof(ButtonIcon));

    public static Geometry? GetData(Button button) => button.GetValue(DataProperty);

    public static void SetData(Button button, Geometry? value) => button.SetValue(DataProperty, value);
}
