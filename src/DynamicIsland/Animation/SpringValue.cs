using System.Windows.Media;

namespace DynamicIsland.Animation;

/// <summary>
/// A single spring-driven number (e.g. the carousel offset). Like <see cref="IslandAnimator"/>, it is
/// attached to the render loop only while it moves.
/// </summary>
internal sealed class SpringValue(Action<double> apply)
{
    private readonly Spring _spring = new();
    private TimeSpan _lastFrame;
    private bool _running;

    public double Value => _spring.Value;
    public double Target => _spring.Target;

    /// <summary>Jumps straight to <paramref name="value"/> (used while a finger drags it).</summary>
    public void Snap(double value)
    {
        Stop();
        _spring.Snap(value);
        apply(value);
    }

    public void AnimateTo(double target, double velocity = 0, double response = 0.4, double dampingRatio = 0.84)
    {
        _spring.Retarget(target, response, dampingRatio);
        if (velocity != 0) _spring.Push(velocity);
        Start();
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;

        double dt = _lastFrame == TimeSpan.Zero ? 1.0 / 60 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        bool settled = _spring.Step(Math.Clamp(dt, 0, 1.0 / 20));
        apply(_spring.Value);
        if (settled) Stop();
    }
}
