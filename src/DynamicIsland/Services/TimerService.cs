using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>
/// Countdown timer. Time is tracked against the wall clock and a single one-shot timer is armed for
/// the end, so nothing ticks in the background; the UI only refreshes while the countdown is visible.
/// </summary>
internal sealed class TimerService
{
    private readonly DispatcherTimer _alarm = new();
    private DateTime _endsAt;
    private TimeSpan _pausedRemaining;

    public TimerService() => _alarm.Tick += (_, _) => OnAlarm();

    /// <summary>Started, paused, resumed, extended or stopped.</summary>
    public event Action? Changed;

    /// <summary>The countdown reached zero; carries the total duration that elapsed.</summary>
    public event Action<TimeSpan>? Finished;

    public bool IsActive { get; private set; }
    public bool IsPaused { get; private set; }
    public TimeSpan Total { get; private set; }

    public TimeSpan Remaining
    {
        get
        {
            if (!IsActive) return TimeSpan.Zero;
            var remaining = IsPaused ? _pausedRemaining : _endsAt - DateTime.UtcNow;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }

    public void Start(TimeSpan duration)
    {
        Total = duration;
        IsActive = true;
        IsPaused = false;
        _endsAt = DateTime.UtcNow + duration;
        Arm();
        Changed?.Invoke();
    }

    public void TogglePause()
    {
        if (!IsActive) return;

        if (IsPaused)
        {
            _endsAt = DateTime.UtcNow + _pausedRemaining;
            IsPaused = false;
            Arm();
        }
        else
        {
            _pausedRemaining = Remaining;
            IsPaused = true;
            _alarm.Stop();
        }
        Changed?.Invoke();
    }

    public void Add(TimeSpan extra)
    {
        if (!IsActive) return;

        Total += extra;
        if (IsPaused)
        {
            _pausedRemaining += extra;
        }
        else
        {
            _endsAt += extra;
            Arm();
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (!IsActive) return;
        IsActive = false;
        IsPaused = false;
        _alarm.Stop();
        Changed?.Invoke();
    }

    /// <summary>Re-arms against the wall clock, e.g. after the PC wakes from sleep.</summary>
    public void Resync()
    {
        if (IsActive && !IsPaused) Arm();
    }

    private void Arm()
    {
        _alarm.Stop();
        var remaining = _endsAt - DateTime.UtcNow;
        _alarm.Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        _alarm.Start();
    }

    private void OnAlarm()
    {
        // Timers can fire a hair early; wait for the real end instead of finishing at "0:01".
        if (DateTime.UtcNow < _endsAt - TimeSpan.FromMilliseconds(20))
        {
            Arm();
            return;
        }

        var total = Total;
        Stop();
        Finished?.Invoke(total);
    }
}
