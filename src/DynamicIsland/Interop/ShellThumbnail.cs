using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Interop;

/// <summary>Explorer's own thumbnail (photo preview, document page, or the file type icon) for any path.</summary>
internal static class ShellThumbnail
{
    private const int SIIGBF_BIGGERSIZEOK = 0x1;

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, nint bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

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
