using System.Globalization;
using System.Media;
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
        Compact,
        Activity,
        ExpandedMedia,
        ExpandedClock,
        ExpandedTimer,
    }

    private enum ActivityKind
    {
        Volume,
        Power,
        Track,
        Device,
        Privacy,
        TimerDone,
    }

    /// <summary>How long the compact "now playing" stays after pausing before the island goes idle.</summary>
    private static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(45);

    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly PowerMonitor _power = new();
    private readonly TimerService _timer = new();
    private readonly MediaService _media;
    private readonly IslandAnimator _animator;
    private VolumeService? _volume;
    private PrivacyMonitor? _privacy;
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
    private string? _microphoneApp;
    private string? _cameraApp;

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
        _activityTimer.Tick += (_, _) => EndActivity();
        _graceTimer.Tick += (_, _) =>
        {
            _graceTimer.Stop();
            ApplyState();
        };
        _liveTimer.Tick += (_, _) => RefreshLiveContent();

        _media.Changed += OnMediaChanged;
        _media.TrackChanged += OnTrackChanged;
        _timer.Changed += OnTimerChanged;
        _timer.Finished += OnTimerFinished;

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
        _privacy?.Dispose();
        _shell?.Dispose();
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
        ApplyState();

        // Each source is optional: if one is unavailable on this PC the rest keeps working.
        try
        {
            _volume = new VolumeService(Dispatcher);
            _volume.VolumeChanged += OnVolumeChanged;
            _volume.DeviceChanged += OnAudioDeviceChanged;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }

        try
        {
            _privacy = new PrivacyMonitor(Dispatcher);
            _privacy.Changed += OnPrivacyChanged;
            _microphoneApp = _privacy.MicrophoneApp;
            _cameraApp = _privacy.CameraApp;
            UpdatePrivacyDots();
            ApplyState();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
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

    private bool ShowsPrivacyDots => _settings.ShowPrivacy && (_microphoneApp is not null || _cameraApp is not null);

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        ApplyState();
    }

    /// <summary>Single place that decides what the island shows; call it after any state change.</summary>
    private void ApplyState()
    {
        View view =
            _activity == ActivityKind.TimerDone ? View.Activity // an alarm beats everything, even hover
            : _expanded ? (_timer.IsActive ? View.ExpandedTimer : _media.HasSession ? View.ExpandedMedia : View.ExpandedClock)
            : _activity is not null ? View.Activity
            : _timer.IsActive || ShowsCompactMedia ? View.Compact
            : View.Idle;

        if (view == View.Compact) UpdateCompactSlots();

        var (width, height, radius) = SizeOf(view);
        _animator.AnimateTo(width, height, radius);
        SwitchTo(view, width, height);
    }

    private (double Width, double Height, double Radius) SizeOf(View view)
    {
        bool notch = _settings.Style == IslandStyle.Notch;
        switch (view)
        {
            case View.Compact:
                double width = ShowsCompactMedia ? 250 : 200;
                return notch ? (width + 10, 32, 12) : (width, 32, 16);
            case View.Activity:
                return (_activitySize.Width, _activitySize.Height, notch ? 14 : _activitySize.Height / 2);
            case View.ExpandedClock:
                return (360, 132, notch ? 26 : 36);
            case View.ExpandedMedia:
                return (400, _media.HasTimeline ? 176 : 150, notch ? 28 : 40);
            case View.ExpandedTimer:
                return _media.HasSession ? (380, 156, notch ? 28 : 40) : (380, 100, notch ? 26 : 36);
            default:
                if (notch) return (180, 30, 10);
                return ShowsPrivacyDots ? (140, 32, 16) : (120, 32, 16);
        }
    }

    private void SwitchTo(View view, double width, double height)
    {
        FrameworkElement element = view switch
        {
            View.Compact => CompactView,
            View.Activity => ActivityView,
            View.ExpandedMedia => MediaView,
            View.ExpandedClock => ClockView,
            View.ExpandedTimer => TimerView,
            _ => IdleView,
        };

        // Content is laid out at its final size up front, so nothing reflows while the shape morphs.
        element.Width = width;
        element.Height = height;

        var previous = _viewElement;
        _view = view;
        _viewElement = element;

        if (!ReferenceEquals(previous, element))
        {
            if (previous is not null) FadeOut(previous);
            FadeIn(element);
        }

        RefreshLiveContent();

        // The only periodic work in the app, and only while something on screen is counting.
        bool countdown = _timer.IsActive && !_timer.IsPaused && view is View.Compact or View.ExpandedTimer;
        TimeSpan? interval =
            countdown ? TimeSpan.FromMilliseconds(250)
            : view == View.ExpandedMedia && _media.HasTimeline && _media.IsPlaying ? TimeSpan.FromMilliseconds(500)
            : view == View.ExpandedClock ? TimeSpan.FromSeconds(1)
            : null;

        if (interval is { } value)
        {
            if (_liveTimer.Interval != value) _liveTimer.Interval = value;
            _liveTimer.Start();
        }
        else
        {
            _liveTimer.Stop();
        }
    }

    private void RefreshLiveContent()
    {
        if (_timer.IsActive) UpdateTimerContent();
        if (_view == View.ExpandedClock) UpdateClock();
        else if (_view == View.ExpandedMedia) UpdateTimeline();
    }

    /// <summary>Compact island: left = artwork or timer ring, right = equalizer and/or countdown.</summary>
    private void UpdateCompactSlots()
    {
        bool media = ShowsCompactMedia;
        bool timer = _timer.IsActive;
        CompactArt.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        CompactRing.Visibility = !media && timer ? Visibility.Visible : Visibility.Collapsed;
        CompactEq.Visibility = media && !timer ? Visibility.Visible : Visibility.Collapsed;
        CompactTimerText.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
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
        // A finished timer stays on screen until it is dismissed or times out; others wait their turn.
        if (_activity == ActivityKind.TimerDone && kind != ActivityKind.TimerDone) return;

        bool switching = _activity != kind;
        foreach (var candidate in new FrameworkElement[] { VolumePanel, PowerPanel, TrackPanel, InfoPanel })
            candidate.Visibility = ReferenceEquals(candidate, panel) ? Visibility.Visible : Visibility.Collapsed;
        if (switching && _view == View.Activity) FadeIn(panel);

        _activity = kind;
        _activitySize = new Size(width, height);
        _activityTimer.Stop();
        _activityTimer.Interval = duration;
        _activityTimer.Start();
        ApplyState();
    }

    private void EndActivity()
    {
        _activityTimer.Stop();
        _activity = null;
        InfoIcon.BeginAnimation(OpacityProperty, null);
        ApplyState();
    }

    /// <summary>Icon + title (+ subtitle) notification. Icons come from Theme.xaml as fill / stroke parts.</summary>
    private void ShowInfo(ActivityKind kind, string? fillIcon, string? strokeIcon, Brush accent,
        string title, string? subtitle, double width, TimeSpan duration)
    {
        if (_activity == ActivityKind.TimerDone && kind != ActivityKind.TimerDone) return;

        InfoIconFill.Data = fillIcon is null ? null : (Geometry)FindResource(fillIcon);
        InfoIconFill.Fill = accent;
        InfoIconStroke.Data = strokeIcon is null ? null : (Geometry)FindResource(strokeIcon);
        InfoIconStroke.Stroke = accent;
        InfoIcon.BeginAnimation(OpacityProperty, null);

        InfoTitle.Text = title;
        InfoSubtitle.Text = subtitle ?? "";
        InfoSubtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;

        ShowActivity(kind, InfoPanel, width, string.IsNullOrEmpty(subtitle) ? 36 : 52, duration);
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
            SetArtwork(MiniArt);
        }

        if (_accent != _media.Accent)
        {
            _accent = _media.Accent;
            var brush = Frozen(new SolidColorBrush(_accent));
            CompactEq.BarBrush = MediaEq.BarBrush = TrackEq.BarBrush = brush;
        }
        CompactEq.IsPlaying = MediaEq.IsPlaying = TrackEq.IsPlaying = _media.IsPlaying;

        MediaTitle.Text = MiniTitle.Text = _media.Title;
        MediaArtist.Text = MiniArtist.Text = _media.Artist;
        if (_activity == ActivityKind.Track) FillTrackPanel();

        var playPause = (Geometry)FindResource(_media.IsPlaying ? "IconPause" : "IconPlay");
        PlayPauseIcon.Data = MiniPlayPauseIcon.Data = playPause;
        PreviousButton.IsEnabled = MiniPreviousButton.IsEnabled = _media.CanGoPrevious;
        NextButton.IsEnabled = MiniNextButton.IsEnabled = _media.CanGoNext;
        MediaTimeline.Visibility = _media.HasTimeline ? Visibility.Visible : Visibility.Collapsed;
        MiniPlayer.Visibility = _media.HasSession ? Visibility.Visible : Visibility.Collapsed;
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

    // ───────────────────────────── timer ─────────────────────────────

    private void OnTimerChanged()
    {
        UpdateTimerContent();
        ApplyState();
    }

    private void OnTimerFinished(TimeSpan total)
    {
        SystemSounds.Exclamation.Play();
        ShowInfo(ActivityKind.TimerDone, null, "IconStopwatch", (Brush)FindResource("TimerBrush"),
            "Waktu habis!", $"Timer {FormatDuration(total)} selesai · klik untuk menutup", 330, TimeSpan.FromSeconds(12));

        // Gentle pulse so it catches the eye; finite, so it stops costing anything afterwards.
        InfoIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(450))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(6),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void UpdateTimerContent()
    {
        int seconds = (int)Math.Ceiling(_timer.Remaining.TotalSeconds);
        double total = _timer.Total.TotalSeconds;
        string text = FormatCountdown(seconds);

        // Whole seconds only, so the ring and text change (and re-render) at most once a second.
        CompactTimerText.Text = TimerText.Text = text;
        CompactRing.Progress = TimerRing.Progress = total > 0 ? Math.Clamp(seconds / total, 0, 1) : 0;
        TimerLabel.Text = _timer.IsPaused ? "Dijeda" : $"Timer {FormatDuration(_timer.Total)}";
        TimerText.Opacity = CompactTimerText.Opacity = _timer.IsPaused ? 0.55 : 1;
        TimerPauseIcon.Data = (Geometry)FindResource(_timer.IsPaused ? "IconPlay" : "IconPause");
    }

    private void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int minutes))
            _timer.Start(TimeSpan.FromMinutes(minutes));
    }

    private void TimerPause_Click(object sender, RoutedEventArgs e) => _timer.TogglePause();

    private void TimerAdd_Click(object sender, RoutedEventArgs e) => _timer.Add(TimeSpan.FromMinutes(1));

    private void TimerStop_Click(object sender, RoutedEventArgs e) => _timer.Stop();

    // ───────────────────────────── volume, devices & privacy ─────────────────────────────

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

    private void OnAudioDeviceChanged(string name, bool worn)
    {
        if (!_settings.ShowDeviceChanges || _hiddenForFullscreen) return;

        ShowInfo(ActivityKind.Device,
            worn ? "IconHeadphoneCups" : "IconSpeaker",
            worn ? "IconHeadphoneBand" : "IconSpeakerWaves",
            (Brush)FindResource("PrimaryText"),
            name, "Output audio", 330, TimeSpan.FromSeconds(2.6));
    }

    private void OnPrivacyChanged()
    {
        if (_privacy is null) return;

        bool cameraStarted = _privacy.CameraApp is not null && _cameraApp is null;
        bool microphoneStarted = _privacy.MicrophoneApp is not null && _microphoneApp is null;
        _microphoneApp = _privacy.MicrophoneApp;
        _cameraApp = _privacy.CameraApp;
        UpdatePrivacyDots();

        if (_settings.ShowPrivacy && !_hiddenForFullscreen && (cameraStarted || microphoneStarted))
        {
            if (cameraStarted)
                ShowInfo(ActivityKind.Privacy, "IconCamera", null, (Brush)FindResource("CameraBrush"),
                    "Kamera aktif", _cameraApp, 300, TimeSpan.FromSeconds(2.6));
            else
                ShowInfo(ActivityKind.Privacy, "IconMicBody", "IconMicStand", (Brush)FindResource("MicBrush"),
                    "Mikrofon aktif", _microphoneApp, 300, TimeSpan.FromSeconds(2.6));
            return;
        }

        ApplyState(); // the idle pill widens / narrows for the dots
    }

    private void UpdatePrivacyDots()
    {
        var microphone = _settings.ShowPrivacy && _microphoneApp is not null ? Visibility.Visible : Visibility.Collapsed;
        var camera = _settings.ShowPrivacy && _cameraApp is not null ? Visibility.Visible : Visibility.Collapsed;
        IdleMicDot.Visibility = CompactMicDot.Visibility = microphone;
        IdleCameraDot.Visibility = CompactCameraDot.Visibility = camera;
    }

    // ───────────────────────────── power ─────────────────────────────

    private void OnPowerChanged()
    {
        _timer.Resync(); // also covers waking up from sleep

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
            PowerChange.SaverOn => "Penghemat baterai aktif",
            PowerChange.SaverOff => "Penghemat baterai mati",
            _ => "Baterai lemah",
        };
        FillPowerPanel(state);
        ShowActivity(ActivityKind.Power, PowerPanel, 300, 36, TimeSpan.FromSeconds(low ? 4 : 2.6));
    }

    private void FillPowerPanel(PowerState state)
    {
        bool low = _batteryLow && !state.PluggedIn;
        var primary = (Brush)FindResource("PrimaryText");
        var accent = state.PluggedIn ? (Brush)FindResource("ChargingBrush")
            : state.Saver ? (Brush)FindResource("SaverBrush")
            : low ? (Brush)FindResource("WarningBrush")
            : primary;
        PowerTitle.Foreground = low ? accent : primary;
        PowerPercent.Text = $"{state.Percent}%";
        PowerPercent.Foreground = accent;
        PowerBattery.Level = state.Percent / 100.0;
        PowerBattery.IsCharging = state.PluggedIn;
        PowerBattery.IsSaver = state.Saver;
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
        ClockBatteryIcon.IsSaver = power.Saver;
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
        _hoverTimer.Stop();

        // Clicking a finished timer dismisses the alarm.
        if (_activity == ActivityKind.TimerDone)
        {
            EndActivity();
            return;
        }

        // Otherwise a click expands right away (handy for touch screens and pens).
        SetExpanded(true);
    }

    // ───────────────────────────── settings menu ─────────────────────────────

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        MenuTimerStop.IsEnabled = _timer.IsActive;
        MenuIsland.IsChecked = _settings.Style == IslandStyle.Island;
        MenuNotch.IsChecked = _settings.Style == IslandStyle.Notch;
        MenuVolume.IsChecked = _settings.ShowVolume;
        MenuTracks.IsChecked = _settings.ShowTrackChanges;
        MenuDevices.IsChecked = _settings.ShowDeviceChanges;
        MenuPrivacy.IsChecked = _settings.ShowPrivacy;
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

    private void MenuDevices_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowDeviceChanges = !_settings.ShowDeviceChanges;
        _settings.Save();
    }

    private void MenuPrivacy_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowPrivacy = !_settings.ShowPrivacy;
        _settings.Save();
        UpdatePrivacyDots();
        ApplyState();
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

    /// <summary>1499 → "24:59", 3600 → "1:00:00".</summary>
    private static string FormatCountdown(int seconds) =>
        seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";

    /// <summary>25 min → "25 menit", 75 min → "1 jam 15 menit".</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        int minutes = (int)Math.Round(duration.TotalMinutes);
        if (minutes < 60) return $"{minutes} menit";
        return minutes % 60 == 0 ? $"{minutes / 60} jam" : $"{minutes / 60} jam {minutes % 60} menit";
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
