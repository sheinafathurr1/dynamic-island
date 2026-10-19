using System.Windows;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland.Controls;

/// <summary>Small colour weather glyphs (sun, moon, clouds, rain, snow, storm, fog), drawn as vectors on a 24×24 grid.</summary>
public sealed class WeatherIcon : FrameworkElement
{
    private static readonly Brush Sun = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));
    private static readonly Brush Moon = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA)));
    private static readonly Brush Cloud = Frozen(new SolidColorBrush(Color.FromRgb(0xD1, 0xD1, 0xD6)));
    private static readonly Brush DarkCloud = Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));
    private static readonly Brush Water = Frozen(new SolidColorBrush(Color.FromRgb(0x64, 0xD2, 0xFF)));
    private static readonly Brush Snow = Frozen(new SolidColorBrush(Colors.White));

    private static readonly Geometry CloudShape = Frozen(BuildCloud());
    private static readonly Geometry MoonShape = Frozen(new CombinedGeometry(GeometryCombineMode.Exclude,
        new EllipseGeometry(new Point(12, 12), 7.5, 7.5), new EllipseGeometry(new Point(16, 8.5), 6.5, 6.5)));
    private static readonly Geometry BoltShape = Frozen(Geometry.Parse("M12.5,15 L9.5,20 L12,20 L10.5,24 L15.5,18.5 L13,18.5 L14.5,15 Z"));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(int), typeof(WeatherIcon), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDayProperty = DependencyProperty.Register(
        nameof(IsDay), typeof(bool), typeof(WeatherIcon), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A <see cref="WeatherKind"/>, as int so XAML can set it.</summary>
    public int Kind
    {
        get => (int)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool IsDay
    {
        get => (bool)GetValue(IsDayProperty);
        set => SetValue(IsDayProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 24, size / 24));

        switch ((WeatherKind)Kind)
        {
            case WeatherKind.Clear:
                DrawSky(dc, new Point(12, 12), 1);
                break;
            case WeatherKind.PartlyCloudy:
                dc.PushTransform(new TranslateTransform(-3.5, -3.5));
                DrawSky(dc, new Point(12, 12), 0.7);
                dc.Pop();
                dc.PushTransform(new TranslateTransform(1.5, 3));
                dc.DrawGeometry(Cloud, null, CloudShape);
                dc.Pop();
                break;
            case WeatherKind.Cloudy:
                dc.PushTransform(new TranslateTransform(-2.5, -2.5));
                dc.DrawGeometry(DarkCloud, null, CloudShape);
                dc.Pop();
                dc.PushTransform(new TranslateTransform(1, 1.5));
                dc.DrawGeometry(Cloud, null, CloudShape);
                dc.Pop();
                break;
            case WeatherKind.Fog:
                var fog = new Pen(Cloud, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                for (int i = 0; i < 4; i++)
                    dc.DrawLine(fog, new Point(4 + i % 2 * 2, 7 + i * 3.8), new Point(20 - (i + 1) % 2 * 2, 7 + i * 3.8));
                break;
            default:
                // Precipitation: a cloud on top, with drops, flakes or a bolt underneath.
                dc.PushTransform(new TranslateTransform(0, -4));
                dc.DrawGeometry((WeatherKind)Kind == WeatherKind.Storm ? DarkCloud : Cloud, null, CloudShape);
                dc.Pop();
                DrawFalling(dc, (WeatherKind)Kind);
                break;
        }

        dc.Pop();
        dc.Pop();
    }

    private void DrawSky(DrawingContext dc, Point center, double scale)
    {
        if (!IsDay)
        {
            dc.PushTransform(new ScaleTransform(scale, scale, center.X, center.Y));
            dc.DrawGeometry(Moon, null, MoonShape);
            dc.Pop();
            return;
        }

        var rays = new Pen(Sun, 1.8 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
            dc.DrawLine(rays, center + direction * 7.6 * scale, center + direction * 10 * scale);
        }
        dc.DrawEllipse(Sun, null, center, 5 * scale, 5 * scale);
    }

    private static void DrawFalling(DrawingContext dc, WeatherKind kind)
    {
        switch (kind)
        {
            case WeatherKind.Snow:
                foreach (var (x, y) in new[] { (8.0, 18.0), (12.5, 21.0), (17.0, 18.0) })
                    dc.DrawEllipse(Snow, null, new Point(x, y), 1.4, 1.4);
                break;
            case WeatherKind.Storm:
                dc.DrawGeometry(Sun, null, BoltShape);
                break;
            default:
                var drop = new Pen(Water, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                int count = kind == WeatherKind.Drizzle ? 2 : 3;
                for (int i = 0; i < count; i++)
                {
                    double x = count == 2 ? 10 + i * 5 : 8 + i * 4.5;
                    double length = kind == WeatherKind.Drizzle ? 2 : 4;
                    dc.DrawLine(drop, new Point(x, 16.5), new Point(x - 1.2, 16.5 + length));
                }
                break;
        }
    }

    /// <summary>A puffy cloud: two bumps on a rounded base, roughly 4..20 × 7..17.</summary>
    private static Geometry BuildCloud()
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(new RectangleGeometry(new Rect(4, 11.5, 16, 5.5), 2.75, 2.75));
        group.Children.Add(new EllipseGeometry(new Point(9.5, 11.5), 4, 4));
        group.Children.Add(new EllipseGeometry(new Point(14.5, 10.5), 5, 5));
        return group;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
