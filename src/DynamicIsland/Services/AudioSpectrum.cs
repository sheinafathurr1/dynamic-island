using System.Diagnostics;
using System.Runtime.InteropServices;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Listens to what the default speakers / headphones are playing (WASAPI loopback: nothing is
/// recorded or stored, and the microphone is never touched) and turns it into equalizer levels.
/// Runs on its own thread, and only between <see cref="Start"/> and <see cref="Stop"/>, which the
/// island calls while music plays on screen.
/// </summary>
internal sealed class AudioSpectrum : IDisposable
{
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    private const int E_NOTFOUND = unchecked((int)0x80070490);
    private const long BufferDuration = 2_000_000; // 200 ms, in 100 ns units
    private static readonly long StaleTicks = Stopwatch.Frequency / 4;
    private static readonly long AnalysisTicks = Stopwatch.Frequency / 33;

    private enum Outcome
    {
        Stopped,
        Reconnect,
        Unavailable,
    }

    private readonly object _gate = new();
    private readonly float[] _levels = new float[SpectrumAnalyzer.BandCount];
    private long _audibleAt;
    private int _generation;
    private bool _wanted;
    private volatile bool _unavailable;

    /// <summary>Begins listening (no-op when already running, or when this PC has nothing to listen to).</summary>
    public void Start()
    {
        if (_wanted || _unavailable) return;
        _wanted = true;
        int generation = Interlocked.Increment(ref _generation);
        new Thread(() => Run(generation)) { IsBackground = true, Name = "Audio spectrum" }.Start();
    }

    /// <summary>Stops listening; the thread notices within a few milliseconds and releases the device.</summary>
    public void Stop()
    {
        if (!_wanted) return;
        _wanted = false;
        Interlocked.Increment(ref _generation);
        lock (_gate)
        {
            Array.Clear(_levels);
            _audibleAt = 0;
        }
    }

    /// <summary>The default output changed: reconnect to the new one (which may work where the old one did not).</summary>
    public void Reset()
    {
        bool wanted = _wanted;
        Stop();
        _unavailable = false;
        if (wanted) Start();
    }

    /// <summary>Copies the latest levels (0..1 per band); false when nothing audible was heard just now.</summary>
    public bool TryGetLevels(Span<float> levels)
    {
        lock (_gate)
        {
            _levels.CopyTo(levels);
            return _audibleAt != 0 && Stopwatch.GetTimestamp() - _audibleAt < StaleTicks;
        }
    }

    public void Dispose() => Stop();

    private bool IsCurrent(int generation) => Volatile.Read(ref _generation) == generation;

    private void Run(int generation)
    {
        while (IsCurrent(generation))
        {
            Outcome outcome;
            try
            {
                outcome = Capture(generation);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                outcome = Outcome.Unavailable;
            }

            if (outcome == Outcome.Unavailable) _unavailable = true; // until the output device changes
            if (outcome != Outcome.Reconnect) return;
            Thread.Sleep(500); // the device went away (unplugged, switched): give Windows a moment
        }
    }

    private Outcome Capture(int generation)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        nint format = 0;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(CoreAudio.ERender, CoreAudio.EMultimedia, out device);
            if (hr == E_NOTFOUND || device is null) return Outcome.Unavailable; // no speakers or headphones
            if (hr != 0) return Fail("GetDefaultAudioEndpoint", hr);

            var clientId = typeof(IAudioClient).GUID;
            hr = device.Activate(ref clientId, CoreAudio.CLSCTX_ALL, 0, out var instance);
            if (hr != 0 || instance is not IAudioClient audioClient) return Fail("Activate", hr);
            client = audioClient;

            hr = client.GetMixFormat(out format);
            if (hr != 0) return Fail("GetMixFormat", hr);
            if (ReadFormat(format) is not { } wave) return Outcome.Unavailable;

            hr = client.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED, CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK, BufferDuration, 0, format, 0);
            if (hr != 0) return Fail("Initialize", hr);

            var captureId = typeof(IAudioCaptureClient).GUID;
            hr = client.GetService(ref captureId, out var service);
            if (hr != 0 || service is not IAudioCaptureClient captureClient) return Fail("GetService", hr);
            capture = captureClient;

            hr = client.Start();
            if (hr != 0) return Fail("Start", hr);

            return Pump(generation, capture, wave);
        }
        finally
        {
            try
            {
                client?.Stop();
            }
            catch (COMException)
            {
                // Already gone with the device.
            }
            if (format != 0) Marshal.FreeCoTaskMem(format);
            foreach (var item in new object?[] { capture, client, device, enumerator })
            {
                if (item is not null) Marshal.ReleaseComObject(item);
            }
        }
    }

    /// <summary>Reads packets as they come, mixes them to mono and analyses ~30 times a second.</summary>
    private Outcome Pump(int generation, IAudioCaptureClient capture, WaveFormat wave)
    {
        var analyzer = new SpectrumAnalyzer(wave.SampleRate);
        int size = analyzer.WindowSize;
        var ring = new float[size];
        var window = new float[size];
        var bands = new float[SpectrumAnalyzer.BandCount];
        float[] floats = [];
        short[] shorts = [];
        int ringPosition = 0;
        int fresh = 0;
        long lastAnalysis = Stopwatch.GetTimestamp();

        while (IsCurrent(generation))
        {
            int hr;
            while ((hr = capture.GetNextPacketSize(out uint packet)) == 0 && packet > 0)
            {
                hr = capture.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _);
                if (hr != 0) break;

                int count = (int)frames * wave.Channels;
                bool silent = (flags & CoreAudio.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                if (!silent && wave.IsFloat)
                {
                    if (floats.Length < count) floats = new float[count];
                    Marshal.Copy(data, floats, 0, count);
                }
                else if (!silent)
                {
                    if (shorts.Length < count) shorts = new short[count];
                    Marshal.Copy(data, shorts, 0, count);
                }

                // Down-mix to mono into the ring buffer.
                for (int frame = 0; frame < frames; frame++)
                {
                    float sum = 0;
                    if (!silent)
                    {
                        int offset = frame * wave.Channels;
                        for (int channel = 0; channel < wave.Channels; channel++)
                            sum += wave.IsFloat ? floats[offset + channel] : shorts[offset + channel] / 32768f;
                    }
                    ring[ringPosition] = sum / wave.Channels;
                    ringPosition = (ringPosition + 1) % size;
                }
                fresh += (int)frames;
                capture.ReleaseBuffer(frames);
            }
            if (hr == AUDCLNT_E_DEVICE_INVALIDATED) return Outcome.Reconnect;
            if (hr != 0) return Fail("GetBuffer", hr);

            long now = Stopwatch.GetTimestamp();
            if (now - lastAnalysis >= AnalysisTicks)
            {
                double elapsed = (now - lastAnalysis) / (double)Stopwatch.Frequency;
                lastAnalysis = now;

                // Nothing arrives while nothing plays: that is silence, not "the same as before".
                bool audible = false;
                if (fresh > 0)
                {
                    Array.Copy(ring, ringPosition, window, 0, size - ringPosition);
                    Array.Copy(ring, 0, window, size - ringPosition, ringPosition);
                    audible = analyzer.Analyze(window, bands, elapsed);
                    fresh = 0;
                }
                lock (_gate)
                {
                    if (audible)
                    {
                        bands.CopyTo(_levels, 0);
                        _audibleAt = now;
                    }
                    else
                    {
                        Array.Clear(_levels);
                    }
                }
            }

            Thread.Sleep(15);
        }
        return Outcome.Stopped;
    }

    private readonly record struct WaveFormat(int SampleRate, int Channels, bool IsFloat);

    /// <summary>Shared-mode mix formats are 32-bit float in practice; 16-bit PCM is accepted too.</summary>
    private static WaveFormat? ReadFormat(nint format)
    {
        ushort tag = (ushort)Marshal.ReadInt16(format, 0);
        int channels = Marshal.ReadInt16(format, 2);
        int sampleRate = Marshal.ReadInt32(format, 4);
        int bits = Marshal.ReadInt16(format, 14);
        if (tag == CoreAudio.WAVE_FORMAT_EXTENSIBLE)
        {
            var subFormat = Marshal.PtrToStructure<Guid>(format + 24);
            tag = subFormat == CoreAudio.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT ? CoreAudio.WAVE_FORMAT_IEEE_FLOAT
                : subFormat == CoreAudio.KSDATAFORMAT_SUBTYPE_PCM ? CoreAudio.WAVE_FORMAT_PCM
                : (ushort)0;
        }

        if (channels <= 0 || sampleRate < 8000) return null;
        if (tag == CoreAudio.WAVE_FORMAT_IEEE_FLOAT && bits == 32) return new WaveFormat(sampleRate, channels, IsFloat: true);
        if (tag == CoreAudio.WAVE_FORMAT_PCM && bits == 16) return new WaveFormat(sampleRate, channels, IsFloat: false);
        return null;
    }

    private static Outcome Fail(string step, int hr)
    {
        Log.Error(new COMException($"Audio visualizer: {step} failed", hr));
        return Outcome.Unavailable;
    }
}
