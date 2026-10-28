using Avalonia;
using Avalonia.Media;

namespace Avgit.App.Converters;

public sealed class StatusBrushConverter : Avalonia.Data.Converters.IValueConverter
{
    private static readonly IBrush Added = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush Modified = new SolidColorBrush(Color.Parse("#D29922"));
    private static readonly IBrush Deleted = new SolidColorBrush(Color.Parse("#F85149"));
    private static readonly IBrush Conflict = new SolidColorBrush(Color.Parse("#DB6D28"));
    private static readonly IBrush Unknown = new SolidColorBrush(Color.Parse("#58A6FF"));
    private static readonly IBrush Default = new SolidColorBrush(Color.Parse("#A0A0A0"));

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not Core.Model.PathState status)
        {
            return Default;
        }

        if ((status & Core.Model.PathState.Conflicted) != 0)
        {
            return Conflict;
        }

        if ((status & Core.Model.PathState.New) != 0)
        {
            return Added;
        }

        if ((status & Core.Model.PathState.Removed) != 0)
        {
            return Deleted;
        }

        if ((status & Core.Model.PathState.Moved) != 0)
        {
            return Added;
        }

        if ((status & Core.Model.PathState.Edited) != 0)
        {
            return Modified;
        }

        if ((status & Core.Model.PathState.Untracked) != 0)
        {
            return Unknown;
        }

        return Default;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

public sealed class HexBrushConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string text && Color.TryParse(text, out Color color))
        {
            return new SolidColorBrush(color);
        }

        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
