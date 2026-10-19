using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Interop;

/// <summary>
/// Explorer's own thumbnail (photo preview, document page, or the file type icon) for any path, and
/// the names and icons of installed apps ("shell:AppsFolder\{app id}").
/// </summary>
internal static class ShellThumbnail
{
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIGDN_NORMALDISPLAY = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE(int cx, int cy)
    {
        public int Cx = cx;
        public int Cy = cy;
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out nint bitmap);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bindContext, ref Guid handler, ref Guid riid, out nint result);
        [PreserveSig] int GetParent(out IShellItem? parent);
        [PreserveSig] int GetDisplayName(int nameType, [MarshalAs(UnmanagedType.LPWStr)] out string? name);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, nint bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHCreateItemFromParsingName")]
    private static extern int SHCreateShellItem(
        string path, nint bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    /// <summary>"WhatsApp", "Discord"... for an app id; null when the shell does not know it.</summary>
    public static string? GetAppName(string appId)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateShellItem($"shell:AppsFolder\\{appId}", 0, ref iid, out var item) != 0 || item is null) return null;
            try
            {
                return item.GetDisplayName(SIGDN_NORMALDISPLAY, out var name) == 0 && !string.IsNullOrWhiteSpace(name) ? name : null;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The app's icon, as in the Start menu.</summary>
    public static BitmapSource? GetAppIcon(string appId, int size) => Get($"shell:AppsFolder\\{appId}", size);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    public static BitmapSource? Get(string path, int size)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, 0, ref iid, out var factory) != 0 || factory is null) return null;
            try
            {
                if (factory.GetImage(new SIZE(size, size), SIIGBF_BIGGERSIZEOK, out var bitmap) != 0 || bitmap == 0) return null;
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    DeleteObject(bitmap);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception)
        {
            return null; // e.g. a file on a disconnected network share
        }
    }
}
