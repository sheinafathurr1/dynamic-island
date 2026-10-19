using System.Windows;
using System.Windows.Media;

namespace DynamicIsland.Controls;

/// <summary>A tiny line chart of recent values (0..1), newest on the right, with a soft fill underneath.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private double[] _values = [];

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Number of points the full width stands for; fewer values fill it from the right.</summary>
    public int Capacity { get; set; } = 40;

    public void SetValues(IEnumerable<double> values)
    {
        _values = values.ToArray();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0 || _values.Length < 2) return;

        double step = width / Math.Max(1, Capacity - 1);
        double x0 = width - (_values.Length - 1) * step;
        Point At(int i) => new(x0 + i * step, height - 1 - Math.Clamp(_values[i], 0, 1) * (height - 2));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(At(0), isFilled: false, isClosed: false);
            for (int i = 1; i < _values.Length; i++) context.LineTo(At(i), isStroked: true, isSmoothJoin: true);
        }
        using (var context = area.Open())
        {
            context.BeginFigure(new Point(x0, height), isFilled: true, isClosed: true);
            for (int i = 0; i < _values.Length; i++) context.LineTo(At(i), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(width, height), isStroked: false, isSmoothJoin: false);
        }
        line.Freeze();
        area.Freeze();

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.White;
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
