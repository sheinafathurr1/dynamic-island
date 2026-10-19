using System.IO;
using System.Net.NetworkInformation;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// CPU, memory, network speed and free disk space for the system page. Nothing runs in the
/// background: the page calls <see cref="Sample"/> once a second while it is on screen.
/// </summary>
internal sealed class SystemStats
{
    public const int HistoryLength = 40;

    private long _idle, _kernel, _user;
    private long _received, _sent;
    private DateTime _sampledAt;
    private readonly Queue<double> _cpuHistory = new();

    /// <summary>0..1, averaged over the time since the previous sample.</summary>
    public double Cpu { get; private set; }

    public IReadOnlyCollection<double> CpuHistory => _cpuHistory;

    public ulong MemoryUsed { get; private set; }

    public ulong MemoryTotal { get; private set; }

    /// <summary>Bytes per second over all connected network adapters.</summary>
    public double Download { get; private set; }

    public double Upload { get; private set; }

    public string Drive { get; private set; } = "";

    public long DriveFree { get; private set; }

    public long DriveTotal { get; private set; }

    /// <summary>Takes a new reading. The very first one only sets the baseline for the rates.</summary>
    public void Sample()
    {
        var now = DateTime.UtcNow;
        double seconds = (now - _sampledAt).TotalSeconds;
        bool first = _sampledAt == default || seconds > 10; // after a long pause, start over rather than average it
        _sampledAt = now;

        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long total = kernel - _kernel + (user - _user); // kernel time includes idle time
            long busy = total - (idle - _idle);
            if (!first && total > 0)
            {
                Cpu = Math.Clamp((double)busy / total, 0, 1);
                _cpuHistory.Enqueue(Cpu);
                while (_cpuHistory.Count > HistoryLength) _cpuHistory.Dequeue();
            }
            (_idle, _kernel, _user) = (idle, kernel, user);
        }

        var memory = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref memory))
        {
            MemoryTotal = memory.ullTotalPhys;
            MemoryUsed = memory.ullTotalPhys - memory.ullAvailPhys;
        }

        (long received, long sent) = NetworkTotals();
        if (!first && seconds > 0)
        {
            Download = Math.Max(0, received - _received) / seconds;
            Upload = Math.Max(0, sent - _sent) / seconds;
        }
        (_received, _sent) = (received, sent);

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            Drive = drive.Name.TrimEnd('\\');
            DriveFree = drive.AvailableFreeSpace;
            DriveTotal = drive.TotalSize;
        }
        catch (IOException)
        {
            DriveTotal = 0;
        }
    }

    /// <summary>The page is hidden: forget the baseline so the next opening starts fresh.</summary>
    public void Reset()
    {
        _sampledAt = default;
        _cpuHistory.Clear();
        Download = Upload = 0;
    }

    private static (long Received, long Sent) NetworkTotals()
    {
        long received = 0, sent = 0;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var statistics = adapter.GetIPStatistics();
                received += statistics.BytesReceived;
                sent += statistics.BytesSent;
            }
        }
        catch (NetworkInformationException)
        {
            // Adapters can disappear mid-scan; next second is fine.
        }
        return (received, sent);
    }
}
