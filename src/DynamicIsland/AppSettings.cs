using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace DynamicIsland;

internal enum IslandStyle
{
    /// <summary>Floating pill a few pixels below the top edge (iPhone style).</summary>
    Island,

    /// <summary>Attached to the top edge with concave shoulders (MacBook style).</summary>
    Notch,
}

internal sealed class AppSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DynamicIsland";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public IslandStyle Style { get; set; } = IslandStyle.Island;
    public bool ShowVolume { get; set; } = true;
    public bool ShowTrackChanges { get; set; } = true;
    public bool ShowDeviceChanges { get; set; } = true;
    public bool ShowPrivacy { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    /// <summary>Per-user autostart through the HKCU Run key (no admin rights needed).</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}

internal static class Log
{
    public static void Error(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.AppendAllText(Path.Combine(AppSettings.DataDirectory, "error.log"), $"[{DateTime.Now:u}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing sensible left to do.
        }
    }
}
