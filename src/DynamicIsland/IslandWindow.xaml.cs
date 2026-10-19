using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Animation;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland;

public partial class IslandWindow : Window
{
    private enum View
    {
        Idle,
        CompactMedia,
        Activity,
        ExpandedMedia,
        ExpandedClock,
    }

    private enum ActivityKind
    {
        Volume,
        Power,
        Track,
    }

    /// <summary>How long the compact "now playing" stays after pausing before the island goes idle.</summary>
    private static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(45);

    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly PowerMonitor _power = new();
    private readonly MediaService _media;
    private readonly IslandAnimator _animator;
    private VolumeService? _volume;
    private ShellHooks? _shell;
    private nint _hwnd;

    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(170) };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly DispatcherTimer _activityTimer = new();
    private readonly DispatcherTimer _graceTimer = new() { Interval = PauseGrace };
    private readonly DispatcherTimer _liveTimer = new();

    private View _view = View.Idle;
    private FrameworkElement? _viewElement;
    private ActivityKind? _activity;
    private Size _activitySize;
    private bool _expanded;
    private bool _hiddenForFullscreen;
    private bool _wasPlaying;
    private bool _batteryLow;
    private DateTime _pausedAt = DateTime.MinValue;

    private BitmapSource? _artSource;
    private Brush? _artBrush;
    private Color _accent;

    public IslandWindow()
    {
        InitializeComponent();

        _media = new MediaService(Dispatcher);
        _animator = new IslandAnimator(Island, Stage);
        ApplyStyle();
        _animator.Snap(60, 12, 6); // boots from a tiny dot

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            SetExpanded(true);
        };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            SetExpanded(false);
        };
        _activityTimer.Tick += (_, _) =>
        {
            _activityTimer.Stop();
            _activity = null;
            ApplyState();
        };
        _graceTimer.Tick += (_, _) =>
        {
            _graceTimer.Stop();
            ApplyState();
        };
        _liveTimer.Tick += (_, _) => RefreshLiveContent();

        _media.Changed += OnMediaChanged;
        _media.TrackChanged += OnTrackChanged;

        Loaded += OnLoaded;
    }

    // ───────────────────────────── window plumbing ─────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);

        _shell = new ShellHooks(_hwnd);
        _shell.FullscreenChanged += OnFullscreenChanged;
        _shell.PowerChanged += OnPowerChanged;
        _shell.DisplayChanged += () => Dispatcher.InvokeAsync(PositionWindow, DispatcherPriority.Background);
        HwndSource.FromHwnd(_hwnd)?.AddHook(_shell.WndProc);

        PositionWindow();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(PositionWindow, DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e)
    {
        _media.Dispose();
        _volume?.Dispose();
        _shell?.Dispose();
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
        ApplyState();

        try
        {
            _volume = new VolumeService(Dispatcher);
            _volume.VolumeChanged += OnVolumeChanged;
        }
        catch (Exception ex)
        {
            Log.Error(ex); // no audio stack (e.g. some VMs): the island still works without it
        }

        await _media.StartAsync();
    }

    /// <summary>
    /// Centres the window on the primary monitor, at the top of its work area (below the taskbar
    /// when the taskbar sits at the top). Physical pixels, so it is right at any scaling.
    /// </summary>
    private void PositionWindow()
    {
        if (_hwnd == 0 || !Native.TryGetPrimaryMonitor(out var monitor, out var workArea) || !Native.GetWindowRect(_hwnd, out var rect))
            return;

        int width = rect.Right - rect.Left;
        int x = monitor.Left + (monitor.Right - monitor.Left - width) / 2;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, workArea.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void OnFullscreenChanged(bool fullscreen)
    {
        // Get out of the way of games, videos and presentations on our monitor.
        bool hide = fullscreen && Native.IsForegroundFullscreen(_hwnd);
        if (hide == _hiddenForFullscreen) return;
        _hiddenForFullscreen = hide;

        if (hide)
        {
            _hoverTimer.Stop();
            _leaveTimer.Stop();
            _expanded = false;
            Hide();
        }
        else
        {
            Show();
            Native.BringToTopmost(_hwnd);
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
            ApplyState();
        }
    }

    // ───────────────────────────── state machine ─────────────────────────────

    private bool ShowsCompactMedia =>
        _media.HasSession && (_media.IsPlaying || DateTime.UtcNow - _pausedAt < PauseGrace);

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        ApplyState();
    }

    /// <summary>Single place that decides what the island shows; call it after any state change.</summary>
    private void ApplyState()
    {
        View view = _expanded ? (_media.HasSession ? View.ExpandedMedia : View.ExpandedClock)
            : _activity is not null ? View.Activity
            : ShowsCompactMedia ? View.CompactMedia
            : View.Idle;

        var (width, height, radius) = SizeOf(view);
        _animator.AnimateTo(width, height, radius);
        SwitchTo(view, width, height);
    }

    private (double Width, double Height, double Radius) SizeOf(View view)
    {
        bool notch = _settings.Style == IslandStyle.Notch;
        return view switch
        {
            View.CompactMedia => notch ? (260, 32, 12) : (250, 32, 16),
            View.Activity => (_activitySize.Width, _activitySize.Height, notch ? 14 : _activitySize.Height / 2),
            View.ExpandedClock => (340, 88, notch ? 24 : 32),
            View.ExpandedMedia => (400, _media.HasTimeline ? 176 : 150, notch ? 28 : 40),
            _ => notch ? (180, 30, 10) : (120, 32, 16),
        };
    }

    private void SwitchTo(View view, double width, double height)
    {
        FrameworkElement? element = view switch
        {
            View.CompactMedia => CompactMediaView,
            View.Activity => ActivityView,
            View.ExpandedMedia => MediaView,
            View.ExpandedClock => ClockView,
            _ => null,
        };

        // Content is laid out at its final size up front, so nothing reflows while the shape morphs.
        if (element is not null)
        {
            element.Width = width;
            element.Height = height;
        }

        var previous = _viewElement;
        _view = view;
        _viewElement = element;

        if (!ReferenceEquals(previous, element))
        {
            if (previous is not null) FadeOut(previous);
            if (element is not null) FadeIn(element);
        }

        RefreshLiveContent();

        // The only periodic work in the app, and only while an expanded view is on screen.
        bool live = view == View.ExpandedClock || (view == View.ExpandedMedia && _media.HasTimeline && _media.IsPlaying);
        if (live)
        {
            var interval = TimeSpan.FromMilliseconds(view == View.ExpandedClock ? 1000 : 500);
            if (_liveTimer.Interval != interval) _liveTimer.Interval = interval;
            _liveTimer.Start();
        }
        else
        {
            _liveTimer.Stop();
        }
    }

    private void RefreshLiveContent()
    {
        if (_view == View.ExpandedClock) UpdateClock();
        else if (_view == View.ExpandedMedia) UpdateTimeline();
    }

    private void FadeIn(FrameworkElement element)
    {
        double from = element.IsVisible ? element.Opacity : 0;
        element.Visibility = Visibility.Visible;
        element.IsHitTestVisible = true;

        if (element.RenderTransform is not ScaleTransform scale)
        {
            element.RenderTransformOrigin = new Point(0.5, 0);
            element.RenderTransform = scale = new ScaleTransform();
        }

        // Wait a beat so the shape has started growing, then fade and settle in.
        element.BeginAnimation(OpacityProperty, DelayedEase(from, 1, 70, 240));
        var grow = DelayedEase(0.94, 1, 70, 320);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void FadeOut(FrameworkElement element)
    {
        element.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(element, _viewElement)) element.Visibility = Visibility.Collapsed;
        };
        element.BeginAnimation(OpacityProperty, fade);
    }

    private static DoubleAnimationUsingKeyFrames DelayedEase(double from, double to, int delayMs, int durationMs)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + durationMs)), Ease));
        animation.Freeze();
        return animation;
    }

    private void ShowActivity(ActivityKind kind, FrameworkElement panel, double width, double height, TimeSpan duration)
    {
        bool switching = _activity != kind;
        foreach (var candidate in new FrameworkElement[] { VolumePanel, PowerPanel, TrackPanel })
            candidate.Visibility = ReferenceEquals(candidate, panel) ? Visibility.Visible : Visibility.Collapsed;
        if (switching && _view == View.Activity) FadeIn(panel);

        _activity = kind;
        _activitySize = new Size(width, height);
        _activityTimer.Stop();
        _activityTimer.Interval = duration;
        _activityTimer.Start();
        ApplyState();
    }

    // ───────────────────────────── media ─────────────────────────────

    private void OnMediaChanged()
    {
        if (_wasPlaying && !_media.IsPlaying)
        {
            _pausedAt = DateTime.UtcNow;
            _graceTimer.Stop();
            _graceTimer.Start();
        }
        else if (_media.IsPlaying)
        {
            _graceTimer.Stop();
        }
        _wasPlaying = _media.IsPlaying;

        UpdateMediaContent();
        ApplyState();
    }

    private void OnTrackChanged()
    {
        if (!_settings.ShowTrackChanges || _expanded) return;

        FillTrackPanel();
        ShowActivity(ActivityKind.Track, TrackPanel, 340, 52, TimeSpan.FromSeconds(3.2));
    }

    private void UpdateMediaContent()
    {
        if (!ReferenceEquals(_artSource, _media.Art))
        {
            _artSource = _media.Art;
            _artBrush = _artSource is null ? null : Frozen(new ImageBrush(_artSource) { Stretch = Stretch.UniformToFill });
            SetArtwork(CompactArt);
            SetArtwork(MediaArt);
            SetArtwork(TrackArt);
        }

        if (_accent != _media.Accent)
        {
            _accent = _media.Accent;
            var brush = Frozen(new SolidColorBrush(_accent));
            CompactEq.BarBrush = MediaEq.BarBrush = TrackEq.BarBrush = brush;
        }
        CompactEq.IsPlaying = MediaEq.IsPlaying = TrackEq.IsPlaying = _media.IsPlaying;

        MediaTitle.Text = _media.Title;
        MediaArtist.Text = _media.Artist;
        if (_activity == ActivityKind.Track) FillTrackPanel();

        PlayPauseIcon.Data = (Geometry)FindResource(_media.IsPlaying ? "IconPause" : "IconPlay");
        PreviousButton.IsEnabled = _media.CanGoPrevious;
        NextButton.IsEnabled = _media.CanGoNext;
        MediaTimeline.Visibility = _media.HasTimeline ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FillTrackPanel()
    {
        TrackTitle.Text = _media.Title;
        TrackArtist.Text = _media.Artist;
        TrackArtist.Visibility = _media.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetArtwork(Border target)
    {
        target.Background = _artBrush ?? (Brush)FindResource("ArtPlaceholder");
        if (target.Child is UIElement glyph)
            glyph.Visibility = _artBrush is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTimeline()
    {
        if (!_media.HasTimeline) return;

        var duration = _media.Duration;
        var position = _media.Position;
        SetProgress(MediaProgressDone, MediaProgressRest, position.TotalSeconds / duration.TotalSeconds);
        MediaElapsed.Text = FormatTime(position);
        MediaRemaining.Text = "-" + FormatTime(duration - position);
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => _media.Previous();

    private void Next_Click(object sender, RoutedEventArgs e) => _media.Next();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => _media.TogglePlayPause();

    private void MediaTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (MediaTrack.ActualWidth <= 0) return;
        _media.Seek(e.GetPosition(MediaTrack).X / MediaTrack.ActualWidth);
    }

    // ───────────────────────────── volume & power ─────────────────────────────

    private void OnVolumeChanged(float level, bool muted)
    {
        if (!_settings.ShowVolume || _hiddenForFullscreen) return;

        bool silent = muted || level < 0.005f;
        WaveSmall.Visibility = silent ? Visibility.Collapsed : Visibility.Visible;
        WaveLarge.Visibility = !silent && level > 0.5f ? Visibility.Visible : Visibility.Collapsed;
        MuteMark.Visibility = silent ? Visibility.Visible : Visibility.Collapsed;
        VolumeFill.Opacity = muted ? 0.4 : 1;
        SetProgress(VolumeDone, VolumeRest, level);
        VolumeText.Text = muted ? "–" : Math.Round(level * 100).ToString(CultureInfo.CurrentCulture);

        ShowActivity(ActivityKind.Volume, VolumePanel, 300, 36, TimeSpan.FromSeconds(1.6));
    }

    private void OnPowerChanged()
    {
        var change = _power.Update();
        var state = _power.State;

        if (_view == View.ExpandedClock) UpdateClock();
        if (_activity == ActivityKind.Power) FillPowerPanel(state);
        if (change is PowerChange.None or PowerChange.PercentChanged || _hiddenForFullscreen) return;

        bool low = _batteryLow = change == PowerChange.BatteryLow;
        PowerTitle.Text = change switch
        {
            PowerChange.PluggedIn => state.Percent >= 100 ? "Terisi penuh" : "Mengisi daya",
            PowerChange.Unplugged => "Menggunakan baterai",
            _ => "Baterai lemah",
        };
        FillPowerPanel(state);
        ShowActivity(ActivityKind.Power, PowerPanel, 300, 36, TimeSpan.FromSeconds(low ? 4 : 2.6));
    }

    private void FillPowerPanel(PowerState state)
    {
        bool low = _batteryLow && !state.PluggedIn;
        var accent = state.PluggedIn ? (Brush)FindResource("ChargingBrush")
            : low ? (Brush)FindResource("WarningBrush")
            : (Brush)FindResource("PrimaryText");
        PowerTitle.Foreground = low ? accent : (Brush)FindResource("PrimaryText");
        PowerPercent.Text = $"{state.Percent}%";
        PowerPercent.Foreground = accent;
        PowerBattery.Level = state.Percent / 100.0;
        PowerBattery.IsCharging = state.PluggedIn;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        ClockTime.Text = now.ToString("t", culture);
        ClockDate.Text = now.ToString("dddd, d MMMM", culture);

        var power = _power.State;
        ClockBattery.Visibility = power.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        ClockBatteryText.Text = $"{power.Percent}%";
        ClockBatteryIcon.Level = power.Percent / 100.0;
        ClockBatteryIcon.IsCharging = power.PluggedIn;
    }

    // ───────────────────────────── mouse ─────────────────────────────

    private void Shape_MouseEnter(object sender, MouseEventArgs e)
    {
        _leaveTimer.Stop();
        if (!_expanded) _hoverTimer.Start();
    }

    private void Shape_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        if (_expanded) _leaveTimer.Start();
    }

    private void Shape_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Click expands right away (handy for touch screens and pens).
        _hoverTimer.Stop();
        SetExpanded(true);
    }

    // ───────────────────────────── settings menu ─────────────────────────────

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        MenuIsland.IsChecked = _settings.Style == IslandStyle.Island;
        MenuNotch.IsChecked = _settings.Style == IslandStyle.Notch;
        MenuVolume.IsChecked = _settings.ShowVolume;
        MenuTracks.IsChecked = _settings.ShowTrackChanges;
        MenuStartup.IsChecked = AppSettings.StartWithWindows;
    }

    private void MenuIsland_Click(object sender, RoutedEventArgs e) => SetStyle(IslandStyle.Island);

    private void MenuNotch_Click(object sender, RoutedEventArgs e) => SetStyle(IslandStyle.Notch);

    private void MenuVolume_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowVolume = !_settings.ShowVolume;
        _settings.Save();
    }

    private void MenuTracks_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowTrackChanges = !_settings.ShowTrackChanges;
        _settings.Save();
    }

    private void MenuStartup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppSettings.StartWithWindows = !AppSettings.StartWithWindows;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        Close();
        Application.Current.Shutdown();
    }

    private void SetStyle(IslandStyle style)
    {
        _settings.Style = style;
        _settings.Save();
        ApplyStyle();
        ApplyState();
    }

    private void ApplyStyle()
    {
        bool notch = _settings.Style == IslandStyle.Notch;
        Shape.Margin = new Thickness(0, notch ? 0 : 8, 0, 0);
        ShoulderLeft.Visibility = ShoulderRight.Visibility = notch ? Visibility.Visible : Visibility.Collapsed;
        _animator.SquareTopCorners = notch;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static void SetProgress(ColumnDefinition done, ColumnDefinition rest, double fraction)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        done.Width = new GridLength(fraction, GridUnitType.Star);
        rest.Width = new GridLength(1 - fraction, GridUnitType.Star);
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
