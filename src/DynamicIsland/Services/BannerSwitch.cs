using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// Keeps a message from popping up twice (once in the island, once as Windows' own banner in the
/// corner). For each app whose notifications the island shows, it flips the same per-app switch as
/// Settings → System → Notifications → [app] → "Show notification banners". Messages still land in
/// Windows' notification centre, with their sound. While the island is closed the banners are back:
/// <see cref="RestoreAll"/> runs on exit and <see cref="ApplyAll"/> on start.
/// </summary>
internal sealed class BannerSwitch(AppSettings settings)
{
    private const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private readonly object _gate = new();

    /// <summary>True when the island itself switched this app's banner off (so its toasts are still the island's to show).</summary>
    public bool IsHiddenByIsland(string appId)
    {
        lock (_gate) return settings.BannerHiddenApps.Contains(appId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>After the island has shown a notification of <paramref name="appId"/>: no more Windows banners for it.</summary>
    public void Hide(string appId)
    {
        if (!settings.HideWindowsBanners || IsHiddenByIsland(appId)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{SettingsKey}\{appId}");
            if (key.GetValue("ShowBanner") is 0) return; // the user turned them off: that is theirs, not ours
            key.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return;
        }
        lock (_gate) settings.BannerHiddenApps.Add(appId);
        settings.Save();
    }

    /// <summary>The island is (back) on screen: hide the banners it took over before.</summary>
    public void ApplyAll()
    {
        if (!settings.HideWindowsBanners) return;
        foreach (string appId in Snapshot()) Write(appId, show: false);
    }

    /// <summary>Gives Windows its banners back; <paramref name="forget"/> also drops the list (option switched off).</summary>
    public void RestoreAll(bool forget)
    {
        foreach (string appId in Snapshot()) Write(appId, show: true);
        if (!forget) return;
        lock (_gate) settings.BannerHiddenApps.Clear();
        settings.Save();
    }

    private List<string> Snapshot()
    {
        lock (_gate) return [.. settings.BannerHiddenApps];
    }

    private static void Write(string appId, bool show)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{SettingsKey}\{appId}");
            if (show) key.DeleteValue("ShowBanner", throwOnMissingValue: false); // back to Windows' default: on
            else key.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }
}
