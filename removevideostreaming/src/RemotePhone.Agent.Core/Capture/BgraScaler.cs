namespace RemotePhone.Agent.Core.Capture;

/// <summary>
/// CPU downscale for BGRA8 frames before VP8 encode. Keeps even dimensions (I420/VP8).
/// </summary>
public static class BgraScaler
{
    public const int DefaultMaxLongEdge = 1280;

    /// <summary>
    /// Fit <paramref name="bgra"/> so the longer edge is at most <paramref name="maxLongEdge"/>.
    /// Returns the original buffer when no resize is required (still even-cropped if needed).
    /// </summary>
    public static byte[] Fit(byte[] bgra, int width, int height, int maxLongEdge, out int outWidth, out int outHeight)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width < 1 || height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame size must be positive.");
        }

        if (maxLongEdge < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLongEdge));
        }

        var expected = checked(width * height * 4);
        if (bgra.Length < expected)
        {
            throw new ArgumentException($"BGRA buffer is {bgra.Length} bytes, expected at least {expected}.", nameof(bgra));
        }

        var longEdge = Math.Max(width, height);
        int dstW;
        int dstH;
        if (longEdge <= maxLongEdge)
        {
            dstW = width & ~1;
            dstH = height & ~1;
            if (dstW == width && dstH == height)
            {
                outWidth = width;
                outHeight = height;
                return bgra;
            }

            outWidth = Math.Max(2, dstW);
            outHeight = Math.Max(2, dstH);
            return CropEven(bgra, width, height, outWidth, outHeight);
        }

        var scale = (double)maxLongEdge / longEdge;
        dstW = Math.Max(2, (int)Math.Round(width * scale) & ~1);
        dstH = Math.Max(2, (int)Math.Round(height * scale) & ~1);
        var dst = new byte[checked(dstW * dstH * 4)];
        for (var y = 0; y < dstH; y++)
        {
            var sy = Math.Min(height - 1, y * height / dstH);
            for (var x = 0; x < dstW; x++)
            {
                var sx = Math.Min(width - 1, x * width / dstW);
                var si = (sy * width + sx) * 4;
                var di = (y * dstW + x) * 4;
                dst[di] = bgra[si];
                dst[di + 1] = bgra[si + 1];
                dst[di + 2] = bgra[si + 2];
                dst[di + 3] = bgra[si + 3];
            }
        }

        outWidth = dstW;
        outHeight = dstH;
        return dst;
    }

    private static byte[] CropEven(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[checked(dstW * dstH * 4)];
        for (var y = 0; y < dstH; y++)
        {
            Buffer.BlockCopy(src, y * srcW * 4, dst, y * dstW * 4, dstW * 4);
        }

        return dst;
    }
}
