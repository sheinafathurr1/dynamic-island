using System.Diagnostics;
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
        Expanded,
    }

    /// <summary>Pages of the expanded carousel, in display order.</summary>
    private enum Page
    {
        Call,
        Media,
        Lyrics,
        Timer,
        Today,
        Controls,
        System,
        Shelf,
    }

    private enum LyricsStatus
    {
        None,
        Loading,
        Ready,
        Instrumental,
        NotFound,
        Offline,
    }

    private enum ActivityKind
    {
        Volume,
        Power,
        Track,
        Device,
        Privacy,
        TimerDone,
        Notification,
        File,
    }

    /// <summary>How long the compact "now playing" stays after pausing before the island goes idle.</summary>
    private static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(45);

    private const double PageWidth = 400;
    private const double IslandTopMargin = 8;
    private const double DotsSpace = 16;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int MaxShelfTiles = 5;
    private const float VolumeStep = 0.02f; // per wheel notch, same as the keyboard volume keys

    /// <summary>Show each lyric line a hair early so it appears together with the voice.</summary>
    private static readonly TimeSpan LyricLead = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan NotificationDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FileDuration = TimeSpan.FromSeconds(6);

    /// <summary>One round switch on the controls page and the parts that show its state.</summary>
    private sealed record QuickControl(Button Button, System.Windows.Shapes.Path Slash, TextBlock State);

    /// <summary>Silence this long while "playing" (muted player, music on another output) hands the bars back to the canned loop.</summary>
    private static readonly TimeSpan LiveAudioGrace = TimeSpan.FromSeconds(2.5);

    /// <summary>After a page turn, further wheel / touchpad input is ignored until it pauses this long.</summary>
    private static readonly TimeSpan ScrollLock = TimeSpan.FromMilliseconds(220);

    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly PowerMonitor _power = new();
    private readonly TimerService _timer = new();
    private readonly ShelfService _shelf = new();
    private readonly LyricsService _lyricsService = new();
    private readonly AudioSpectrum _spectrum = new();
    private readonly CalendarService _calendar = new();
    private readonly WeatherService _weather = new();
    private readonly SystemStats _stats = new();
    private readonly QuickControls _quick;
    private readonly MediaService _media;
    private readonly IslandAnimator _animator;
    private readonly SpringValue _strip;
    private readonly TranslateTransform _stripOffset = new();
    private readonly SpringValue _lift;
    private readonly TranslateTransform _liftOffset = new();
    private readonly BannerSwitch _banners;
    private VolumeService? _volume;
    private PrivacyMonitor? _privacy;
    private ShellHooks? _shell;
    private NotificationWatcher? _notifications;
    private ScreenshotWatcher? _screenshots;
    private DownloadWatcher? _downloads;
    private CalendarDialog? _calendarDialog;
    private SettingsWindow? _settingsWindow;
    private nint _hwnd;

    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly DispatcherTimer _activityTimer = new();
    private readonly DispatcherTimer _graceTimer = new() { Interval = PauseGrace };
    private readonly DispatcherTimer _liveTimer = new();
    private readonly DispatcherTimer _dropLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromMilliseconds(1300) };
    private readonly DispatcherTimer _lyricTimer = new();
    private readonly DispatcherTimer _visualTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly SpringValue _lyricsScroll;
    private readonly TranslateTransform _lyricsOffset = new();

    private View _view = View.Idle;
    private FrameworkElement? _viewElement;
    private ActivityKind? _activity;
    private Size _activitySize;
    private bool _expanded;
    private bool _hiddenForFullscreen;
    private bool _wasPlaying;
    private bool _batteryLow;
    private DateTime _pausedAt = DateTime.MinValue;
    private Point _hoverPoint;
    private bool? _slimShown;
    private string? _microphoneApp;
    private string? _cameraApp;

    // Carousel
    private Page _page = Page.Today;
    private List<Page> _pages = [];
    private bool _snapStrip;
    private int _scrollAccumulator;
    private DateTime _lastScroll;
    private DateTime _scrollLockedUntil;
    private bool _pointerDown;
    private bool _dragging;
    private double _dragStartX;
    private double _dragOrigin;
    private double _dragLastX;
    private long _dragLastTicks;
    private double _dragVelocity;
    private DateTime _weekStripDate;
    private Page? _pendingPage;

    // Volume & file shelf
    private DateTime _lastVolumeGesture;
    private bool _hudVisible;
    private bool _dropActive;
    private bool _draggingOut;
    private FrameworkElement? _pressedTile;
    private Point _pressPoint;

    // Lyrics
    private Lyrics? _lyrics;
    private LyricsStatus _lyricsStatus;
    private string _lyricsKey = "";
    private CancellationTokenSource? _lyricsCancel;
    private int _lyricIndex = -2;
    private DependencyObject? _pressTarget;

    private BitmapSource? _artSource;
    private Brush? _artBrush;
    private Color _accent;
    private bool _scrubbing;
    private double _scrubFraction;

    // Live equalizer & rim
    private readonly float[] _bandLevels = new float[SpectrumAnalyzer.BandCount];
    private readonly float[] _shownLevels = new float[SpectrumAnalyzer.BandCount];
    private bool _liveAudio;
    private DateTime _lastAudible;
    private bool _rimOn;
    private double _rimFade;
    private double _rimPhase;
    private long _lastVisualTick;

    // Call, quick controls, files
    private bool? _micMuted;
    private string? _filePath;
    private readonly Dictionary<string, QuickControl> _controls = [];
    private WeatherForecast? _shownForecast;

    // Notifications & agenda
    private AppNotification? _notification;
    private readonly Dictionary<string, (string Name, BitmapSource? Icon)> _appInfo = [];
    private string _agendaKey = "";

    public IslandWindow()
    {
        InitializeComponent();

        _media = new MediaService(Dispatcher);
        _quick = new QuickControls(Dispatcher);
        _animator = new IslandAnimator(Island, Stage);
        _strip = new SpringValue(ApplyStripOffset);
        PageStrip.RenderTransform = _stripOffset;
        _lift = new SpringValue(y => _liftOffset.Y = y);
        Shape.RenderTransform = _liftOffset;
        _banners = new BannerSwitch(_settings);
        _lyricsScroll = new SpringValue(y => _lyricsOffset.Y = y);
        LyricsLines.RenderTransform = _lyricsOffset;
        foreach (var page in new FrameworkElement[] { CallPage, MediaPage, LyricsPage, TimerPage, TodayPage, ControlsPage, SystemPage, ShelfPage })
        {
            page.RenderTransformOrigin = new Point(0.5, 0.35);
            page.RenderTransform = new ScaleTransform();
        }

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
            if (IsPointerAboveIsland())
            {
                _leaveTimer.Start(); // opened from the thin line: the pointer still rests at the screen edge just above
                return;
            }
            SetExpanded(false);
        };
        _activityTimer.Tick += (_, _) => EndActivity();
        _graceTimer.Tick += (_, _) =>
        {
            _graceTimer.Stop();
            ApplyState();
        };
        _liveTimer.Tick += (_, _) => RefreshLiveContent(allPages: false);
        _dropLeaveTimer.Tick += (_, _) =>
        {
            _dropLeaveTimer.Stop();
            EndDropHover();
        };
        _hudTimer.Tick += (_, _) => HideVolumeHud();
        _lyricTimer.Tick += (_, _) => ScheduleLyricTick();
        _visualTimer.Tick += OnVisualTick;

        _media.Changed += OnMediaChanged;
        _media.TrackChanged += OnTrackChanged;
        _timer.Changed += OnTimerChanged;
        _timer.Finished += OnTimerFinished;
        _shelf.Changed += OnShelfChanged;
        _shelf.Load();
        OnShelfChanged();
        AttachDragOut(ShelfDragAll, () => _shelf.Items.ToList(), onClick: null);
        AttachDragOut(FileThumb, () => _filePath is { } file ? [new ShelfItem(file)] : [], onClick: OpenCurrentFile);
        BuildControls();
        _quick.Changed += UpdateControls;
        _weather.Changed += OnWeatherChanged;
        Shape.MouseWheel += Shape_MouseWheel;
        Shape.MouseMove += Shape_MouseMove;
        _calendar.Changed += OnCalendarChanged;
        _calendar.SetSource(_settings.CalendarSource);
        _weather.Configure(_settings.ShowWeather, _settings.WeatherCity);

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
        ApplyReservation();

        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(_shell.WndProc);
        source?.AddHook(WndProc);

        try
        {
            _screenshots = new ScreenshotWatcher(_hwnd, Dispatcher) { Enabled = _settings.ShowScreenshots };
            _screenshots.Captured += path => ShowFile(path, screenshot: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }

        PositionWindow();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyReservation();
        Dispatcher.InvokeAsync(PositionWindow, DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e)
    {
        _media.Dispose();
        _spectrum.Dispose();
        _notifications?.Dispose();
        _screenshots?.Dispose();
        _downloads?.Dispose();
        _banners.RestoreAll(forget: false); // Windows shows its own banners again while the island is closed
        _volume?.Dispose();
        _privacy?.Dispose();
        _shell?.Dispose();
        base.OnClosed(e);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // WPF has no event for horizontal scrolling (tilt wheel, two-finger touchpad swipes).
        if (msg == WM_MOUSEHWHEEL)
        {
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            if (Native.IsShiftDown) AdjustVolume(delta); // some mouse drivers turn Shift + wheel into a sideways scroll
            else if (_view == View.Expanded) HandleScroll(delta);
            handled = true;
        }
        else if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            _screenshots?.OnClipboardUpdate();
        }
        return 0;
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
            _notifications = new NotificationWatcher(Dispatcher) { IsBannerHiddenByIsland = _banners.IsHiddenByIsland };
            _notifications.Received += OnAppNotification;
            if (_settings.ShowAppNotifications)
            {
                _notifications.Start();
                _banners.ApplyAll();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }

        try
        {
            _downloads = new DownloadWatcher(Dispatcher) { Enabled = _settings.ShowDownloads };
            _downloads.Finished += path => ShowFile(path, screenshot: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        if (_settings.ShowControlsPage) _quick.Start();

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
    /// when the taskbar sits at the top), or inside its own reserved strip. Physical pixels, so it is
    /// right at any scaling.
    /// </summary>
    private void PositionWindow()
    {
        if (_hwnd == 0 || !Native.TryGetPrimaryMonitor(out var monitor, out var workArea) || !Native.GetWindowRect(_hwnd, out var rect))
            return;

        int width = rect.Right - rect.Left;
        int x = monitor.Left + (monitor.Right - monitor.Left - width) / 2;
        int top = _shell?.ReservedTop ?? workArea.Top; // our own strip is part of the "used" area, so not workArea
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// "Don't cover windows": reserve the compact island's height at the top of the screen, like a
    /// taskbar, so maximised windows (browser tabs!) start below it. Opening the island still overlaps.
    /// </summary>
    private void ApplyReservation()
    {
        if (_shell is null) return;
        if (!_settings.ReserveTopSpace)
        {
            _shell.ReserveTop(0);
            return;
        }
        bool notch = _settings.Style == IslandStyle.Notch;
        double height = notch ? 32 : IslandTopMargin + 32 + 4;
        _shell.ReserveTop((int)Math.Ceiling(height * VisualTreeHelper.GetDpi(this).DpiScaleY));
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
            ConfigureVisuals();
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

    /// <summary>An app is recording from the microphone (a call or meeting): the compact island becomes a call pill.</summary>
    private bool ShowsCall => _settings.ShowPrivacy && _privacy?.Microphone is not null;

    /// <summary>Nothing to show (and no mic / camera dots): just a thin line at the screen edge, covering nothing.</summary>
    private bool ShowsSlimIdle => _settings.SlimWhenIdle && !ShowsPrivacyDots;

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;

        if (expanded)
        {
            // Open on whatever is most relevant right now; the rest is a swipe away.
            _page = _pendingPage
                ?? (ShowsCall ? Page.Call
                : _media.HasSession && _media.IsPlaying ? Page.Media
                : _timer.IsActive ? Page.Timer
                : _media.HasSession ? Page.Media
                : Page.Today);
            _pendingPage = null;
            _snapStrip = true;
        }
        else
        {
            EndDrag();

            // A message that came in while the island was open gets its full time once it closes
            // (its Windows banner may be switched off, so this is where it is seen).
            if (_activity is ActivityKind.Notification or ActivityKind.File)
            {
                _activityTimer.Stop();
                _activityTimer.Interval = NotificationDuration;
                _activityTimer.Start();
            }
        }
        ApplyState();
    }

    /// <summary>Single place that decides what the island shows; call it after any state change.</summary>
    private void ApplyState()
    {
        View view =
            _activity == ActivityKind.TimerDone ? View.Activity // an alarm beats everything, even hover
            : _expanded || _dropActive ? View.Expanded
            : _activity is not null ? View.Activity
            : _timer.IsActive || ShowsCompactMedia || ShowsCall ? View.Compact
            : View.Idle;

        if (view == View.Compact) UpdateCompactSlots();
        if (view == View.Expanded) SyncPages();

        var (width, height, radius) = SizeOf(view);
        _animator.AnimateTo(width, height, radius);
        UpdateSlim(view);
        SwitchTo(view, width, height);
        ScheduleLyricTick();
        ConfigureVisuals();
    }

    private (double Width, double Height, double Radius) SizeOf(View view)
    {
        bool notch = _settings.Style == IslandStyle.Notch;
        switch (view)
        {
            case View.Compact:
                double width = ShowsCall ? 260 : ShowsCompactLyric ? 380 : ShowsCompactMedia ? 250 : 200;
                return notch ? (width + 10, 32, 12) : (width, 32, 16);
            case View.Activity:
                return (_activitySize.Width, _activitySize.Height, notch ? 14 : _activitySize.Height / 2);
            case View.Expanded:
                return (PageWidth, PageHeight(_page) + DotsSpace, notch ? 28 : 38);
            default:
                if (ShowsSlimIdle) return (110, 5, 2.5);
                if (notch) return (180, 30, 10);
                return ShowsPrivacyDots || _shelf.Items.Count > 0 ? (140, 32, 16) : (120, 32, 16);
        }
    }

    /// <summary>The thin idle line sits right at the screen edge (the floating style's gap closes) and has no shoulders.</summary>
    private void UpdateSlim(View view)
    {
        bool slim = view == View.Idle && ShowsSlimIdle;
        double lift = slim ? -Shape.Margin.Top : 0;
        if (_lift.Target != lift) _lift.AnimateTo(lift);

        if (_slimShown == slim) return;
        _slimShown = slim;
        IdleContent.Visibility = slim ? Visibility.Collapsed : Visibility.Visible;
        var shoulders = new DoubleAnimation(slim ? 0 : 1, TimeSpan.FromMilliseconds(slim ? 120 : 260));
        ShoulderLeft.BeginAnimation(OpacityProperty, shoulders);
        ShoulderRight.BeginAnimation(OpacityProperty, shoulders);
    }

    /// <summary>True while the pointer is in the gap between the screen edge and the (floating) island, right above it.</summary>
    private bool IsPointerAboveIsland()
    {
        if (!_expanded || !Native.GetCursorPos(out var cursor)) return false;
        var pointer = PointFromScreen(new Point(cursor.X, cursor.Y));
        var island = Island.TranslatePoint(new Point(0, 0), this);
        return pointer.Y >= 0 && pointer.Y < island.Y && pointer.X >= island.X && pointer.X <= island.X + Island.ActualWidth;
    }

    private double PageHeight(Page page) => page switch
    {
        Page.Call => 104,
        Page.Media => _media.HasTimeline ? 176 : 150,
        Page.Lyrics => 168,
        Page.Timer => 100,
        Page.Controls => 112,
        Page.System => 146,
        Page.Shelf => 134,
        _ => _settings.ShowWeather ? 202 : 164, // today, with or without the weather row
    };

    private void SwitchTo(View view, double width, double height)
    {
        FrameworkElement element = view switch
        {
            View.Compact => CompactView,
            View.Activity => ActivityView,
            View.Expanded => CarouselView,
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
            if (_hudVisible) HideVolumeHud();

            bool dots = view == View.Expanded;
            PageDots.IsHitTestVisible = dots;
            PageDots.BeginAnimation(OpacityProperty, dots ? DelayedEase(0, 1, 120, 260) : new DoubleAnimation(0, TimeSpan.FromMilliseconds(100)));
            if (dots) RefreshLiveContent(allPages: true);
        }

        RefreshLiveContent(allPages: false);
        ConfigureLiveTimer();
    }

    /// <summary>The only periodic work in the app, and only while something on screen is counting.</summary>
    private void ConfigureLiveTimer()
    {
        bool expanded = _view == View.Expanded;
        bool countdown = _timer.IsActive && !_timer.IsPaused && (_view == View.Compact || (expanded && _page == Page.Timer));
        bool callClock = ShowsCall && (_view == View.Compact || (expanded && _page == Page.Call));
        TimeSpan? interval =
            countdown ? TimeSpan.FromMilliseconds(250)
            : expanded && _page == Page.Media && _media.HasTimeline && _media.IsPlaying ? TimeSpan.FromMilliseconds(500)
            : callClock || (expanded && _page is Page.Today or Page.System) ? TimeSpan.FromSeconds(1)
            : expanded && _page == Page.Controls ? TimeSpan.FromSeconds(2) // mic / sound mute can change elsewhere
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

    private void RefreshLiveContent(bool allPages)
    {
        if (_timer.IsActive) UpdateTimerContent();
        if (ShowsCall && (_view == View.Compact || _view == View.Expanded)) UpdateCall();
        if (_view != View.Expanded)
        {
            _stats.Reset(); // the system page starts its history afresh next time
            return;
        }
        if (allPages || _page == Page.Today) UpdateToday();
        if (allPages || _page == Page.Media) UpdateTimeline();
        if (allPages || _page == Page.Controls) UpdateControls();
        if (_page == Page.System) UpdateSystem();
    }

    /// <summary>
    /// Compact island: left = artwork, timer ring or call microphone; middle = live lyric or call app;
    /// right = equalizer, countdown or call duration. A call takes over the whole pill.
    /// </summary>
    private void UpdateCompactSlots()
    {
        bool call = ShowsCall;
        bool media = ShowsCompactMedia && !call;
        bool timer = _timer.IsActive && !call;
        CompactArt.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        CompactRing.Visibility = !media && timer ? Visibility.Visible : Visibility.Collapsed;
        CompactEq.Visibility = media && !timer ? Visibility.Visible : Visibility.Collapsed;
        CompactTimerText.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
        CompactLyric.Visibility = CompactLyricOld.Visibility = ShowsCompactLyric && !call ? Visibility.Visible : Visibility.Collapsed;
        CompactCall.Visibility = CompactCallApp.Visibility = CompactCallTime.Visibility = call ? Visibility.Visible : Visibility.Collapsed;
        CompactMicDot.Visibility = !call && _settings.ShowPrivacy && _microphoneApp is not null ? Visibility.Visible : Visibility.Collapsed;
        if (call) UpdateCall();
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
        foreach (var candidate in new FrameworkElement[] { VolumePanel, PowerPanel, TrackPanel, NotifyPanel, InfoPanel })
            candidate.Visibility = ReferenceEquals(candidate, panel) ? Visibility.Visible : Visibility.Collapsed;
        if ((switching || kind == ActivityKind.Notification) && _view == View.Activity) FadeIn(panel); // a new message pops in again

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
        _notification = null;
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

    // ───────────────────────────── carousel ─────────────────────────────

    /// <summary>Keeps the page list, the dots and the strip position in line with what is available.</summary>
    private void SyncPages()
    {
        List<Page> pages = [];
        if (ShowsCall) pages.Add(Page.Call);
        if (_media.HasSession) pages.Add(Page.Media);
        if (_media.HasSession && _settings.ShowLyrics) pages.Add(Page.Lyrics);
        pages.Add(Page.Timer);
        pages.Add(Page.Today);
        if (_settings.ShowControlsPage) pages.Add(Page.Controls);
        if (_settings.ShowSystemPage) pages.Add(Page.System);
        if (_shelf.Items.Count > 0 || _dropActive) pages.Add(Page.Shelf);

        CallPage.Visibility = pages.Contains(Page.Call) ? Visibility.Visible : Visibility.Collapsed;
        ControlsPage.Visibility = pages.Contains(Page.Controls) ? Visibility.Visible : Visibility.Collapsed;
        SystemPage.Visibility = pages.Contains(Page.System) ? Visibility.Visible : Visibility.Collapsed;
        TodayPage.Height = PageHeight(Page.Today);
        MediaPage.Visibility = pages.Contains(Page.Media) ? Visibility.Visible : Visibility.Collapsed;
        bool lyricsShown = LyricsPage.Visibility == Visibility.Visible;
        LyricsPage.Visibility = pages.Contains(Page.Lyrics) ? Visibility.Visible : Visibility.Collapsed;
        if (!lyricsShown && LyricsPage.Visibility == Visibility.Visible)
            _lyricIndex = -2; // it was never laid out while hidden: position the lines afresh
        ShelfPage.Visibility = pages.Contains(Page.Shelf) ? Visibility.Visible : Visibility.Collapsed;
        UpdateShelfMode();
        if (!pages.Contains(_page)) _page = Page.Timer;

        bool changed = !pages.SequenceEqual(_pages);
        _pages = pages;
        if (changed) RebuildDots();

        if (changed || _snapStrip)
        {
            _snapStrip = false;
            _strip.Snap(StripTarget(_page)); // pages moved under us: jump, don't slide
            UpdateDots(animate: false);
        }
    }

    private double StripTarget(Page page) => -_pages.IndexOf(page) * PageWidth;

    private void GoToPage(Page page, double velocity = 0)
    {
        if (!_pages.Contains(page)) return;

        bool changed = page != _page;
        _page = page;
        _strip.AnimateTo(StripTarget(page), velocity);
        UpdateDots(animate: true);

        if (changed)
        {
            RefreshLiveContent(allPages: false);
            ApplyState(); // the island morphs to the new page's height
        }
    }

    private void StepPage(int direction)
    {
        int index = _pages.IndexOf(_page) + direction;
        if (index >= 0 && index < _pages.Count)
        {
            GoToPage(_pages[index]);
            return;
        }

        // Already at the first / last page: a little bump says "that's the end".
        _strip.AnimateTo(StripTarget(_page), velocity: -direction * 900);
    }

    /// <summary>Moves the strip and gives the neighbours depth: they shrink and fade as they slide away.</summary>
    private void ApplyStripOffset(double x)
    {
        _stripOffset.X = x;
        for (int i = 0; i < _pages.Count; i++)
        {
            var element = PageElement(_pages[i]);
            double distance = Math.Min(1, Math.Abs(x + i * PageWidth) / PageWidth);
            element.Opacity = 1 - 0.65 * distance;
            if (element.RenderTransform is ScaleTransform scale)
                scale.ScaleX = scale.ScaleY = 1 - 0.08 * distance;
        }
    }

    private FrameworkElement PageElement(Page page) => page switch
    {
        Page.Call => CallPage,
        Page.Controls => ControlsPage,
        Page.System => SystemPage,
        Page.Media => MediaPage,
        Page.Lyrics => LyricsPage,
        Page.Timer => TimerPage,
        Page.Shelf => ShelfPage,
        _ => TodayPage,
    };

    private void RebuildDots()
    {
        PageDots.Children.Clear();
        foreach (var page in _pages)
        {
            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = Brushes.White,
                Opacity = 0.35,
            };
            var target = new Border // bigger, invisible hit area around the 6px dot
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(3, 5, 3, 5),
                Cursor = Cursors.Hand,
                Tag = page,
                Child = dot,
            };
            target.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                GoToPage(page);
            };
            PageDots.Children.Add(target);
        }
    }

    /// <summary>The active dot stretches into a pill, like iOS.</summary>
    private void UpdateDots(bool animate)
    {
        foreach (Border target in PageDots.Children)
        {
            var dot = (Border)target.Child;
            bool active = (Page)target.Tag == _page;
            double width = active ? 16 : 6, opacity = active ? 0.95 : 0.35;
            if (animate)
            {
                var duration = TimeSpan.FromMilliseconds(260);
                dot.BeginAnimation(WidthProperty, new DoubleAnimation(width, duration) { EasingFunction = Ease });
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration));
            }
            else
            {
                dot.BeginAnimation(WidthProperty, null);
                dot.BeginAnimation(OpacityProperty, null);
                dot.Width = width;
                dot.Opacity = opacity;
            }
        }
    }

    /// <summary>Plain wheel turns the carousel pages; Shift + wheel is volume, anywhere on the island.</summary>
    private void Shape_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Native.IsShiftDown) AdjustVolume(e.Delta);
        else if (_view == View.Expanded) HandleScroll(-e.Delta); // wheel down / two fingers up → next page
    }

    /// <summary>One wheel notch (120) = 2%; touchpads send fractions of a notch, so they get finer steps.</summary>
    private void AdjustVolume(int delta)
    {
        if (_volume is null) return;
        _hoverTimer.Stop(); // adjusting the volume should not pop the island open
        _lastVolumeGesture = DateTime.UtcNow;
        _volume.Adjust(delta / 120f * VolumeStep);
    }

    /// <summary>Middle click (wheel click) toggles mute.</summary>
    private void Shape_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _volume is null) return;
        e.Handled = true;
        _lastVolumeGesture = DateTime.UtcNow;
        _volume.ToggleMute();
    }

    /// <summary>Positive = towards the next page. A full wheel notch is 120; touchpads send many small steps.</summary>
    private void HandleScroll(int delta)
    {
        var now = DateTime.UtcNow;
        if (now < _scrollLockedUntil)
        {
            _scrollLockedUntil = now + ScrollLock; // still the same flick (touchpad inertia)
            return;
        }

        if (now - _lastScroll > TimeSpan.FromMilliseconds(400)) _scrollAccumulator = 0;
        _lastScroll = now;
        _scrollAccumulator += delta;
        if (Math.Abs(_scrollAccumulator) < 100) return;

        StepPage(Math.Sign(_scrollAccumulator));
        _scrollAccumulator = 0;
        _scrollLockedUntil = now + ScrollLock;
    }

    private void PageViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons and the seek bar handle their own clicks, so this only fires on "empty" page areas.
        _pointerDown = true;
        _dragging = false;
        _pressTarget = e.OriginalSource as DependencyObject;
        _dragStartX = _dragLastX = e.GetPosition(Island).X;
        _dragLastTicks = Stopwatch.GetTimestamp();
        _dragVelocity = 0;
        _dragOrigin = _strip.Value;
        PageViewport.CaptureMouse();
    }

    private void PageViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pointerDown) return;

        double x = e.GetPosition(Island).X;
        double dx = x - _dragStartX;
        if (!_dragging && Math.Abs(dx) < 6) return;
        _dragging = true;

        // Follow the finger 1:1, with rubber-banding past the first and last page.
        double offset = _dragOrigin + dx;
        double min = -(_pages.Count - 1) * PageWidth;
        if (offset > 0) offset *= 0.35;
        else if (offset < min) offset = min + (offset - min) * 0.35;
        _strip.Snap(offset);

        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _dragLastTicks) / (double)Stopwatch.Frequency;
        if (seconds > 0.004) _dragVelocity = (x - _dragLastX) / seconds;
        _dragLastX = x;
        _dragLastTicks = now;
    }

    private void PageViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerDown) return;
        bool swiped = _dragging;
        if (swiped) e.Handled = true; // a swipe is not a click
        FinishDrag();

        if (swiped) return;
        switch (TappedTag())
        {
            // A tap on a lyric line jumps the song to that line, like in Spotify.
            case LyricLine line when _lyrics?.IsSynced == true && _media.HasTimeline:
                _media.Seek(line.Time.TotalSeconds / _media.Duration.TotalSeconds);
                break;
            case "open-app":
                OpenSourceApp();
                break;
            case "go-media":
                GoToPage(Page.Media);
                break;
            case "calendar-setup":
                OpenCalendarDialog();
                break;
            case "open-call-app":
                OpenCallApp();
                break;
            case "open-settings":
                OpenSettings();
                break;
        }
    }

    /// <summary>The Tag of the tapped element or its nearest tagged parent inside the carousel.</summary>
    private object? TappedTag()
    {
        for (var node = _pressTarget; node is not null && !ReferenceEquals(node, PageViewport);
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Tag: { } tag }) return tag;
        }
        return null;
    }

    private void PageViewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_pointerDown) FinishDrag();
    }

    private void FinishDrag()
    {
        bool dragged = _dragging;
        _pointerDown = false;
        _dragging = false;
        if (PageViewport.IsMouseCaptured) PageViewport.ReleaseMouseCapture();
        if (!dragged) return;

        // Where would a flick of this speed land? Move at most one page per swipe.
        int current = _pages.IndexOf(_page);
        double projected = _strip.Value + _dragVelocity * 0.18;
        int target = Math.Clamp((int)Math.Round(-projected / PageWidth), current - 1, current + 1);
        target = Math.Clamp(target, 0, _pages.Count - 1);
        GoToPage(_pages[target], _dragVelocity);
    }

    private void EndDrag()
    {
        _pointerDown = false;
        _dragging = false;
        if (PageViewport.IsMouseCaptured) PageViewport.ReleaseMouseCapture();
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
        if (LyricsKey != _lyricsKey) LoadLyrics();
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
            SetArtwork(LyricsArt);

            // Ambient light from the artwork, cross-faded with each new cover.
            var backdrop = _media.Backdrop is null ? null : Frozen(new ImageBrush(_media.Backdrop) { Stretch = Stretch.UniformToFill });
            MediaGlow.Background = LyricsGlow.Background = backdrop;
            var fadeIn = TimeSpan.FromMilliseconds(450);
            MediaGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, backdrop is null ? 0 : 0.6, fadeIn));
            LyricsGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, backdrop is null ? 0 : 0.4, fadeIn));
            MediaArt.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = Ease });
        }

        if (_accent != _media.Accent)
        {
            _accent = _media.Accent;
            var brush = Frozen(new SolidColorBrush(_accent));
            CompactEq.BarBrush = MediaEq.BarBrush = TrackEq.BarBrush = brush;
            MediaArtGlow.Color = _artSource is null ? Colors.Black : _accent; // the cover glows in its own colour
            IslandRim.BorderBrush = RimBrush(_accent);
        }
        CompactEq.IsPlaying = MediaEq.IsPlaying = TrackEq.IsPlaying = _media.IsPlaying;

        MediaTitle.Text = _media.Title;
        MediaArtist.Text = _media.Artist;
        if (_activity == ActivityKind.Track) FillTrackPanel();

        PlayPauseIcon.Data = (Geometry)FindResource(_media.IsPlaying ? "IconPause" : "IconPlay");
        PreviousButton.IsEnabled = _media.CanGoPrevious;
        NextButton.IsEnabled = _media.CanGoNext;
        MediaTimeline.Visibility = _media.HasTimeline ? Visibility.Visible : Visibility.Collapsed;
        MediaPage.Height = PageHeight(Page.Media);

        LyricsButton.Visibility = _settings.ShowLyrics ? Visibility.Visible : Visibility.Collapsed;
        OpenAppButton.Visibility = _media.SourceAppId.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenAppButton.ToolTip = _media.SourceAppName.Length > 0 ? $"Buka {_media.SourceAppName}" : "Buka pemutar";
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
        if (!_media.HasTimeline || _scrubbing) return; // while scrubbing the bar follows the pointer

        var duration = _media.Duration;
        var position = _media.Position;
        SetProgress(MediaProgressDone, MediaProgressRest, position.TotalSeconds / duration.TotalSeconds);
        MediaElapsed.Text = FormatTime(position);
        MediaRemaining.Text = "-" + FormatTime(duration - position);
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => _media.Previous();

    private void Next_Click(object sender, RoutedEventArgs e) => _media.Next();

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        _media.TogglePlayPause();

        // A small springy "pop" on the icon as it swaps.
        var pop = new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
        PlayPauseScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        PlayPauseScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void LyricsButton_Click(object sender, RoutedEventArgs e) => GoToPage(Page.Lyrics);

    private void OpenAppButton_Click(object sender, RoutedEventArgs e) => OpenSourceApp();

    /// <summary>Brings the player (Spotify, a browser...) to the front, or starts it.</summary>
    private void OpenSourceApp()
    {
        string id = _media.SourceAppId;
        if (id.Length == 0) return;
        try
        {
            // Store apps: their app id launches / activates them directly.
            if (id.Contains('!'))
            {
                LaunchApp(id);
                return;
            }

            string name = System.IO.Path.GetFileNameWithoutExtension(id);
            BringToFront(name, name + ".exe");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    /// <summary>
    /// Desktop apps ("Spotify", "Zoom"): find their window and bring it forward; when it is hidden
    /// in the tray, starting it again makes single-instance apps show themselves.
    /// </summary>
    private static void BringToFront(string processName, string start)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            nint window = process.MainWindowHandle;
            process.Dispose();
            if (window == 0) continue;
            Native.ShowWindow(window, Native.IsIconic(window) ? Native.SW_RESTORE : Native.SW_SHOW);
            Native.SetForegroundWindow(window);
            return;
        }
        Process.Start(new ProcessStartInfo(start) { UseShellExecute = true });
    }

    /// <summary>Starts or activates an app by its app id, like clicking it in the Start menu.</summary>
    private static void LaunchApp(string appId)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    // Progress bar: grows and shows a knob on hover; press and drag to scrub, release to seek.
    private void MediaTrack_MouseEnter(object sender, MouseEventArgs e) => SetTrackHover(true);

    private void MediaTrack_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) SetTrackHover(false);
    }

    private void SetTrackHover(bool hover)
    {
        var duration = TimeSpan.FromMilliseconds(160);
        MediaBar.BeginAnimation(HeightProperty, new DoubleAnimation(hover ? 8 : 5, duration) { EasingFunction = Ease });
        MediaKnob.BeginAnimation(OpacityProperty, new DoubleAnimation(hover ? 1 : 0, duration));
    }

    private void MediaTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_media.HasTimeline || MediaTrack.ActualWidth <= 0) return;
        _scrubbing = true;
        MediaTrack.CaptureMouse();
        ScrubTo(e.GetPosition(MediaTrack).X);
    }

    private void MediaTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (_scrubbing) ScrubTo(e.GetPosition(MediaTrack).X);
    }

    private void MediaTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbing) return;
        e.Handled = true;
        _scrubbing = false;
        MediaTrack.ReleaseMouseCapture();
        _media.Seek(_scrubFraction);
        if (!MediaTrack.IsMouseOver) SetTrackHover(false);
    }

    private void MediaTrack_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false; // e.g. the island collapsed mid-drag: keep the song where it was
        UpdateTimeline();
    }

    private void ScrubTo(double x)
    {
        _scrubFraction = Math.Clamp(x / MediaTrack.ActualWidth, 0, 1);
        var duration = _media.Duration;
        var position = duration * _scrubFraction;
        SetProgress(MediaProgressDone, MediaProgressRest, _scrubFraction);
        MediaElapsed.Text = FormatTime(position);
        MediaRemaining.Text = "-" + FormatTime(duration - position);
    }

    // ───────────────────────────── lyrics ─────────────────────────────

    private string LyricsKey => _media.HasSession && _settings.ShowLyrics ? $"{_media.Artist}\u0001{_media.Title}" : "";

    private bool HasLyricLines => _lyrics is { IsInstrumental: false, Lines.Count: > 0 };

    private bool ShowsCompactLyric => _settings.ShowLyrics && _settings.CompactLyrics && ShowsCompactMedia && HasLyricLines;

    private async void LoadLyrics()
    {
        _lyricsCancel?.Cancel();
        _lyricsKey = LyricsKey;
        _lyrics = null;
        _lyricIndex = -2;
        LyricsLines.Children.Clear();

        if (_lyricsKey.Length == 0)
        {
            SetLyricsStatus(LyricsStatus.None);
            return;
        }

        string key = _lyricsKey;
        var cancel = _lyricsCancel = new CancellationTokenSource();
        SetLyricsStatus(LyricsStatus.Loading);

        Lyrics? lyrics;
        try
        {
            lyrics = await _lyricsService.GetAsync(_media.Title, _media.Artist, _media.Album, _media.Duration, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return; // the track changed again
        }
        catch (Exception)
        {
            if (key == _lyricsKey) SetLyricsStatus(LyricsStatus.Offline); // no internet: not worth an error.log entry
            return;
        }
        if (key != _lyricsKey) return;

        _lyrics = lyrics;
        BuildLyricLines();
        SetLyricsStatus(lyrics is null ? LyricsStatus.NotFound : lyrics.IsInstrumental ? LyricsStatus.Instrumental : LyricsStatus.Ready);
        ApplyState(); // the compact island widens for the live line
    }

    private void SetLyricsStatus(LyricsStatus status)
    {
        _lyricsStatus = status;
        LyricsHeader.Text = _media.Title.Length > 0 ? _media.Title : "Lirik";
        LyricsArtist.Text = _media.Artist;
        LyricsButton.Opacity = status == LyricsStatus.Ready ? 1 : 0.4; // dim when there is nothing to show
        LyricsSource.Text = status == LyricsStatus.Ready ? (_lyrics!.IsSynced ? "LRCLIB" : "LRCLIB · tanpa sinkron") : "";
        LyricsMessage.Text = status switch
        {
            LyricsStatus.Loading => "Mencari lirik…",
            LyricsStatus.NotFound => "Lirik tidak ditemukan",
            LyricsStatus.Instrumental => "♪  Instrumental",
            LyricsStatus.Offline => "Lirik tidak bisa dimuat (offline?)",
            _ => "",
        };
        LyricsMessage.Visibility = LyricsMessage.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildLyricLines()
    {
        LyricsLines.Children.Clear();
        _lyricIndex = -2;
        if (!HasLyricLines) return;

        foreach (var line in _lyrics!.Lines)
        {
            LyricsLines.Children.Add(new TextBlock
            {
                Text = line.Text,
                Tag = line,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 3),
                Opacity = 0.3,
                Cursor = _lyrics.IsSynced ? Cursors.Hand : null,
            });
        }
        _lyricsScroll.Snap(LyricsViewport.Height * 0.4);
    }

    /// <summary>
    /// Moves the highlight to the line being sung and arms a one-shot timer for exactly when the next
    /// line starts — no fixed-rate ticking. Runs only while lyrics are on screen.
    /// </summary>
    private void ScheduleLyricTick()
    {
        _lyricTimer.Stop();
        bool onScreen = HasLyricLines && ((_view == View.Compact && ShowsCompactLyric) || (_view == View.Expanded && _page == Page.Lyrics));
        if (!onScreen) return;

        UpdateLyricHighlight();
        if (!_media.IsPlaying) return;

        var lines = _lyrics!.Lines;
        int next = _lyricIndex + 1;
        var wait = next < lines.Count ? lines[next].Time - (_media.Position + LyricLead) : TimeSpan.FromSeconds(1);
        // Never sleep longer than a second, so seeks in the player itself are picked up quickly too.
        _lyricTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds, 40, 1000));
        _lyricTimer.Start();
    }

    private void UpdateLyricHighlight()
    {
        var lines = _lyrics!.Lines;
        int index = _lyrics.IndexAt(_media.Position + LyricLead);
        if (index == _lyricIndex) return;

        int previous = _lyricIndex;
        _lyricIndex = index;

        // Karaoke page: dim the old line, light up the new one and roll it to the middle.
        var fade = TimeSpan.FromMilliseconds(220);
        if (previous == -2)
        {
            foreach (UIElement line in LyricsLines.Children)
            {
                line.BeginAnimation(OpacityProperty, null);
                line.Opacity = 0.3;
            }
        }
        else if (previous >= 0 && previous < LyricsLines.Children.Count)
        {
            LyricsLines.Children[previous].BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, fade));
        }
        if (index >= 0 && index < LyricsLines.Children.Count)
        {
            var current = (FrameworkElement)LyricsLines.Children[index];
            current.BeginAnimation(OpacityProperty, new DoubleAnimation(1, fade));
            if (current.ActualHeight <= 0) LyricsLines.UpdateLayout();
            double top = current.TranslatePoint(new Point(0, 0), LyricsLines).Y;
            double target = LyricsViewport.Height * 0.42 - (top + current.ActualHeight / 2);
            if (previous == -2) _lyricsScroll.Snap(target);
            else _lyricsScroll.AnimateTo(target, response: 0.55, dampingRatio: 0.9);
        }

        // Compact island: the old line rolls up and out while the new one rolls in from below.
        string text = index >= 0 ? lines[index].Text : "♪";
        string old = CompactLyric.Text;
        if (old == text) return;
        CompactLyric.Text = text;
        if (previous == -2 || old.Length == 0) return;

        CompactLyricOld.Text = old;
        var roll = TimeSpan.FromMilliseconds(360);
        CompactLyricOld.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)));
        CompactLyricOldShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -12, roll) { EasingFunction = EaseIn });
        CompactLyric.BeginAnimation(OpacityProperty, DelayedEase(0, 1, 70, 260));
        CompactLyricShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, roll) { EasingFunction = Ease });
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
        TimerIdle.Visibility = _timer.IsActive ? Visibility.Collapsed : Visibility.Visible;
        TimerRunning.Visibility = _timer.IsActive ? Visibility.Visible : Visibility.Collapsed;
        if (!_timer.IsActive) return;

        int seconds = (int)Math.Ceiling(_timer.Remaining.TotalSeconds);
        double total = _timer.Total.TotalSeconds;
        string text = FormatCountdown(seconds);

        // Whole seconds only, so the ring and text change (and re-render) at most once a second.
        CompactTimerText.Text = TimerText.Text = text;
        CompactRing.Progress = TimerRing.Progress = total > 0 ? Math.Clamp(seconds / total, 0, 1) : 0;
        TimerText.Opacity = CompactTimerText.Opacity = _timer.IsPaused ? 0.55 : 1;
        TimerPauseIcon.Data = (Geometry)FindResource(_timer.IsPaused ? "IconPlay" : "IconPause");

        // "25 menit · selesai 10.24"; rounded to whole seconds so the minute never flickers.
        var end = DateTime.Now + TimeSpan.FromSeconds(seconds);
        TimerLabel.Text = _timer.IsPaused ? "Dijeda" : $"{FormatDuration(_timer.Total)} · selesai {end.ToString("t", CultureInfo.CurrentCulture)}";

        // The last ten seconds turn red; a paused ring dims and stops glowing.
        bool urgent = !_timer.IsPaused && seconds <= 10;
        var accent = (SolidColorBrush)FindResource(urgent ? "WarningBrush" : "TimerBrush");
        if (!ReferenceEquals(TimerRing.RingBrush, accent))
        {
            var track = (Brush)FindResource(urgent ? "WarningTrackBrush" : "TimerTrackBrush");
            TimerRing.RingBrush = CompactRing.RingBrush = accent;
            TimerRing.TrackBrush = CompactRing.TrackBrush = track;
            TimerText.Foreground = CompactTimerText.Foreground = TimerIcon.Stroke = accent;
            TimerGlow.Color = accent.Color;
        }
        TimerGlow.Opacity = _timer.IsPaused ? 0 : 0.85;
        TimerRing.Opacity = _timer.IsPaused ? 0.5 : 1;
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
        if (_view == View.Expanded && _page == Page.Controls) UpdateControls();
        // Feedback for the user's own scroll / middle click shows even when volume notifications are off.
        bool fromGesture = DateTime.UtcNow - _lastVolumeGesture < TimeSpan.FromMilliseconds(600);
        if ((!_settings.ShowVolume && !fromGesture) || _hiddenForFullscreen) return;

        if (_view == View.Expanded)
        {
            ShowVolumeHud(level, muted);
            return;
        }

        bool silent = muted || level < 0.005f;
        WaveSmall.Visibility = silent ? Visibility.Collapsed : Visibility.Visible;
        WaveLarge.Visibility = !silent && level > 0.5f ? Visibility.Visible : Visibility.Collapsed;
        MuteMark.Visibility = silent ? Visibility.Visible : Visibility.Collapsed;
        VolumeFill.Opacity = muted ? 0.4 : 1;
        SetProgress(VolumeDone, VolumeRest, level);
        VolumeText.Text = muted ? "–" : Math.Round(level * 100).ToString(CultureInfo.CurrentCulture);

        ShowActivity(ActivityKind.Volume, VolumePanel, 300, 36, TimeSpan.FromSeconds(1.6));
    }

    private void ShowVolumeHud(float level, bool muted)
    {
        bool silent = muted || level < 0.005f;
        HudWaves.Visibility = silent ? Visibility.Collapsed : Visibility.Visible;
        HudMute.Visibility = silent ? Visibility.Visible : Visibility.Collapsed;
        HudFill.Opacity = muted ? 0.4 : 1;
        SetProgress(HudDone, HudRest, level);
        HudText.Text = muted ? "–" : Math.Round(level * 100).ToString(CultureInfo.CurrentCulture);

        if (!_hudVisible)
        {
            _hudVisible = true;
            VolumeHud.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
            PageDots.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(100)));
        }
        _hudTimer.Stop();
        _hudTimer.Start();
    }

    private void HideVolumeHud()
    {
        _hudTimer.Stop();
        _hudVisible = false;
        VolumeHud.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
        if (_view == View.Expanded)
            PageDots.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    private void OnAudioDeviceChanged(string name, bool worn)
    {
        _spectrum.Reset(); // listen to the new output from now on
        if (_view == View.Expanded) UpdateToday();
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

    // ───────────────────────────── call pill ─────────────────────────────

    /// <summary>Call pill and call page: which app, how long, and whether the microphone is muted.</summary>
    private void UpdateCall()
    {
        if (_privacy?.Microphone is not { } app) return;
        _micMuted = MicrophoneMute.Get();
        bool muted = _micMuted == true;
        string duration = FormatTime(DateTime.UtcNow - app.StartedUtc);

        CompactCallApp.Text = app.Name;
        CompactCallTime.Text = duration;
        var accent = (Brush)FindResource(muted ? "WarningBrush" : "CallBrush");
        CompactCall.Background = CallBadge.Background = CompactCallTime.Foreground = accent;
        CompactCallSlash.Visibility = CallSlash.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        CallGlow.Color = ((SolidColorBrush)accent).Color;

        CallApp.Text = app.Name;
        string state = _micMuted is null ? "Mikrofon aktif" : muted ? "Mikrofon dibisukan" : "Mikrofon aktif";
        CallStatus.Text = _cameraApp is not null ? $"{state} · kamera · {duration}" : $"{state} · {duration}";
        CallMuteButton.Content = muted ? "Nyalakan" : "Bisukan";
        CallMuteButton.IsEnabled = _micMuted is not null;
        CallMuteButton.Background = muted ? (Brush)FindResource("WarningBrush") : (Brush)FindResource("TrackBrush");
    }

    private void CallMute_Click(object sender, RoutedEventArgs e)
    {
        MicrophoneMute.Toggle();
        UpdateCall();
        UpdateControls();
    }

    /// <summary>Brings the app that is using the microphone (the meeting) to the front.</summary>
    private void OpenCallApp()
    {
        if (_privacy?.Microphone is not { } app) return;
        try
        {
            if (app.ExePath is { } path) BringToFront(System.IO.Path.GetFileNameWithoutExtension(path), path);
            else LaunchApp(app.Key + "!App"); // Store apps: their main entry is almost always "App"
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    // ───────────────────────────── power & today page ─────────────────────────────

    private void OnPowerChanged()
    {
        _timer.Resync(); // also covers waking up from sleep

        var change = _power.Update();
        var state = _power.State;

        if (_view == View.Expanded) UpdateToday();
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

    private void UpdateToday()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        ClockTime.Text = now.ToString("t", culture);
        ClockDate.Text = now.ToString("dddd, d MMMM", culture);
        UpdateWeekStrip(now.Date);
        UpdateAgenda(now);
        UpdateWeather();

        var power = _power.State;
        ClockBattery.Visibility = power.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        ClockBatteryText.Text = $"{power.Percent}%";
        ClockBatteryIcon.Level = power.Percent / 100.0;
        ClockBatteryIcon.IsCharging = power.PluggedIn;
        ClockBatteryIcon.IsSaver = power.Saver;

        string device = _volume?.DeviceName ?? "";
        ClockDevice.Visibility = device.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClockDeviceText.Text = device;
        bool worn = _volume?.DeviceWorn ?? false;
        ClockDeviceFill.Data = (Geometry)FindResource(worn ? "IconHeadphoneCups" : "IconSpeaker");
        ClockDeviceStroke.Data = (Geometry)FindResource(worn ? "IconHeadphoneBand" : "IconSpeakerWaves");
    }

    /// <summary>This week as seven columns (localised, starting on the culture's first weekday), today circled.</summary>
    private void UpdateWeekStrip(DateTime today)
    {
        if (_weekStripDate == today) return;
        _weekStripDate = today;

        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        var primary = (Brush)FindResource("PrimaryText");
        var secondary = (Brush)FindResource("SecondaryText");
        var tertiary = (Brush)FindResource("TertiaryText");
        var highlight = (Brush)FindResource("WarningBrush");
        var busyBrush = (Brush)FindResource("CalendarBrush");
        var events = _calendar.EventsAround(today);

        int offset = ((int)today.DayOfWeek - (int)format.FirstDayOfWeek + 7) % 7;
        var start = today.AddDays(-offset);

        WeekStrip.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            var day = start.AddDays(i);
            bool isToday = day == today;
            bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            var name = new TextBlock
            {
                Text = format.GetAbbreviatedDayName(day.DayOfWeek),
                FontSize = 10.5,
                Foreground = isToday ? primary : tertiary,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var number = new TextBlock
            {
                Text = day.Day.ToString(CultureInfo.CurrentCulture),
                FontSize = 13,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isToday ? primary : weekend ? tertiary : secondary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var circle = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = isToday ? highlight : Brushes.Transparent,
                Margin = new Thickness(0, 3, 0, 0),
                Child = number,
            };
            // A dot under days with something in the calendar.
            bool busy = events.Any(e => e.Start.Date <= day && (e.End > day || e.Start.Date == day));
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 4,
                Height = 4,
                Margin = new Thickness(0, 3, 0, 0),
                Fill = busyBrush,
                Opacity = busy ? 1 : 0,
            };
            WeekStrip.Children.Add(new StackPanel { Children = { name, circle, dot } });
        }
    }

    private void OnWeatherChanged()
    {
        if (_view == View.Expanded) UpdateToday();
        if (_view == View.Expanded && _page == Page.Today) ApplyState(); // the row may appear or go
    }

    /// <summary>Weather row: now (icon, temperature, description · city) and the next three hours.</summary>
    private void UpdateWeather()
    {
        var state = _weather.State;
        WeatherRow.Visibility = state == WeatherState.Off ? Visibility.Collapsed : Visibility.Visible;
        bool setup = state is WeatherState.NoLocation or WeatherState.Failed && _weather.Forecast is null;
        WeatherRow.Tag = setup ? "open-settings" : null; // tap the hint to set a city
        WeatherRow.Background = setup ? Brushes.Transparent : null;
        WeatherRow.Cursor = setup ? Cursors.Hand : null;
        if (state == WeatherState.Off) return;

        if (_weather.Forecast is not { } forecast)
        {
            WeatherNowIcon.Visibility = Visibility.Collapsed;
            WeatherNowTemp.Text = "";
            WeatherNowText.MaxWidth = double.PositiveInfinity;
            WeatherNowText.Text = state switch
            {
                WeatherState.NoLocation => "Cuaca: atur kota di Pengaturan",
                WeatherState.Failed => "Cuaca tidak bisa dimuat",
                _ => "Memuat cuaca…",
            };
            WeatherHours.Children.Clear();
            _shownForecast = null;
            return;
        }

        WeatherNowIcon.Visibility = Visibility.Visible;
        WeatherNowIcon.Kind = (int)WeatherForecast.KindOf(forecast.Code);
        WeatherNowIcon.IsDay = forecast.IsDay;
        WeatherNowTemp.Text = $"{Math.Round(forecast.Temperature):0}°";
        WeatherNowText.MaxWidth = 96;
        WeatherNowText.Text = WeatherForecast.Describe(forecast.Code);
        WeatherRow.ToolTip = _weather.PlaceName.Length > 0 ? $"{WeatherForecast.Describe(forecast.Code)} · {_weather.PlaceName}" : null;
        if (ReferenceEquals(forecast, _shownForecast)) return;
        _shownForecast = forecast;

        WeatherHours.Children.Clear();
        foreach (var hour in forecast.Hours.Take(3))
        {
            WeatherHours.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0, 0, 0),
                Children =
                {
                    new TextBlock { Text = hour.Time.ToString("HH", CultureInfo.InvariantCulture), FontSize = 11, Foreground = (Brush)FindResource("TertiaryText"), VerticalAlignment = VerticalAlignment.Center },
                    new Controls.WeatherIcon { Kind = (int)WeatherForecast.KindOf(hour.Code), IsDay = hour.IsDay, Width = 15, Height = 15, Margin = new Thickness(3, 0, 2, 0) },
                    new TextBlock { Text = $"{Math.Round(hour.Temperature):0}°", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }
    }

    // ───────────────────────────── controls page ─────────────────────────────

    private void BuildControls()
    {
        AddControl("wifi", "Wi-Fi", "IconWifiDot", "IconWifi", () =>
        {
            if (_quick.WifiOn is { } on) _quick.SetWifi(!on);
        });
        AddControl("bluetooth", "Bluetooth", null, "IconBluetooth", () =>
        {
            if (_quick.BluetoothOn is { } on) _quick.SetBluetooth(!on);
        });
        AddControl("mic", "Mikrofon", "IconMicBody", "IconMicStand", () =>
        {
            MicrophoneMute.Toggle();
            if (ShowsCall) UpdateCall();
        });
        AddControl("sound", "Suara", "IconSpeaker", "IconSpeakerWaves", () => _volume?.ToggleMute());
        AddControl("dark", "Mode gelap", "IconMoon", null, () => QuickControls.SetDarkMode(!QuickControls.DarkMode));
    }

    private void AddControl(string key, string label, string? fillIcon, string? strokeIcon, Action toggle)
    {
        var icon = new Grid { Width = 16, Height = 16 };
        if (fillIcon is not null) icon.Children.Add(new System.Windows.Shapes.Path { Data = (Geometry)FindResource(fillIcon), Fill = Brushes.White });
        if (strokeIcon is not null)
        {
            icon.Children.Add(new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource(strokeIcon),
                Style = (Style)FindResource("StrokeIcon"),
                StrokeLineJoin = PenLineJoin.Round,
                StrokeThickness = key == "mic" ? 1.8 : 1.5,
            });
        }
        var slash = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("IconSlash"),
            Style = (Style)FindResource("StrokeIcon"),
            StrokeThickness = 1.8,
            Visibility = Visibility.Collapsed,
        };
        icon.Children.Add(slash);

        var button = new Button { Style = (Style)FindResource("ControlButton"), Content = new Viewbox { Width = 19, Height = 19, Child = icon } };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            toggle();
            UpdateControls();
        };
        var state = new TextBlock { FontSize = 10.5, Foreground = (Brush)FindResource("TertiaryText"), HorizontalAlignment = HorizontalAlignment.Center };
        ControlsGrid.Children.Add(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                button,
                new TextBlock { Text = label, FontSize = 11.5, Margin = new Thickness(0, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Center },
                state,
            },
        });
        _controls[key] = new QuickControl(button, slash, state);
    }

    /// <summary>Wi-Fi / Bluetooth light up blue when on; microphone and sound turn red (struck through) when muted.</summary>
    private void UpdateControls()
    {
        if (_controls.Count == 0) return;
        var blue = (Brush)FindResource("CalendarBrush");
        var red = (Brush)FindResource("WarningBrush");
        SetControl("wifi", _quick.WifiOn, blue, "Nyala", "Mati", slash: false);
        SetControl("bluetooth", _quick.BluetoothOn, blue, "Nyala", "Mati", slash: false);
        SetControl("mic", MicrophoneMute.Get(), red, "Dibisukan", "Aktif", slash: true);
        SetControl("sound", _volume is { HasDevice: true } volume ? volume.IsMuted : null, red, "Senyap", "Nyala", slash: true);
        SetControl("dark", QuickControls.DarkMode, new SolidColorBrush(Color.FromRgb(0x5E, 0x5C, 0xE6)), "Aktif", "Mati", slash: false);
    }

    private void SetControl(string key, bool? active, Brush activeBrush, string activeText, string inactiveText, bool slash)
    {
        var control = _controls[key];
        control.Button.IsEnabled = active is not null;
        control.Button.Background = active == true ? activeBrush : (Brush)FindResource("ControlOffBrush");
        control.Slash.Visibility = slash && active == true ? Visibility.Visible : Visibility.Collapsed;
        control.State.Text = active is null ? "Tidak ada" : active.Value ? activeText : inactiveText;
    }

    // ───────────────────────────── system page ─────────────────────────────

    private void UpdateSystem()
    {
        _stats.Sample();
        bool warm = _stats.CpuHistory.Count > 0; // the first reading only sets the baseline
        CpuText.Text = warm ? $"{_stats.Cpu * 100:0}%" : "…";
        CpuSpark.SetValues(_stats.CpuHistory);

        MemoryText.Text = $"{FormatBytes((long)_stats.MemoryUsed)} / {FormatBytes((long)_stats.MemoryTotal)}";
        SetProgress(MemoryUsed, MemoryFree, _stats.MemoryTotal > 0 ? (double)_stats.MemoryUsed / _stats.MemoryTotal : 0);

        NetDownText.Text = "↓ " + FormatRate(_stats.Download);
        NetUpText.Text = "↑ " + FormatRate(_stats.Upload);

        DiskLabel.Text = $"Disk {_stats.Drive}";
        DiskText.Text = _stats.DriveTotal > 0 ? $"{FormatBytes(_stats.DriveFree)} kosong" : "–";
        SetProgress(DiskUsed, DiskFree, _stats.DriveTotal > 0 ? 1 - (double)_stats.DriveFree / _stats.DriveTotal : 0);
    }

    // ───────────────────────────── screenshots & downloads ─────────────────────────────

    /// <summary>A new screenshot (big thumbnail) or a finished download (its icon): drag it out, open it, show it, or keep it on the shelf.</summary>
    private void ShowFile(string path, bool screenshot)
    {
        if (_hiddenForFullscreen || !System.IO.File.Exists(path)) return;
        _filePath = path;
        long size = 0;
        try
        {
            size = new System.IO.FileInfo(path).Length;
        }
        catch (System.IO.IOException)
        {
            // Being replaced already; show it without a size.
        }

        FileTitle.Text = screenshot ? "Screenshot" : "Unduhan selesai";
        FileName.Text = $"{System.IO.Path.GetFileName(path)} · {FormatBytes(size)}";
        FileThumb.Width = screenshot ? 92 : 40;
        FileThumb.Height = screenshot ? 54 : 40;
        ImageSource? thumbnail = screenshot ? LoadPicture(path, 184) : ShellThumbnail.Get(path, 80);
        FileThumb.Background = thumbnail is null
            ? (Brush)FindResource("ArtPlaceholder")
            : new ImageBrush(thumbnail) { Stretch = screenshot ? Stretch.UniformToFill : Stretch.Uniform };
        FileThumb.BorderThickness = new Thickness(screenshot ? 1 : 0);
        ShowActivity(ActivityKind.File, FilePanel, 400, screenshot ? 74 : 60, FileDuration);
    }

    private void OpenCurrentFile()
    {
        if (_filePath is null) return;
        OpenShelfItem(new ShelfItem(_filePath));
        EndActivity();
    }

    private void FileOpen_Click(object sender, RoutedEventArgs e) => OpenCurrentFile();

    private void FileFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_filePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        EndActivity();
    }

    private void FileShelf_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath is null) return;
        _shelf.Add([_filePath]);
        EndActivity();
    }

    /// <summary>Agenda card: what is on now / next, today and tomorrow (rebuilt at most once a minute).</summary>
    private void UpdateAgenda(DateTime now)
    {
        var state = _calendar.State;
        bool ready = state == CalendarState.Ready;
        AgendaHint.Visibility = state is CalendarState.Off or CalendarState.Failed ? Visibility.Visible : Visibility.Collapsed;
        AgendaList.Visibility = ready || state == CalendarState.Loading ? Visibility.Visible : Visibility.Collapsed;
        AgendaHintTitle.Text = state == CalendarState.Failed ? "Kalender tidak bisa dimuat" : "Hubungkan kalender";
        AgendaHintSubtitle.Text = state == CalendarState.Failed ? "Klik untuk cek link-nya" : "Agenda tampil di sini";

        string key = ready ? now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) : state.ToString();
        if (key == _agendaKey) return;
        _agendaKey = key;
        AgendaList.Children.Clear();

        if (state == CalendarState.Loading)
        {
            AgendaList.Children.Add(AgendaRow("Kalender", "Memuat agenda…", muted: true));
            return;
        }
        if (!ready) return;

        var today = now.Date;
        var events = _calendar.EventsAround(today);
        var soon = events.Where(e => e.End > now && e.Start < today.AddDays(2)).Take(2).ToList();
        foreach (var item in soon)
            AgendaList.Children.Add(AgendaRow(WhenLabel(item, now), item.Title, live: !item.AllDay && item.Start <= now));

        if (soon.Count == 0)
        {
            var next = events.FirstOrDefault(e => e.Start >= today.AddDays(2));
            AgendaList.Children.Add(next is null
                ? AgendaRow("Hari ini & besok", "Tidak ada agenda", muted: true)
                : AgendaRow("Berikutnya · " + next.Start.ToString("ddd d MMM", CultureInfo.CurrentCulture), next.Title, muted: true));
        }
    }

    private static string WhenLabel(CalendarEvent item, DateTime now)
    {
        var culture = CultureInfo.CurrentCulture;
        bool tomorrow = item.Start.Date > now.Date;
        if (item.AllDay) return tomorrow ? "Besok · sepanjang hari" : "Hari ini · sepanjang hari";
        if (item.Start <= now) return "Sekarang · s/d " + item.End.ToString("t", culture);
        string time = item.Start.ToString("t", culture);
        return tomorrow ? "Besok · " + time : time;
    }

    /// <summary>Coloured bar, then "when" above the title.</summary>
    private DockPanel AgendaRow(string when, string title, bool live = false, bool muted = false)
    {
        var accent = (Brush)FindResource("CalendarBrush");
        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 2, 8, 2),
            Background = muted ? (Brush)FindResource("TrackBrush") : accent,
        };
        DockPanel.SetDock(bar, Dock.Left);
        return new DockPanel
        {
            Margin = new Thickness(0, 2, 0, 2),
            Children =
            {
                bar,
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = when,
                            FontSize = 10.5,
                            FontWeight = live ? FontWeights.SemiBold : FontWeights.Normal,
                            Foreground = live ? accent : (Brush)FindResource("SecondaryText"),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        new TextBlock
                        {
                            Text = title.Length > 0 ? title : "(tanpa judul)",
                            FontSize = 12.5,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = (Brush)FindResource(muted ? "SecondaryText" : "PrimaryText"),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
            },
        };
    }

    private void OnCalendarChanged()
    {
        _settingsWindow?.RefreshCalendar();
        _agendaKey = "";
        _weekStripDate = default; // redraw the busy-day dots
        if (_view == View.Expanded) UpdateToday();
    }

    private void OpenCalendarDialog()
    {
        if (_calendarDialog is { } open)
        {
            open.Activate();
            return;
        }

        var dialog = _calendarDialog = new CalendarDialog(_settings.CalendarSource ?? "");
        dialog.Saved += source =>
        {
            _settings.CalendarSource = source;
            _settings.Save();
            _calendar.SetSource(source);
        };
        dialog.Closed += (_, _) => _calendarDialog = null;
        dialog.Show();
        dialog.Activate();
    }

    // ───────────────────────────── live equalizer & rim ─────────────────────────────

    /// <summary>
    /// The 30 fps loop behind the live equalizer and the breathing rim. Runs only while music plays
    /// and one of them is on screen; the audio listener only while an equalizer is.
    /// </summary>
    private void ConfigureVisuals()
    {
        bool playing = _media.HasSession && _media.IsPlaying && !_hiddenForFullscreen;
        bool equalizer = playing && (
            (_view == View.Compact && CompactEq.Visibility == Visibility.Visible) ||
            (_view == View.Expanded && _page == Page.Media) ||
            (_view == View.Activity && _activity == ActivityKind.Track));
        bool rim = playing && _settings.AccentRim && _view == View.Compact && ShowsCompactMedia && _artSource is not null;

        if (equalizer && _settings.LiveVisualizer) _spectrum.Start();
        else _spectrum.Stop();
        if (!playing || !_settings.LiveVisualizer) SetLiveAudio(false);

        SetRim(rim);
        if (equalizer || rim)
        {
            if (_visualTimer.IsEnabled) return;
            _lastVisualTick = Stopwatch.GetTimestamp();
            _visualTimer.Start();
        }
        else
        {
            _visualTimer.Stop();
        }
    }

    private void OnVisualTick(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Clamp((now - _lastVisualTick) / (double)Stopwatch.Frequency, 0, 0.1);
        _lastVisualTick = now;

        // Real audio takes over the moment it is heard; after a few silent seconds (player muted,
        // music on another output) the bars go back to the canned loop instead of lying flat.
        bool audible = _settings.LiveVisualizer && _spectrum.TryGetLevels(_bandLevels);
        if (audible) _lastAudible = DateTime.UtcNow;
        SetLiveAudio(audible || (_liveAudio && DateTime.UtcNow - _lastAudible < LiveAudioGrace));

        for (int i = 0; i < _shownLevels.Length; i++)
        {
            float target = audible ? _bandLevels[i] : 0;
            float rate = target > _shownLevels[i] ? 0.65f : 0.2f; // quick attack, gentle fall
            _shownLevels[i] += (target - _shownLevels[i]) * rate;
        }
        if (_liveAudio)
        {
            CompactEq.SetLevels(_shownLevels);
            MediaEq.SetLevels(_shownLevels);
            TrackEq.SetLevels(_shownLevels);
        }

        if (!_rimOn) return;
        _rimFade = Math.Min(1, _rimFade + dt / 0.4);
        double energy;
        if (_liveAudio)
        {
            energy = 0.6 * _shownLevels[0] + 0.25 * _shownLevels[1] + 0.15 * _shownLevels[2]; // mostly the kick and bass
        }
        else
        {
            _rimPhase = (_rimPhase + dt * 2 * Math.PI / 2.8) % (2 * Math.PI); // slow breathing
            energy = 0.5 + 0.5 * Math.Sin(_rimPhase);
        }
        IslandRim.Opacity = _rimFade * (0.3 + 0.7 * energy);
    }

    private void SetLiveAudio(bool live)
    {
        if (_liveAudio == live) return;
        _liveAudio = live;
        CompactEq.IsLive = MediaEq.IsLive = TrackEq.IsLive = live;
    }

    private void SetRim(bool on)
    {
        if (_rimOn == on) return;
        _rimOn = on;
        if (on)
        {
            _rimFade = 0;
            IslandRim.BeginAnimation(OpacityProperty, null);
            IslandRim.Opacity = 0;
        }
        else
        {
            IslandRim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        }
    }

    /// <summary>Brightest at the ends (next to the artwork and the equalizer), softer in the middle.</summary>
    private static LinearGradientBrush RimBrush(Color accent) => Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(accent, 0),
            new(Color.FromArgb(0x55, accent.R, accent.G, accent.B), 0.5),
            new(accent, 1),
        },
        new Point(0, 0.5),
        new Point(1, 0.5)));

    // ───────────────────────────── app notifications ─────────────────────────────

    private void OnAppNotification(AppNotification notification)
    {
        if (!_settings.ShowAppNotifications || _hiddenForFullscreen) return;

        var (name, icon) = AppInfo(notification.AppId);
        var picture = notification.ImagePath is { } path ? LoadPicture(path) : null;
        var main = picture ?? icon;

        NotifyTitle.Text = notification.Title;
        NotifyBody.Text = _settings.ShowNotificationContent ? notification.Body : "Pesan baru";
        NotifyBody.Visibility = NotifyBody.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifyApp.Text = name;
        NotifyPicture.Background = main is null ? (Brush)FindResource("ArtPlaceholder") : new ImageBrush(main) { Stretch = Stretch.UniformToFill };
        NotifyPicture.CornerRadius = new CornerRadius(picture is not null && notification.RoundImage ? 18 : 10);
        NotifyBell.Visibility = main is null ? Visibility.Visible : Visibility.Collapsed;

        // A contact photo gets the app's icon as a small badge, like on a phone.
        bool badge = picture is not null && icon is not null;
        NotifyBadge.Visibility = badge ? Visibility.Visible : Visibility.Collapsed;
        NotifyBadge.Background = badge ? new ImageBrush(icon) { Stretch = Stretch.UniformToFill } : null;

        _notification = notification;
        ShowActivity(ActivityKind.Notification, NotifyPanel, 400, 56, NotificationDuration);
        _banners.Hide(notification.AppId); // from now on this app's messages pop up here only, not twice
    }

    /// <summary>Display name and Start-menu icon of an app, cached per app id.</summary>
    private (string Name, BitmapSource? Icon) AppInfo(string appId)
    {
        if (!_appInfo.TryGetValue(appId, out var info))
        {
            info = (ShellThumbnail.GetAppName(appId) ?? FallbackAppName(appId), ShellThumbnail.GetAppIcon(appId, 64));
            _appInfo[appId] = info;
        }
        return info;
    }

    /// <summary>"5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App" → "WhatsAppDesktop", "com.squirrel.Discord.Discord" → "Discord".</summary>
    private static string FallbackAppName(string appId)
    {
        if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.GetFileNameWithoutExtension(appId.Replace('/', '\\').Split('\\')[^1]);
        string id = appId;
        int bang = id.IndexOf('!');
        if (bang > 0)
        {
            id = id[..bang];
            int underscore = id.LastIndexOf('_');
            if (underscore > 0) id = id[..underscore];
        }
        return id.Split('.', '\\', '/').LastOrDefault(p => p.Length > 0) ?? appId;
    }

    private static BitmapSource? LoadPicture(string path, int width = 72)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = width;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // deleted already, or not an image
        }
    }

    // ───────────────────────────── file shelf ("titipan") ─────────────────────────────

    private void OnShelfChanged()
    {
        int count = _shelf.Items.Count;
        IdleShelfBadge.Visibility = CompactShelfBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        IdleShelfCount.Text = CompactShelfCount.Text = count.ToString(CultureInfo.CurrentCulture);
        ShelfCount.Text = count > 0 ? $"  ·  {count} file" : "";

        ShelfTiles.Children.Clear();
        bool overflow = count > MaxShelfTiles;
        var shown = _shelf.Items.Take(overflow ? MaxShelfTiles - 1 : MaxShelfTiles).ToList();
        foreach (var item in shown) ShelfTiles.Children.Add(CreateShelfTile(item));
        if (overflow) ShelfTiles.Children.Add(CreateMoreTile(_shelf.Items.Skip(shown.Count).ToList()));

        if (IsLoaded) ApplyState();
    }

    /// <summary>Drop zone while files hover over the island (or when the shelf is empty); tiles otherwise.</summary>
    private void UpdateShelfMode()
    {
        bool empty = _shelf.Items.Count == 0;
        ShelfDropZone.Visibility = _dropActive || empty ? Visibility.Visible : Visibility.Collapsed;
        ShelfContent.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ShelfDropTitle.Text = _dropActive ? "Lepaskan untuk menitip file" : "Seret file ke sini";
    }

    private FrameworkElement CreateShelfTile(ShelfItem item)
    {
        var thumbnail = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(),
        };
        RenderOptions.SetBitmapScalingMode(thumbnail, BitmapScalingMode.HighQuality);
        SetThumbnail(thumbnail, item);

        var name = new TextBlock
        {
            Text = item.Name,
            Width = 64,
            MaxHeight = 30,
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("SecondaryText"),
        };

        var remove = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -5, 3, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Child = new Viewbox
            {
                Width = 7,
                Height = 7,
                Child = new Grid
                {
                    Width = 16,
                    Height = 16,
                    Children = { new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IconClose"), Style = (Style)FindResource("StrokeIcon"), StrokeThickness = 3 } },
                },
            },
        };
        remove.MouseLeftButtonDown += (_, e) => e.Handled = true;
        remove.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _shelf.Remove(item);
        };

        var tile = new Grid
        {
            Width = 64,
            Margin = new Thickness(4, 0, 4, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = item.Path,
            Children = { new StackPanel { Children = { thumbnail, name } }, remove },
        };

        // Hover: lift the thumbnail a little and offer the × button.
        tile.MouseEnter += (_, _) =>
        {
            remove.Visibility = Visibility.Visible;
            AnimateScale(thumbnail, 1.07);
        };
        tile.MouseLeave += (_, _) =>
        {
            remove.Visibility = Visibility.Collapsed;
            AnimateScale(thumbnail, 1);
        };
        AttachDragOut(tile, () => [item], onClick: () => OpenShelfItem(item));
        return tile;
    }

    /// <summary>"+N" tile when there are more files than fit; dragging it takes all of those.</summary>
    private FrameworkElement CreateMoreTile(IReadOnlyList<ShelfItem> rest)
    {
        var tile = new StackPanel
        {
            Width = 64,
            Margin = new Thickness(4, 0, 4, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = string.Join(Environment.NewLine, rest.Select(i => i.Name)),
            Children =
            {
                new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(10),
                    Background = (Brush)FindResource("TrackBrush"),
                    Child = new TextBlock
                    {
                        Text = $"+{rest.Count}",
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
                new TextBlock
                {
                    Text = "lainnya",
                    Margin = new Thickness(0, 5, 0, 0),
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    Foreground = (Brush)FindResource("SecondaryText"),
                },
            },
        };
        AttachDragOut(tile, () => rest, onClick: null);
        return tile;
    }

    private void SetThumbnail(Border target, ShelfItem item)
    {
        if (item.Thumbnail is { } cached)
        {
            target.Background = new ImageBrush(cached) { Stretch = Stretch.Uniform };
            return;
        }

        // Explorer thumbnails can take a moment (big photos, PDFs): fetch them after the UI has settled.
        Dispatcher.InvokeAsync(() =>
        {
            item.Thumbnail ??= ShellThumbnail.Get(item.Path, 96);
            if (item.Thumbnail is { } thumbnail) target.Background = new ImageBrush(thumbnail) { Stretch = Stretch.Uniform };
        }, DispatcherPriority.Background);
    }

    private static void AnimateScale(UIElement element, double to)
    {
        if (element.RenderTransform is not ScaleTransform scale) return;
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = Ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>Press + move = drag the files out; press + release = <paramref name="onClick"/> (e.g. open it).</summary>
    private void AttachDragOut(FrameworkElement element, Func<IReadOnlyList<ShelfItem>> items, Action? onClick)
    {
        element.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; // not a carousel swipe
            _pressedTile = element;
            _pressPoint = e.GetPosition(this);
            element.CaptureMouse();
        };
        element.MouseMove += (_, e) =>
        {
            if (!ReferenceEquals(_pressedTile, element) || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - _pressPoint.X) < 5 && Math.Abs(point.Y - _pressPoint.Y) < 5) return;

            _pressedTile = null;
            element.ReleaseMouseCapture();
            DragOut(items(), element);
        };
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!ReferenceEquals(_pressedTile, element)) return;
            _pressedTile = null;
            element.ReleaseMouseCapture();
            onClick?.Invoke();
        };
    }

    /// <summary>Hands the files to whatever they are dropped on (Explorer, chat apps, editors...).</summary>
    private void DragOut(IReadOnlyList<ShelfItem> items, DependencyObject source)
    {
        if (items.Count == 0) return;

        var effect = DragDropEffects.None;
        _draggingOut = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, items.Select(i => i.Path).ToArray());
            effect = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        finally
        {
            _draggingOut = false;
        }

        // Delivered: the shelf was only a stopover. Explorer's moves can report "None", so also prune.
        if (effect != DragDropEffects.None) _shelf.RemoveRange(items);
        _shelf.PruneMissing();
    }

    private static void OpenShelfItem(ShelfItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e) => _shelf.Clear();

    private void ShelfBadge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activity == ActivityKind.TimerDone) return; // let the click dismiss the alarm
        e.Handled = true;
        _hoverTimer.Stop();
        if (_view == View.Expanded)
        {
            GoToPage(Page.Shelf);
            return;
        }
        _pendingPage = Page.Shelf;
        SetExpanded(true);
    }

    // DragEnter and DragOver: files are hovering over the island.
    private void Shape_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        bool files = !_draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None; // never Move: we only keep a reference
        if (!files) return;

        _dropLeaveTimer.Stop();
        if (_dropActive) return;

        _dropActive = true;
        _hoverTimer.Stop();
        _leaveTimer.Stop();
        bool wasOpen = _view == View.Expanded;
        if (!wasOpen)
        {
            _page = Page.Shelf;
            _snapStrip = true;
        }
        ApplyState();
        if (wasOpen) GoToPage(Page.Shelf);

        // "Marching ants" around the drop zone while it is armed.
        var ants = new DoubleAnimation(0, -14, TimeSpan.FromSeconds(0.7)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(ants, 30);
        ShelfDropOutline.Stroke = (Brush)FindResource("PrimaryText");
        ShelfDropOutline.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, ants);
    }

    // Also raised when moving between child elements, hence the short grace timer.
    private void Shape_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dropLeaveTimer.Stop();
        _dropLeaveTimer.Start();
    }

    private void Shape_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.Copy; // tell the source we copied: it must never delete or move the original
        _dropLeaveTimer.Stop();
        if (_draggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            EndDropHover();
            return;
        }

        // Stay open on the shelf to show what landed; close by itself if the pointer has moved on.
        // (State first: adding raises Changed, which re-applies the state.)
        StopDropAnimation();
        _dropActive = false;
        _expanded = true;
        _page = Page.Shelf;
        _shelf.Add(paths);
        ApplyState();
        StartLeaveTimer(TimeSpan.FromSeconds(1.6));
    }

    private void EndDropHover()
    {
        if (!_dropActive) return;
        _dropActive = false;
        StopDropAnimation();
        ApplyState();
    }

    private void StopDropAnimation()
    {
        ShelfDropOutline.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
        ShelfDropOutline.Stroke = (Brush)FindResource("SecondaryText");
    }

    // ───────────────────────────── mouse ─────────────────────────────

    private void Shape_MouseEnter(object sender, MouseEventArgs e)
    {
        _leaveTimer.Stop();

        // Reading a notification or a file preview: keep it up and don't open the island underneath the pointer.
        if (_activity is ActivityKind.Notification or ActivityKind.File)
        {
            _activityTimer.Stop();
            return;
        }
        if (_expanded) return;
        _hoverPoint = e.GetPosition(this);
        _hoverTimer.Start();
    }

    /// <summary>Open on hover only once the pointer rests: just passing over (on the way to a browser tab) does nothing.</summary>
    private void Shape_MouseMove(object sender, MouseEventArgs e)
    {
        if (_expanded || !_hoverTimer.IsEnabled) return;
        var point = e.GetPosition(this);
        if ((point - _hoverPoint).Length < 4) return;
        _hoverPoint = point;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void Shape_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        if (_activity is ActivityKind.Notification or ActivityKind.File)
        {
            _activityTimer.Interval = TimeSpan.FromSeconds(2);
            _activityTimer.Start();
        }
        if (_expanded && !_pointerDown && !_draggingOut) StartLeaveTimer(TimeSpan.FromMilliseconds(320));
    }

    private void StartLeaveTimer(TimeSpan delay)
    {
        _leaveTimer.Stop();
        _leaveTimer.Interval = delay;
        _leaveTimer.Start();
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

        // Clicking a file preview opens the file.
        if (_activity == ActivityKind.File)
        {
            OpenCurrentFile();
            return;
        }

        // Clicking a notification opens the app it came from.
        if (_activity == ActivityKind.Notification && _notification is { } notification)
        {
            EndActivity();
            LaunchApp(notification.AppId);
            return;
        }

        // Otherwise a click expands right away (handy for touch screens and pens).
        SetExpanded(true);
    }

    // ───────────────────────────── menu & settings ─────────────────────────────

    private void Menu_Opened(object sender, RoutedEventArgs e) => MenuTimerStop.IsEnabled = _timer.IsActive;

    private void MenuCalendar_Click(object sender, RoutedEventArgs e) => OpenCalendarDialog();

    private void MenuSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        Close();
        Application.Current.Shutdown();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { } open)
        {
            open.Activate();
            return;
        }

        var window = _settingsWindow = new SettingsWindow(_settings, CalendarStatusText);
        window.Changed += ApplySetting;
        window.CalendarRequested += OpenCalendarDialog;
        window.Closed += (_, _) => _settingsWindow = null;
        window.Show();
        window.Activate();
    }

    private string CalendarStatusText() => _calendar.State switch
    {
        CalendarState.Off => "Belum terhubung",
        CalendarState.Loading => "Memuat…",
        CalendarState.Failed => "Link tidak bisa dimuat",
        _ => "Terhubung",
    };

    /// <summary>Side effects of a setting the settings window just changed (and saved).</summary>
    private void ApplySetting(string name)
    {
        switch (name)
        {
            case nameof(AppSettings.Style):
                ApplyStyle();
                ApplyReservation(); // the notch needs a lower strip than the floating island
                break;
            case nameof(AppSettings.ReserveTopSpace):
                ApplyReservation();
                PositionWindow();
                break;
            case nameof(AppSettings.ShowPrivacy):
                UpdatePrivacyDots();
                break;
            case nameof(AppSettings.ShowLyrics):
                LoadLyrics();
                break;
            case nameof(AppSettings.ShowAppNotifications):
                if (_settings.ShowAppNotifications)
                {
                    _notifications?.Start();
                    _banners.ApplyAll();
                }
                else
                {
                    _notifications?.Stop();
                    _banners.RestoreAll(forget: false); // the island no longer shows them: Windows must again
                }
                break;
            case nameof(AppSettings.HideWindowsBanners):
                if (!_settings.HideWindowsBanners) _banners.RestoreAll(forget: true);
                break;
            case nameof(AppSettings.ShowWeather):
            case nameof(AppSettings.WeatherCity):
                _weather.Configure(_settings.ShowWeather, _settings.WeatherCity);
                break;
            case nameof(AppSettings.ShowScreenshots):
                if (_screenshots is not null) _screenshots.Enabled = _settings.ShowScreenshots;
                break;
            case nameof(AppSettings.ShowDownloads):
                if (_downloads is not null) _downloads.Enabled = _settings.ShowDownloads;
                break;
            case nameof(AppSettings.ShowControlsPage):
                if (_settings.ShowControlsPage) _quick.Start();
                break;
        }
        ApplyState(); // pages, sizes, visuals: cheap to re-evaluate for any change
    }

    private void ApplyStyle()
    {
        bool notch = _settings.Style == IslandStyle.Notch;
        Shape.Margin = new Thickness(0, notch ? 0 : IslandTopMargin, 0, 0);
        ShoulderLeft.Visibility = ShoulderRight.Visibility = notch ? Visibility.Visible : Visibility.Collapsed;
        IslandRim.BorderThickness = notch ? new Thickness(1.5, 0, 1.5, 1.5) : new Thickness(1.5);
        _animator.SquareTopCorners = notch;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static void SetProgress(ColumnDefinition done, ColumnDefinition rest, double fraction)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        done.Width = new GridLength(fraction, GridUnitType.Star);
        rest.Width = new GridLength(1 - fraction, GridUnitType.Star);
    }

    /// <summary>1536 → "1,5 KB", 6.2e9 → "5,8 GB" (in the user's number format).</summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };

    private static string FormatRate(double bytesPerSecond) => bytesPerSecond >= 1 << 20
        ? $"{bytesPerSecond / (1 << 20):0.0} MB/s"
        : $"{bytesPerSecond / 1024:0} KB/s";

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
