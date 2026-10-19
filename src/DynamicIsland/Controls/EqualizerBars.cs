using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DynamicIsland.Controls;

/// <summary>
/// The little "now playing" waveform. Animates at 30 fps only while it is visible and the music is
/// playing; otherwise it costs nothing.
/// </summary>
public sealed class EqualizerBars : StackPanel
{
    private const double RestScale = 0.28;
    private const int FrameRate = 30;

    // Hand-tuned loops with different lengths so the bars never visibly sync up.
    private static readonly double[][] Patterns =
    [
        [0.35, 0.95, 0.5, 1.0, 0.4, 0.75],
        [0.7, 0.4, 1.0, 0.55, 0.85, 0.3],
        [0.5, 1.0, 0.35, 0.8, 0.6, 0.95],
        [0.9, 0.45, 0.7, 0.3, 1.0, 0.6],
    ];

    private static readonly double[] LoopSeconds = [1.3, 1.05, 1.45, 1.2];

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(EqualizerBars),
        new PropertyMetadata(Brushes.White, (d, _) => ((EqualizerBars)d).ApplyBrush()));

    public static readonly DependencyProperty BarHeightProperty = DependencyProperty.Register(
        nameof(BarHeight), typeof(double), typeof(EqualizerBars),
        new PropertyMetadata(14.0, (d, _) => ((EqualizerBars)d).ApplyHeight()));

    private readonly Rectangle[] _bars = new Rectangle[Patterns.Length];
    private readonly ScaleTransform[] _scales = new ScaleTransform[Patterns.Length];
    private bool _isPlaying;
    private bool _animating;

    public EqualizerBars()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;

        for (int i = 0; i < _bars.Length; i++)
        {
            _scales[i] = new ScaleTransform(1, RestScale);
            _bars[i] = new Rectangle
            {
                Width = 3,
                Height = BarHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(1.25, 0, 1.25, 0),
                Fill = BarBrush,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _scales[i],
            };
            Children.Add(_bars[i]);
        }

        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public double BarHeight
    {
        get => (double)GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            UpdateAnimation();
        }
    }

    private void ApplyBrush()
    {
        foreach (var bar in _bars) bar.Fill = BarBrush;
    }

    private void ApplyHeight()
    {
        foreach (var bar in _bars) bar.Height = BarHeight;
    }

    private void UpdateAnimation()
    {
        bool animate = _isPlaying && IsVisible;
        if (animate == _animating) return;
        _animating = animate;

        for (int i = 0; i < _scales.Length; i++)
        {
            if (animate)
            {
                _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, CreateLoop(i));
            }
            else if (IsVisible)
            {
                // Paused: settle the bars down gently (a finite animation stops ticking when done).
                var settle = new DoubleAnimation(RestScale, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, settle);
            }
            else
            {
                _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _scales[i].ScaleY = RestScale;
            }
        }
    }

    private static DoubleAnimationUsingKeyFrames CreateLoop(int index)
    {
        var pattern = Patterns[index];
        var loop = TimeSpan.FromSeconds(LoopSeconds[index]);
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = loop,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        for (int k = 0; k <= pattern.Length; k++)
        {
            double value = pattern[k % pattern.Length]; // last frame returns to the first for a seamless loop
            var time = KeyTime.FromTimeSpan(TimeSpan.FromTicks(loop.Ticks * k / pattern.Length));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, time, ease));
        }

        Timeline.SetDesiredFrameRate(animation, FrameRate);
        animation.Freeze();
        return animation;
    }
}
