using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Notices new screenshots, like the little preview a Mac shows: Win+Shift+S / PrtSc (Snipping
/// Tool puts the picture on the clipboard) and Win+PrtSc or auto-saved snips (a new file in
/// Pictures\Screenshots). Clipboard-only shots are saved as a PNG so they can be dragged out as a
/// file. Both are pure notifications from Windows; nothing is polled.
/// </summary>
internal sealed class ScreenshotWatcher : IDisposable
{
    /// <summary>Apps whose clipboard images are screenshots (process names).</summary>
    private static readonly HashSet<string> CaptureTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "SnippingTool", "ScreenClippingHost", "ScreenSketch", "ShareX", "Greenshot", "Lightshot", "Flameshot", "PicPick", "Snagit32", "SnagitEditor",
    };

    private static readonly TimeSpan FileGrace = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan Echo = TimeSpan.FromSeconds(4);

    private readonly Dispatcher _dispatcher;
    private readonly nint _hwnd;
    private readonly FileSystemWatcher? _folder;
    private readonly DispatcherTimer _pending = new() { Interval = FileGrace };
    private DateTime _lastCapture;
    private bool _listening;

    public ScreenshotWatcher(nint hwnd, Dispatcher dispatcher)
    {
        _hwnd = hwnd;
        _dispatcher = dispatcher;
        _pending.Tick += (_, _) =>
        {
            _pending.Stop();
            SaveClipboardImage();
        };

        if (Native.KnownFolder(Native.FOLDERID_Screenshots) is { } folder && Directory.Exists(folder))
        {
            ScreenshotsFolder = folder;
            _folder = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName };
            _folder.Filters.Add("*.png");
            _folder.Filters.Add("*.jpg");
            _folder.Created += (_, e) => _dispatcher.InvokeAsync(() => OnFileCreated(e.FullPath));
        }
        CleanUpOldCopies();
    }

    /// <summary>Raised on the UI thread with the screenshot's file.</summary>
    public event Action<string>? Captured;

    public string? ScreenshotsFolder { get; }

    /// <summary>Where clipboard-only screenshots are saved (cleared after a few days).</summary>
    public static string CopiesFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "Screenshots");

    public bool Enabled
    {
        get => _listening;
        set
        {
            if (_listening == value) return;
            _listening = value;
            if (value) Native.AddClipboardFormatListener(_hwnd);
            else Native.RemoveClipboardFormatListener(_hwnd);
            if (_folder is not null) _folder.EnableRaisingEvents = value;
        }
    }

    /// <summary>WM_CLIPBOARDUPDATE: something new on the clipboard. Only pictures from screenshot tools count.</summary>
    public void OnClipboardUpdate()
    {
        if (!_listening || DateTime.UtcNow - _lastCapture < Echo || !FromCaptureTool()) return; // Win+PrtSc: file first, then clipboard
        try
        {
            if (!Clipboard.ContainsImage()) return;
        }
        catch (Exception)
        {
            return; // clipboard busy
        }

        // Snipping Tool may also save the shot to Pictures\Screenshots a moment later: prefer that file.
        _pending.Stop();
        _pending.Start();
    }

    public void Dispose()
    {
        Enabled = false;
        _folder?.Dispose();
    }

    private void OnFileCreated(string path)
    {
        if (!_listening) return;
        bool expected = _pending.IsEnabled;
        _pending.Stop();
        if (!expected && DateTime.UtcNow - _lastCapture < Echo) return; // the same shot, already shown from the clipboard
        WaitUntilReadable(path, attempts: 20);
    }

    // The capture tool is still writing the file: try again shortly.
    private void WaitUntilReadable(string path, int attempts)
    {
        try
        {
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
            if (new FileInfo(path).Length > 0)
            {
                Raise(path);
                return;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        if (attempts > 0)
        {
            var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            retry.Tick += (_, _) =>
            {
                retry.Stop();
                WaitUntilReadable(path, attempts - 1);
            };
            retry.Start();
        }
    }

    private void SaveClipboardImage()
    {
        BitmapSource? image = null;
        for (int attempt = 0; attempt < 3 && image is null; attempt++)
        {
            try
            {
                image = ReadClipboardImage();
            }
            catch (Exception)
            {
                Thread.Sleep(60); // another app is holding the clipboard
            }
        }
        if (image is null) return;

        try
        {
            Directory.CreateDirectory(CopiesFolder);
            string path = Path.Combine(CopiesFolder, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path)) encoder.Save(file);
            Raise(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    /// <summary>The "PNG" clipboard format keeps the shot exactly; the bitmap fallback drops its (often empty) alpha.</summary>
    private static BitmapSource? ReadClipboardImage()
    {
        if (Clipboard.GetData("PNG") is MemoryStream png)
        {
            var frame = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return frame;
        }
        if (Clipboard.GetImage() is not { } bitmap) return null;
        var opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
        opaque.Freeze();
        return opaque;
    }

    private void Raise(string path)
    {
        _lastCapture = DateTime.UtcNow;
        Captured?.Invoke(path);
    }

    private static bool FromCaptureTool()
    {
        nint owner = Native.GetClipboardOwner();
        if (owner == 0) return true; // the classic PrtSc copy has no owner window
        Native.GetWindowThreadProcessId(owner, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return CaptureTools.Contains(process.ProcessName);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void CleanUpOldCopies()
    {
        try
        {
            if (!Directory.Exists(CopiesFolder)) return;
            foreach (var file in new DirectoryInfo(CopiesFolder).EnumerateFiles("*.png"))
            {
                if (DateTime.UtcNow - file.CreationTimeUtc > TimeSpan.FromDays(3)) file.Delete();
            }
        }
        catch (Exception)
        {
            // Next start, then.
        }
    }
}
