using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace RemotePhone_Agent.Capture;

/// <summary>
/// GPU surface → SoftwareBitmap helpers for the preview path.
/// Streaming (phase 2) should prefer GPU textures; this path is intentionally CPU for Image controls.
/// </summary>
internal static class SoftwareBitmapFrameConverter
{
    public static async Task<SoftwareBitmap?> CopyFrameToSoftwareBitmapAsync(Direct3D11CaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        try
        {
            IDirect3DSurface surface = frame.Surface;
            var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface);
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                bitmap.Dispose();
                return converted;
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
