using DynamicIsland.Interop;

namespace DynamicIsland.Services;

internal readonly record struct PowerState(bool HasBattery, bool PluggedIn, int Percent, bool Saver);

internal enum PowerChange
{
    None,
    PluggedIn,
    Unplugged,
    BatteryLow,
    SaverOn,
    SaverOff,
    PercentChanged,
}

/// <summary>
/// Reads the battery state on demand. <see cref="ShellHooks"/> calls <see cref="Update"/> whenever
/// Windows broadcasts a power change, so there is no polling.
/// </summary>
internal sealed class PowerMonitor
{
    private static readonly int[] LowThresholds = [20, 10];

    public PowerState State { get; private set; } = Read();

    public PowerChange Update()
    {
        var before = State;
        var now = State = Read();

        if (!now.HasBattery) return PowerChange.None;
        if (now.PluggedIn && !before.PluggedIn) return PowerChange.PluggedIn;
        if (!now.PluggedIn && before.PluggedIn) return PowerChange.Unplugged;
        if (now.Saver != before.Saver) return now.Saver ? PowerChange.SaverOn : PowerChange.SaverOff;
        if (!now.PluggedIn && LowThresholds.Any(t => before.Percent > t && now.Percent <= t)) return PowerChange.BatteryLow;
        return now.Percent != before.Percent ? PowerChange.PercentChanged : PowerChange.None;
    }

    private static PowerState Read()
    {
        if (!Native.GetSystemPowerStatus(out var status)) return default;

        // BatteryFlag: 128 = no system battery, 255 = unknown.
        bool hasBattery = status.BatteryFlag != 255 && (status.BatteryFlag & 128) == 0;
        int percent = status.BatteryLifePercent > 100 ? 100 : status.BatteryLifePercent;
        // SystemStatusFlag: 1 while battery saver is on.
        return new PowerState(hasBattery, status.ACLineStatus == 1, percent, status.SystemStatusFlag == 1);
    }
}
