using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace RemotePhone_Agent.Capture;

/// <summary>
/// Marshals <see cref="SoftwareBitmap"/> frames onto the UI thread for an Image control.
/// Throttles updates to protect the UI thread (preview path only).
/// </summary>
public sealed class CapturePreviewBridge
{
    private readonly DispatcherQueue _dispatcher;
    private readonly TimeSpan _minInterval;
    private readonly object _gate = new();
    private SoftwareBitmapSource? _source;
    private DateTimeOffset _lastUpdateUtc = DateTimeOffset.MinValue;
    private bool _busy;

    public CapturePreviewBridge(DispatcherQueue dispatcher, double maxFps = 30)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (maxFps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFps));
        }

        _minInterval = TimeSpan.FromSeconds(1.0 / maxFps);
    }

    public SoftwareBitmapSource EnsureSource()
    {
        lock (_gate)
        {
            return _source ??= new SoftwareBitmapSource();
        }
    }

    public void TryUpdate(SoftwareBitmap? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_busy || now - _lastUpdateUtc < _minInterval)
            {
                bitmap.Dispose();
                return;
            }

            _busy = true;
            _lastUpdateUtc = now;
        }

        var queued = _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
        {
            try
            {
                var source = EnsureSource();
                using (bitmap)
                {
                    await source.SetBitmapAsync(bitmap);
                }
            }
            catch
            {
                bitmap.Dispose();
            }
            finally
            {
                lock (_gate)
                {
                    _busy = false;
                }
            }
        });

        if (!queued)
        {
            bitmap.Dispose();
            lock (_gate)
            {
                _busy = false;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _source = null;
            _busy = false;
            _lastUpdateUtc = DateTimeOffset.MinValue;
        }
    }
}
