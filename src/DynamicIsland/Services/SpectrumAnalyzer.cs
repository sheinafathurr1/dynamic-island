namespace DynamicIsland.Services;

/// <summary>
/// Turns the last few milliseconds of audio into one level per equalizer bar: a Hann-windowed FFT,
/// the energy of a few log-spaced frequency bands, and an automatic gain per band so the bars stay
/// lively whether the music is quiet or loud. Pure maths, no Windows APIs.
/// </summary>
internal sealed class SpectrumAnalyzer
{
    /// <summary>Bars of the equalizer: bass, low mids, high mids, treble.</summary>
    public static readonly double[] BandEdges = [40, 160, 640, 2500, 10000];

    public const int BandCount = 4;

    // Anything quieter than this is treated as silence (no music, or a muted player).
    private const double SilenceDb = -62;

    // Each bar shows the last ~DynamicRange dB below its own recent peak.
    private const double DynamicRange = 30;

    // How fast a band's reference peak sinks back after a loud part, in dB per second.
    private const double PeakReleasePerSecond = 7;

    private readonly int _size;
    private readonly double[] _window;
    private readonly double[] _real;
    private readonly double[] _imag;
    private readonly int[] _bitReverse;
    private readonly (int From, int To)[] _bins;
    private readonly double[] _peaks = new double[BandCount];

    public SpectrumAnalyzer(int sampleRate)
    {
        // ~21 ms of audio: short enough to follow drums, long enough to resolve the bass band.
        _size = sampleRate > 64000 ? 2048 : 1024;
        SampleRate = sampleRate;

        _window = new double[_size];
        for (int i = 0; i < _size; i++)
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (_size - 1));

        _real = new double[_size];
        _imag = new double[_size];
        _bitReverse = new int[_size];
        int bits = (int)Math.Log2(_size);
        for (int i = 0; i < _size; i++)
        {
            int reversed = 0;
            for (int b = 0; b < bits; b++)
                reversed |= ((i >> b) & 1) << (bits - 1 - b);
            _bitReverse[i] = reversed;
        }

        double binWidth = (double)sampleRate / _size;
        _bins = new (int, int)[BandCount];
        for (int band = 0; band < BandCount; band++)
        {
            int from = Math.Max(1, (int)Math.Round(BandEdges[band] / binWidth));
            int to = Math.Min(_size / 2 - 1, Math.Max(from, (int)Math.Round(BandEdges[band + 1] / binWidth) - 1));
            _bins[band] = (from, to);
        }

        Array.Fill(_peaks, SilenceDb + DynamicRange);
    }

    public int SampleRate { get; }

    /// <summary>Samples needed per analysis.</summary>
    public int WindowSize => _size;

    /// <summary>
    /// Analyses the most recent <see cref="WindowSize"/> mono samples (oldest first) and writes a
    /// 0..1 level per band. <paramref name="elapsedSeconds"/> is the time since the previous call;
    /// it paces the automatic gain. Returns false when the audio is silent.
    /// </summary>
    public bool Analyze(ReadOnlySpan<float> samples, Span<float> levels, double elapsedSeconds)
    {
        if (samples.Length < _size) throw new ArgumentException("Not enough samples.", nameof(samples));
        samples = samples[^_size..];

        for (int i = 0; i < _size; i++)
        {
            _real[_bitReverse[i]] = samples[i] * _window[i];
            _imag[i] = 0;
        }
        Transform();

        // A full-scale sine through a Hann window peaks at N/4: that is 0 dB.
        double reference = _size / 4.0;
        bool audible = false;
        double release = PeakReleasePerSecond * Math.Clamp(elapsedSeconds, 0, 0.5);

        for (int band = 0; band < BandCount; band++)
        {
            var (from, to) = _bins[band];
            double energy = 0;
            for (int k = from; k <= to; k++)
                energy += _real[k] * _real[k] + _imag[k] * _imag[k];

            double db = 10 * Math.Log10(energy / (reference * reference) + 1e-14);
            if (db > SilenceDb) audible = true;

            // Peak follower: jumps up at once, sinks slowly, never below the silence floor.
            _peaks[band] = Math.Max(db, Math.Max(_peaks[band] - release, SilenceDb + DynamicRange));
            double level = (db - (_peaks[band] - DynamicRange)) / DynamicRange;
            levels[band] = (float)Math.Clamp(level, 0, 1);
        }

        if (!audible) levels[..BandCount].Clear();
        return audible;
    }

    /// <summary>In-place iterative radix-2 FFT of the bit-reversed buffers.</summary>
    private void Transform()
    {
        for (int length = 2; length <= _size; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            double stepReal = Math.Cos(angle), stepImag = Math.Sin(angle);
            int half = length >> 1;
            for (int start = 0; start < _size; start += length)
            {
                double wReal = 1, wImag = 0;
                for (int k = 0; k < half; k++)
                {
                    int a = start + k, b = a + half;
                    double tReal = _real[b] * wReal - _imag[b] * wImag;
                    double tImag = _real[b] * wImag + _imag[b] * wReal;
                    _real[b] = _real[a] - tReal;
                    _imag[b] = _imag[a] - tImag;
                    _real[a] += tReal;
                    _imag[a] += tImag;

                    double next = wReal * stepReal - wImag * stepImag;
                    wImag = wReal * stepImag + wImag * stepReal;
                    wReal = next;
                }
            }
        }
    }
}
