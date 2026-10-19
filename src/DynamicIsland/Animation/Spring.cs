namespace DynamicIsland.Animation;

/// <summary>
/// Damped harmonic oscillator, parameterised like SwiftUI springs: <c>response</c> is roughly the
/// period in seconds and <c>dampingRatio</c> &lt; 1 gives the slight overshoot of Apple's island.
/// </summary>
internal sealed class Spring
{
    private double _stiffness = 170;
    private double _damping = 26;
    private double _velocity;

    public double Value { get; private set; }
    public double Target { get; private set; }

    public void Snap(double value)
    {
        Value = Target = value;
        _velocity = 0;
    }

    public void Retarget(double target, double response, double dampingRatio)
    {
        double omega = 2 * Math.PI / response;
        _stiffness = omega * omega;
        _damping = 2 * dampingRatio * omega;
        Target = target;
    }

    /// <summary>Advances the simulation; returns true once it has come to rest.</summary>
    public bool Step(double seconds)
    {
        // Small fixed sub-steps keep semi-implicit Euler stable even after a dropped frame.
        const double maxStep = 1.0 / 240;
        int steps = Math.Max(1, (int)Math.Ceiling(seconds / maxStep));
        double h = seconds / steps;

        for (int i = 0; i < steps; i++)
        {
            double acceleration = -_stiffness * (Value - Target) - _damping * _velocity;
            _velocity += acceleration * h;
            Value += _velocity * h;
        }

        if (Math.Abs(Value - Target) < 0.1 && Math.Abs(_velocity) < 1)
        {
            Snap(Target);
            return true;
        }
        return false;
    }
}
