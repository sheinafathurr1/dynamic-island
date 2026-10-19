using System.Windows;
using System.Windows.Media;

namespace DynamicIsland.Controls;

/// <summary>iOS-style battery glyph, drawn directly so it is crisp at any DPI.</summary>
public sealed class BatteryIcon : FrameworkElement
{
    private static readonly Brush OutlineBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush NormalBrush = Brushes.White;
    private static readonly Brush ChargingBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    private static readonly Brush LowBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    private static readonly Pen OutlinePen = Frozen(new Pen(OutlineBrush, 1));
    private static readonly Pen BoltPen = Frozen(new Pen(Brushes.Black, 0.9) { LineJoin = PenLineJoin.Round });
    private static readonly Geometry Bolt = Frozen(Geometry.Parse("M13.2,1.6 L8.6,7.2 L11.6,7.2 L10.6,11.4 L15.2,5.8 L12.2,5.8 Z"));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(BatteryIcon),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsChargingProperty = DependencyProperty.Register(
        nameof(IsCharging), typeof(bool), typeof(BatteryIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Charge between 0 and 1.</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsCharging
    {
        get => (bool)GetValue(IsChargingProperty);
        set => SetValue(IsChargingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(27, 13);

    protected override void OnRender(DrawingContext dc)
    {
        double level = Math.Clamp(Level, 0, 1);
        var fill = IsCharging ? ChargingBrush : level <= 0.2 ? LowBrush : NormalBrush;

        dc.DrawRoundedRectangle(null, OutlinePen, new Rect(0.5, 0.5, 23, 12), 3.5, 3.5);
        dc.DrawRoundedRectangle(OutlineBrush, null, new Rect(24.5, 4.5, 1.8, 4), 0.9, 0.9);
        dc.DrawRoundedRectangle(fill, null, new Rect(2.5, 2.5, Math.Max(2, 19 * level), 8), 1.8, 1.8);

        if (IsCharging) dc.DrawGeometry(Brushes.White, BoltPen, Bolt);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
