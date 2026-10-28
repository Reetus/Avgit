using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avgit.App.ViewModels;

namespace Avgit.App.Controls;

/// <summary>
/// Shows a commit's refs as pills in one line. It takes at most <see cref="MaxShare"/> of
/// the available width; pills that do not fit are replaced by a "+N" pill, and the panel's
/// tooltip lists every ref. The first pill that does not fit is trimmed with an ellipsis
/// into the space left when that is wide enough to read, and always when it is the first pill.
/// </summary>
public sealed class RefPillsPanel : Panel
{
    public static readonly StyledProperty<IReadOnlyList<RefPill>?> PillsProperty =
        AvaloniaProperty.Register<RefPillsPanel, IReadOnlyList<RefPill>?>(nameof(Pills));

    public static readonly StyledProperty<string?> LaneColorProperty =
        AvaloniaProperty.Register<RefPillsPanel, string?>(nameof(LaneColor));

    public static readonly StyledProperty<double> MaxShareProperty =
        AvaloniaProperty.Register<RefPillsPanel, double>(nameof(MaxShare), 0.5);

    private const double Spacing = 4;
    private const double PillMaxWidth = 200;
    // A partly hidden pill is only worth showing trimmed if this much of it fits.
    private const double MinTrimmedWidth = 60;

    private static readonly IBrush TagColor = new SolidColorBrush(Color.Parse("#D29922"));
    private static readonly IBrush TagBackground = new SolidColorBrush(Color.Parse("#33D29922"));
    private static readonly IBrush OtherColor = new SolidColorBrush(Color.Parse("#A0A0A0"));
    private static readonly IBrush OverflowBackground = new SolidColorBrush(Color.Parse("#3E3E42"));
    private static readonly IBrush PillText = new SolidColorBrush(Color.Parse("#F1F1F1"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#A0A0A0"));

    private readonly List<Control> _pills = [];
    private readonly Border _overflow;
    private readonly TextBlock _overflowText;
    private int _visibleCount;

    static RefPillsPanel()
    {
        ClipToBoundsProperty.OverrideDefaultValue<RefPillsPanel>(true);
    }

    public RefPillsPanel()
    {
        _overflowText = new TextBlock { FontSize = 10, Foreground = PillText, VerticalAlignment = VerticalAlignment.Center };
        _overflow = MakeBorder(_overflowText, OverflowBackground, OverflowBackground, new CornerRadius(8));
    }

    public IReadOnlyList<RefPill>? Pills
    {
        get => GetValue(PillsProperty);
        set => SetValue(PillsProperty, value);
    }

    /// <summary>The commit's lane colour as #RRGGBB; branch pills are tinted with it.</summary>
    public string? LaneColor
    {
        get => GetValue(LaneColorProperty);
        set => SetValue(LaneColorProperty, value);
    }

    /// <summary>Fraction of the available width the pills may use, leaving the rest to the message.</summary>
    public double MaxShare
    {
        get => GetValue(MaxShareProperty);
        set => SetValue(MaxShareProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PillsProperty || change.Property == LaneColorProperty)
        {
            Rebuild();
        }
        else if (change.Property == MaxShareProperty)
        {
            InvalidateMeasure();
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        _pills.Clear();
        Color lane = Color.TryParse(LaneColor, out Color parsed) ? parsed : Color.Parse("#0052CC");
        foreach (RefPill pill in Pills ?? [])
        {
            _pills.Add(MakePill(pill, lane));
        }

        Children.AddRange(_pills);
        Children.Add(_overflow);
        InvalidateMeasure();
    }

    private static Geometry? _worktreeIcon;

    /// <summary>The sidebar's worktree icon, from the application resources.</summary>
    private static Geometry? WorktreeIcon
        => _worktreeIcon ??= Application.Current?.TryGetResource("IconWorktree", null, out var value) == true
            ? value as Geometry
            : null;

    private static Control MakePill(RefPill pill, Color lane)
    {
        var laneBrush = new SolidColorBrush(lane);
        var text = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = PillText,
            Inlines = [new Run(pill.Text)]
        };
        if (pill.Remotes is not null)
        {
            text.Inlines.Add(new Run("  " + pill.Remotes) { Foreground = MutedText });
        }

        IBrush background, border;
        var radius = new CornerRadius(8);
        switch (pill.Kind)
        {
            case RefPillKind.CurrentBranch:
                background = new SolidColorBrush(lane, 0.55);
                border = laneBrush;
                text.FontWeight = FontWeight.SemiBold;
                break;
            case RefPillKind.Branch:
                background = new SolidColorBrush(lane, 0.25);
                border = laneBrush;
                break;
            case RefPillKind.RemoteBranch:
                // Outlined only: a remote-tracking ref with no local branch here.
                background = Brushes.Transparent;
                border = new SolidColorBrush(lane, 0.7);
                text.Foreground = laneBrush;
                break;
            case RefPillKind.Tag:
                // Tags get squarer corners and their own colour so they read apart from branches.
                background = TagBackground;
                border = TagColor;
                text.Foreground = TagColor;
                radius = new CornerRadius(3);
                break;
            default:
                background = Brushes.Transparent;
                border = OtherColor;
                text.Foreground = OtherColor;
                break;
        }

        Control content = text;
        if (pill.InWorktree && WorktreeIcon is { } icon)
        {
            // Docked so the text still trims when the pill is squeezed.
            var marker = new PathIcon
            {
                Data = icon,
                Width = 11,
                Height = 9,
                Margin = new Thickness(0, 0, 4, 0),
                Foreground = text.Foreground,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(marker, Dock.Left);
            content = new DockPanel { Children = { marker, text } };
        }

        return MakeBorder(content, background, border, radius);
    }

    private static Border MakeBorder(Control content, IBrush background, IBrush border, CornerRadius radius) => new()
    {
        Child = content,
        Background = background,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        CornerRadius = radius,
        Padding = new Thickness(6, 0),
        Height = 16,
        MaxWidth = PillMaxWidth,
        VerticalAlignment = VerticalAlignment.Center
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = _pills.Count;
        if (count == 0)
        {
            _visibleCount = 0;
            return default;
        }

        var limit = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width * Math.Clamp(MaxShare, 0, 1));
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        foreach (Control pill in _pills)
        {
            pill.Measure(unbounded);
        }

        // "+N" is sized for the widest N it could show; the width differs by a digit at most.
        SetOverflowText(count);
        _overflow.Measure(unbounded);
        var overflowWidth = _overflow.DesiredSize.Width + Spacing;

        var used = 0.0;
        _visibleCount = 0;
        for (var i = 0; i < count; i++)
        {
            var width = _pills[i].DesiredSize.Width + (i > 0 ? Spacing : 0);
            var reserve = i < count - 1 ? overflowWidth : 0;
            if (used + width + reserve > limit)
            {
                break;
            }

            used += width;
            _visibleCount++;
        }

        if (_visibleCount < count)
        {
            var spacing = _visibleCount > 0 ? Spacing : 0;
            var reserve = _visibleCount < count - 1 ? overflowWidth : 0;
            var room = limit - used - spacing - reserve;
            if (_visibleCount == 0 || room >= MinTrimmedWidth)
            {
                Control pill = _pills[_visibleCount];
                pill.Measure(new Size(Math.Max(0, room), availableSize.Height));
                used += spacing + pill.DesiredSize.Width;
                _visibleCount++;
            }
        }

        var hidden = count - _visibleCount;
        if (hidden > 0)
        {
            SetOverflowText(hidden);
            _overflow.Measure(unbounded);
            used += Spacing + _overflow.DesiredSize.Width;
        }

        var height = Math.Max(_overflow.DesiredSize.Height, _pills.Max(p => p.DesiredSize.Height));
        return new Size(used, height);
    }

    private void SetOverflowText(int hidden)
    {
        var text = "+" + hidden;
        if (_overflowText.Text != text)
        {
            _overflowText.Text = text;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        for (var i = 0; i < _pills.Count; i++)
        {
            Control pill = _pills[i];
            if (i < _visibleCount)
            {
                pill.Arrange(new Rect(x, 0, pill.DesiredSize.Width, finalSize.Height));
                x += pill.DesiredSize.Width + Spacing;
            }
            else
            {
                // Parked outside the clipped bounds.
                pill.Arrange(new Rect(finalSize.Width + 1000, 0, pill.DesiredSize.Width, finalSize.Height));
            }
        }

        if (_visibleCount < _pills.Count)
        {
            _overflow.Arrange(new Rect(x, 0, _overflow.DesiredSize.Width, finalSize.Height));
        }
        else
        {
            _overflow.Arrange(new Rect(finalSize.Width + 1000, 0, _overflow.DesiredSize.Width, finalSize.Height));
        }

        return finalSize;
    }
}
