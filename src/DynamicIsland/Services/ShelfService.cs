using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace DynamicIsland.Services;

internal sealed class ShelfItem(string path)
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;

    /// <summary>Shell thumbnail, loaded lazily by the UI.</summary>
    public ImageSource? Thumbnail { get; set; }
}

/// <summary>
/// The "tempat titip": files dropped on the island, kept until they are dragged out or removed.
/// Only the paths are stored (no copies), newest first, in %AppData%\DynamicIsland\shelf.json.
/// </summary>
internal sealed class ShelfService
{
    private const int MaxItems = 60;

    private static string FilePath => Path.Combine(AppSettings.DataDirectory, "shelf.json");

    public List<ShelfItem> Items { get; } = [];

    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [];
            Items.AddRange(paths.Where(Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxItems).Select(p => new ShelfItem(p)));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    /// <summary>Adds new paths at the front; returns how many were actually new.</summary>
    public int Add(IEnumerable<string> paths)
    {
        var fresh = paths
            .Where(Exists)
            .Where(p => !Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new ShelfItem(p))
            .ToList();
        if (fresh.Count == 0) return 0;

        Items.InsertRange(0, fresh);
        if (Items.Count > MaxItems) Items.RemoveRange(MaxItems, Items.Count - MaxItems);
        Commit();
        return fresh.Count;
    }

    public void Remove(ShelfItem item)
    {
        if (Items.Remove(item)) Commit();
    }

    public void RemoveRange(IEnumerable<ShelfItem> items)
    {
        var gone = items.ToHashSet();
        if (Items.RemoveAll(gone.Contains) > 0) Commit();
    }

    public void Clear()
    {
        if (Items.Count == 0) return;
        Items.Clear();
        Commit();
    }

    /// <summary>Drops entries whose file is gone, e.g. after it was dragged out with a move.</summary>
    public void PruneMissing()
    {
        if (Items.RemoveAll(i => !Exists(i.Path)) > 0) Commit();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private void Commit()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.Select(i => i.Path)));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        Changed?.Invoke();
    }
}
