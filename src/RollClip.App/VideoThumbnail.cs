using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RollClip.App;

internal static class VideoThumbnail
{
    public static Task<BitmapSource?> LoadAsync(string path)
    {
        var result = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(Load(path)); }
            catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException)
            {
                result.SetResult(null);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static BitmapSource? Load(string path)
    {
        var id = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, 0, in id, out var factory) < 0)
            return null;
        try
        {
            const int thumbnailOnly = 8;
            if (factory.GetImage(new ImageSize(192, 108), thumbnailOnly, out var bitmap) < 0 || bitmap == 0)
                return null;
            try
            {
                var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally { DeleteObject(bitmap); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ImageSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(ImageSize size, int flags, out nint bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
}
