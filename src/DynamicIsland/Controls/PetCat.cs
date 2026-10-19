using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland.Controls;

public enum CatMood
{
    Sleeping,
    Awake,
    Dancing,
}

/// <summary>
/// A little black cat hanging from the island's bottom edge by its front paws. It sleeps (breathing,
/// drifting "z"s), wakes when the pointer comes near and follows it with its eyes, bobs its head to
/// music, and shows hearts when petted. Drawn in code; its loops run at a low frame rate and stop
/// completely while it is hidden.
/// </summary>
public sealed class PetCat : FrameworkElement
{
    /// <summary>The island's bottom edge, in the cat's coordinates: the paws hook over it.</summary>
    public const double EdgeY = 3;

    /// <summary>Where the head is, for "is the pointer near" and for clicking it.</summary>
    public static readonly Point HeadCenter = new(30, 17);

    public static readonly DependencyProperty BreathProperty = Register(nameof(Breath));
    public static readonly DependencyProperty EyeOpenProperty = Register(nameof(EyeOpen));
    public static readonly DependencyProperty HappyProperty = Register(nameof(Happy));
    public static readonly DependencyProperty PupilXProperty = Register(nameof(PupilX));
    public static readonly DependencyProperty HeadTiltProperty = Register(nameof(HeadTilt));
    public static readonly DependencyProperty TailSwingProperty = Register(nameof(TailSwing));
    public static readonly DependencyProperty SnoozeProperty = Register(nameof(Snooze));
    public static readonly DependencyProperty HeartsProperty = Register(nameof(Hearts));

    private static readonly Brush Fur = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x21)));
    private static readonly Pen Outline = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5B))), 1));
    private static readonly Brush InnerEar = Frozen(new SolidColorBrush(Color.FromRgb(0x6E, 0x46, 0x52)));
    private static readonly Brush Iris = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xCF, 0x4A)));
    private static readonly Brush Pupil = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)));
    private static readonly Brush Nose = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8F, 0xA3)));
    private static readonly Brush Blush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0x6F, 0x91)));
    private static readonly Brush HeartBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x37, 0x5F)));
    private static readonly Brush SnoozeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD1, 0xD1, 0xD6)));
    private static readonly Pen LidPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xC2, 0xC2, 0xC8))), 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen MouthPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2))), 0.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen WhiskerPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x9A, 0x9A, 0xA2))), 0.6));
    private static readonly Pen ArmOutline = Frozen(new Pen(Outline.Brush, 6.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen ArmFur = Frozen(new Pen(Fur, 4.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen TailOutline = Frozen(new Pen(Outline.Brush, 4.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen TailFur = Frozen(new Pen(Fur, 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Geometry Heart = Frozen(Geometry.Parse("M0,1.3 C-2.7,-0.5 -1.7,-2.9 0,-1.4 C1.7,-2.9 2.7,-0.5 0,1.3 Z"));
    private static readonly Geometry NoseShape = Frozen(Geometry.Parse("M29,20.2 L31,20.2 L30,21.3 Z"));
    private static readonly Geometry Mouth = Frozen(Geometry.Parse("M30,21.3 Q29.2,22.7 28.1,22.1 M30,21.3 Q30.8,22.7 31.9,22.1"));
    private static readonly Typeface SnoozeFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private CatMood _mood = CatMood.Sleeping;
    private bool _active;
    private int _moodVersion;

    public double Breath { get => (double)GetValue(BreathProperty); set => SetValue(BreathProperty, value); }

    /// <summary>0 = closed (asleep), 1 = wide open.</summary>
    public double EyeOpen { get => (double)GetValue(EyeOpenProperty); set => SetValue(EyeOpenProperty, value); }

    /// <summary>Happy "^ ^" eyes (dancing, being petted).</summary>
    public double Happy { get => (double)GetValue(HappyProperty); set => SetValue(HappyProperty, value); }

    /// <summary>-1 = looking left, 1 = right.</summary>
    public double PupilX { get => (double)GetValue(PupilXProperty); set => SetValue(PupilXProperty, value); }

    public double HeadTilt { get => (double)GetValue(HeadTiltProperty); set => SetValue(HeadTiltProperty, value); }

    public double TailSwing { get => (double)GetValue(TailSwingProperty); set => SetValue(TailSwingProperty, value); }

    /// <summary>0..1 through one cycle of the drifting "z"s.</summary>
    public double Snooze { get => (double)GetValue(SnoozeProperty); set => SetValue(SnoozeProperty, value); }

    /// <summary>0 = no hearts, then 0..1 through the hearts of one pat.</summary>
    public double Hearts { get => (double)GetValue(HeartsProperty); set => SetValue(HeartsProperty, value); }

    /// <summary>On screen: runs its loops. Off: every animation stops, so a hidden cat costs nothing.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (value) ApplyMood();
            else StopLoops();
        }
    }

    public CatMood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            _mood = value;
            if (_active) ApplyMood();
        }
    }

    /// <summary>Eyes follow the pointer: -1 far left, 1 far right.</summary>
    public void LookAt(double x)
    {
        x = Math.Clamp(x, -1, 1);
        if (Math.Abs(x - PupilX) < 0.05) return;
        BeginAnimation(PupilXProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase() });
    }

    /// <summary>A pat: hearts float out and the eyes smile for a moment.</summary>
    public void Pet()
    {
        var hearts = new DoubleAnimation(0.001, 1, TimeSpan.FromMilliseconds(1300)) { FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(hearts, 40);
        BeginAnimation(HeartsProperty, hearts);

        if (_mood == CatMood.Dancing) return; // already smiling
        var smile = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        smile.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        smile.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
        BeginAnimation(HappyProperty, smile);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double breath = Breath;
        double swing = TailSwing;

        // Tail, body and arms hang behind the head.
        var tail = new StreamGeometry();
        using (var context = tail.Open())
        {
            context.BeginFigure(new Point(35, 33), isFilled: false, isClosed: false);
            context.BezierTo(new Point(41, 37.5), new Point(46 + swing * 1.5, 35), new Point(45 + swing * 3.5, 27.5 - Math.Abs(swing)), isStroked: true, isSmoothJoin: true);
        }
        dc.DrawGeometry(null, TailOutline, tail);
        dc.DrawGeometry(null, TailFur, tail);

        dc.DrawEllipse(Fur, Outline, new Point(30, 30.5), 8.5, 7.2 + breath * 0.5);

        dc.DrawLine(ArmOutline, new Point(16, 2.5), new Point(24.5, 24));
        dc.DrawLine(ArmOutline, new Point(44, 2.5), new Point(35.5, 24));
        dc.DrawLine(ArmFur, new Point(16, 2.5), new Point(24.5, 24));
        dc.DrawLine(ArmFur, new Point(44, 2.5), new Point(35.5, 24));

        // Head, tilted around the neck (resting on a shoulder while asleep, swaying when it dances).
        dc.PushTransform(new RotateTransform(HeadTilt, 30, 23));
        dc.PushTransform(new TranslateTransform(0, breath * 0.6));
        DrawEar(dc, mirror: false);
        DrawEar(dc, mirror: true);
        dc.DrawEllipse(Fur, Outline, new Point(30, 17), 11.5, 9);
        DrawEyes(dc);

        if (Happy > 0.5 && Hearts > 0)
        {
            dc.DrawEllipse(Blush, null, new Point(23.2, 20.6), 1.8, 1.2);
            dc.DrawEllipse(Blush, null, new Point(36.8, 20.6), 1.8, 1.2);
        }
        dc.DrawGeometry(Nose, null, NoseShape);
        dc.DrawGeometry(null, MouthPen, Mouth);
        dc.DrawLine(WhiskerPen, new Point(24.5, 20.8), new Point(17.5, 20));
        dc.DrawLine(WhiskerPen, new Point(24.5, 21.8), new Point(17.8, 23));
        dc.DrawLine(WhiskerPen, new Point(35.5, 20.8), new Point(42.5, 20));
        dc.DrawLine(WhiskerPen, new Point(35.5, 21.8), new Point(42.2, 23));
        dc.Pop();
        dc.Pop();

        // Front paws hooked over the island's edge, in front of everything.
        dc.DrawRoundedRectangle(Fur, Outline, new Rect(12.5, 0.5, 7, 5), 2.5, 2.5);
        dc.DrawRoundedRectangle(Fur, Outline, new Rect(40.5, 0.5, 7, 5), 2.5, 2.5);

        DrawSnooze(dc);
        DrawHearts(dc);
    }

    private static void DrawEar(DrawingContext dc, bool mirror)
    {
        double Side(double x) => mirror ? 60 - x : x;
        var ear = new StreamGeometry();
        using (var context = ear.Open())
        {
            context.BeginFigure(new Point(Side(19.8), 13), isFilled: true, isClosed: true);
            context.LineTo(new Point(Side(21.2), 4.6), isStroked: true, isSmoothJoin: true);
            context.LineTo(new Point(Side(27.2), 9.6), isStroked: true, isSmoothJoin: true);
        }
        dc.DrawGeometry(Fur, Outline, ear);

        var inner = new StreamGeometry();
        using (var context = inner.Open())
        {
            context.BeginFigure(new Point(Side(21.4), 11), isFilled: true, isClosed: true);
            context.LineTo(new Point(Side(22.1), 6.8), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(Side(25.2), 9.6), isStroked: false, isSmoothJoin: false);
        }
        dc.DrawGeometry(InnerEar, null, inner);
    }

    private void DrawEyes(DrawingContext dc)
    {
        foreach (double x in new[] { 25.6, 34.4 })
        {
            if (Happy > 0.5)
            {
                // ^ ^
                dc.DrawGeometry(null, LidPen, Arc(x, 18.6, 16, 18.6));
            }
            else if (EyeOpen < 0.15)
            {
                // Asleep: closed lids, curved down.
                dc.DrawGeometry(null, LidPen, Arc(x, 17.4, 19.4, 17.4));
            }
            else
            {
                double open = EyeOpen;
                double look = PupilX * 1.1;
                dc.DrawEllipse(Iris, null, new Point(x, 17.6), 2.5, 2.7 * open);
                dc.DrawEllipse(Pupil, null, new Point(x + look, 17.6), 0.95, 2.2 * open);
                if (open > 0.6) dc.DrawEllipse(Brushes.White, null, new Point(x + look - 0.8, 16.6), 0.5, 0.5);
            }
        }
    }

    private static Geometry Arc(double x, double y, double controlY, double endY)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(x - 2.2, y), isFilled: false, isClosed: false);
            context.QuadraticBezierTo(new Point(x, controlY), new Point(x + 2.2, endY), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Two "z"s drifting off to the side (the island is above, so they can't rise far).</summary>
    private void DrawSnooze(DrawingContext dc)
    {
        if (_mood != CatMood.Sleeping || Happy > 0.5 || EyeOpen > 0.15) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (double offset in new[] { 0.0, 0.5 })
        {
            double q = (Snooze + offset) % 1;
            double opacity = Math.Sin(q * Math.PI) * 0.85;
            if (opacity < 0.05) continue;
            var text = new FormattedText("z", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, SnoozeFace, 6 + q * 4, SnoozeBrush, dpi);
            dc.PushOpacity(opacity);
            dc.DrawText(text, new Point(43 + q * 10, 12 - q * 8));
            dc.Pop();
        }
    }

    /// <summary>Three hearts floating out sideways and down from the head, growing and fading.</summary>
    private void DrawHearts(DrawingContext dc)
    {
        double h = Hearts;
        if (h <= 0) return;
        double opacity = h < 0.15 ? h / 0.15 : (1 - h) / 0.85;
        double scale = 1 + 0.8 * h;
        (Point From, Point To)[] paths =
        [
            (new Point(24, 22), new Point(10, 30)),
            (new Point(30, 25), new Point(30, 37)),
            (new Point(36, 22), new Point(50, 30)),
        ];
        dc.PushOpacity(opacity);
        foreach (var (from, to) in paths)
        {
            var at = new Point(from.X + (to.X - from.X) * h, from.Y + (to.Y - from.Y) * h);
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, at.X, at.Y));
            dc.DrawGeometry(HeartBrush, null, Heart);
            dc.Pop();
        }
        dc.Pop();
    }

    /// <summary>Starts the loops for the current mood: slow and few frames asleep, livelier awake, on the beat when dancing.</summary>
    private void ApplyMood()
    {
        bool asleep = _mood == CatMood.Sleeping;
        bool dancing = _mood == CatMood.Dancing;

        Loop(BreathProperty, 0, 1, asleep ? 1.7 : 1.1, fps: 12);
        Loop(TailSwingProperty, asleep ? -0.25 : -1, asleep ? 0.25 : 1, dancing ? 0.48 : asleep ? 2.4 : 1.5, fps: dancing ? 24 : 12);

        if (asleep)
        {
            var snooze = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.8)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(snooze, 12);
            BeginAnimation(SnoozeProperty, snooze);
        }
        else
        {
            BeginAnimation(SnoozeProperty, null);
        }

        if (dancing)
        {
            Loop(HeadTiltProperty, -9, 9, 0.48, fps: 24);
        }
        else
        {
            // Asleep the head rests on a shoulder.
            BeginAnimation(HeadTiltProperty, new DoubleAnimation(asleep ? 8 : 0, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase() });
        }

        BeginAnimation(HappyProperty, null);
        Happy = dancing ? 1 : 0;

        int version = ++_moodVersion;
        if (_mood == CatMood.Awake)
        {
            // Open the eyes, then blink now and then.
            var open = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220));
            open.Completed += (_, _) =>
            {
                if (version != _moodVersion || !_active) return;
                EyeOpen = 1;
                var blink = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(3.8) };
                blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3500))));
                blink.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3580))));
                blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3680))));
                Timeline.SetDesiredFrameRate(blink, 24);
                BeginAnimation(EyeOpenProperty, blink);
            };
            BeginAnimation(EyeOpenProperty, open);
        }
        else
        {
            EyeOpen = 0;
            BeginAnimation(EyeOpenProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(asleep ? 600 : 150)));
            BeginAnimation(PupilXProperty, null);
            PupilX = 0;
        }
    }

    private void StopLoops()
    {
        _moodVersion++;
        foreach (var property in new[] { BreathProperty, TailSwingProperty, SnoozeProperty, HeadTiltProperty, EyeOpenProperty, HappyProperty, HeartsProperty, PupilXProperty })
            BeginAnimation(property, null);
    }

    private void Loop(DependencyProperty property, double from, double to, double seconds, int fps)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(animation, fps);
        BeginAnimation(property, animation);
    }

    private static DependencyProperty Register(string name) => DependencyProperty.Register(
        name, typeof(double), typeof(PetCat), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
