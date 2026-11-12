using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandheldExplorer.Services;

/// <summary>Loads the same icons and thumbnails Windows Explorer shows, off the UI thread.</summary>
public static class ShellThumbnail
{
    private static readonly SemaphoreSlim Gate = new(4);

    public static async Task<ImageSource?> GetAsync(string path, int size, bool iconOnly)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Load(path, size, iconOnly)).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private static ImageSource? Load(string path, int size, bool iconOnly)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null)
                return null;

            const int ResizeToFit = 0x0, IconOnly = 0x4;
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, iconOnly ? IconOnly : ResizeToFit, out bitmap);
            if (hr != 0 || bitmap == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // lets the UI thread use it
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);
}
