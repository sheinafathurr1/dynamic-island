using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland.Animation;

/// <summary>
/// Morphs the island's width, height and corner radius with springs. It hooks the render loop only
/// while something is moving, so an idle island renders zero frames.
/// </summary>
internal sealed class IslandAnimator
{
    private readonly Border _island;
    private readonly RectangleGeometry _clip = new();
    private readonly Spring _width = new();
    private readonly Spring _height = new();
    private readonly Spring _radius = new();
    private TimeSpan _lastFrame;
    private bool _running;
    private bool _squareTopCorners;

    public IslandAnimator(Border island, UIElement content)
    {
        _island = island;
        content.Clip = _clip;
    }

    /// <summary>Notch style: flat top edge, only the bottom corners are rounded.</summary>
    public bool SquareTopCorners
    {
        get => _squareTopCorners;
        set
        {
            _squareTopCorners = value;
            Apply();
        }
    }

    public void Snap(double width, double height, double radius)
    {
        _width.Snap(width);
        _height.Snap(height);
        _radius.Snap(radius);
        Apply();
    }

    public void AnimateTo(double width, double height, double radius)
    {
        if (width == _width.Target && height == _height.Target && radius == _radius.Target) return;

        // Grow with a playful bounce, shrink calmly.
        bool growing = width * height > _width.Target * _height.Target;
        double response = growing ? 0.46 : 0.38;
        double damping = growing ? 0.7 : 0.88;

        _width.Retarget(width, response, damping);
        _height.Retarget(height, response, damping);
        _radius.Retarget(radius, response, damping);

        if (_running) return;
        _running = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return; // WPF can raise Rendering more than once per frame

        double dt = _lastFrame == TimeSpan.Zero ? 1.0 / 60 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        dt = Math.Clamp(dt, 0, 1.0 / 20);

        // Non-short-circuit '&' so every spring advances each frame.
        bool settled = _width.Step(dt) & _height.Step(dt) & _radius.Step(dt);
        Apply();

        if (settled)
        {
            CompositionTarget.Rendering -= OnRendering;
            _running = false;
        }
    }

    private void Apply()
    {
        double width = Math.Max(2, _width.Value);
        double height = Math.Max(2, _height.Value);
        double radius = Math.Clamp(_radius.Value, 0, Math.Min(width, height) / 2);

        _island.Width = width;
        _island.Height = height;
        _island.CornerRadius = _squareTopCorners ? new CornerRadius(0, 0, radius, radius) : new CornerRadius(radius);

        _clip.Rect = new Rect(0, 0, width, height);
        _clip.RadiusX = _clip.RadiusY = radius;
    }
}
