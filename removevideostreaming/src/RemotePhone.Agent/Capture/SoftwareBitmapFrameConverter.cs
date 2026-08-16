using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using RemotePhone.Agent.Core.Capture;

namespace RemotePhone_Agent.Capture;

/// <summary>
/// GPU surface → SoftwareBitmap helpers for the preview path, plus BGRA copies for VP8.
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

    /// <summary>
    /// Copy a SoftwareBitmap to tightly packed BGRA8, downscaled for software VP8.
    /// Does not dispose <paramref name="bitmap"/> (preview still owns it).
    /// </summary>
    public static bool TryCopyToBgra(SoftwareBitmap bitmap, out byte[] bgra, out int width, out int height)
    {
        bgra = [];
        width = 0;
        height = 0;
        ArgumentNullException.ThrowIfNull(bitmap);

        SoftwareBitmap? converted = null;
        try
        {
            var source = bitmap;
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                source = converted;
            }

            var pixelWidth = source.PixelWidth;
            var pixelHeight = source.PixelHeight;
            var length = checked((uint)(pixelWidth * pixelHeight * 4));
            var buffer = new Windows.Storage.Streams.Buffer(length);
            source.CopyToBuffer(buffer);
            var packed = new byte[buffer.Length];
            DataReader.FromBuffer(buffer).ReadBytes(packed);

            bgra = BgraScaler.Fit(packed, pixelWidth, pixelHeight, BgraScaler.DefaultMaxLongEdge, out width, out height);
            return width > 0 && height > 0 && bgra.Length >= width * height * 4;
        }
        catch
        {
            bgra = [];
            width = 0;
            height = 0;
            return false;
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
