using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace RemotePhone_Agent.WebRtc;

/// <summary>
/// Custom video source that accepts BGRA frames from capture, converts to I420, and VP8-encodes.
/// Milestone-quality software path; production streaming should move encoding closer to GPU textures.
/// </summary>
internal sealed class CaptureVideoSource : IVideoSource, IDisposable
{
    private readonly VpxVideoEncoder _encoder = new();
    private readonly MediaFormatManager<VideoFormat> _formats;
    private readonly object _gate = new();
    private bool _started;
    private bool _paused;
    private bool _disposed;
    private int _width = 640;
    private int _height = 360;
    private long _frameCount;
    private DateTimeOffset _lastSampleUtc = DateTimeOffset.MinValue;

    public CaptureVideoSource()
    {
        _formats = new MediaFormatManager<VideoFormat>(_encoder.SupportedFormats);
    }

    public event RawVideoSampleDelegate? OnVideoSourceRawSample;
    public event EncodedSampleDelegate? OnVideoSourceEncodedSample;
    public event SourceErrorDelegate? OnVideoSourceError;

#pragma warning disable CS0067 // Event required by IVideoSource but unused on this path.
    public event RawVideoSampleFasterDelegate? OnVideoSourceRawSampleFaster;
#pragma warning restore CS0067

    public void RestrictFormats(Func<VideoFormat, bool> filter) => _formats.RestrictFormats(filter);

    public List<VideoFormat> GetVideoSourceFormats() => _formats.GetSourceFormats();

    public void SetVideoSourceFormat(VideoFormat videoFormat) => _formats.SetSelectedFormat(videoFormat);

    public void ForceKeyFrame() => _encoder.ForceKeyFrame();

    public bool HasEncodedVideoSubscribers() => OnVideoSourceEncodedSample is not null;

    public bool IsVideoSourcePaused()
    {
        lock (_gate)
        {
            return _paused || !_started;
        }
    }

    public Task StartVideo()
    {
        lock (_gate)
        {
            _started = true;
            _paused = false;
        }

        return Task.CompletedTask;
    }

    public Task CloseVideo()
    {
        lock (_gate)
        {
            _started = false;
            _paused = false;
        }

        return Task.CompletedTask;
    }

    public Task PauseVideo()
    {
        lock (_gate)
        {
            _paused = true;
        }

        return Task.CompletedTask;
    }

    public Task ResumeVideo()
    {
        lock (_gate)
        {
            _paused = false;
            _started = true;
        }

        return Task.CompletedTask;
    }

    public void ExternalVideoSourceRawSample(
        uint durationMilliseconds,
        int width,
        int height,
        byte[] sample,
        VideoPixelFormatsEnum pixelFormat)
    {
        // Not used; capture pushes via PushBgraFrame.
    }

    public void ExternalVideoSourceRawSampleFaster(uint durationMilliseconds, RawImage rawImage)
    {
        // Not used.
    }

    /// <summary>
    /// Accepts a BGRA frame from capture (or generates a placeholder when null).
    /// </summary>
    public void PushBgraFrame(int width, int height, byte[]? bgra, uint durationMs = 33)
    {
        if (_disposed)
        {
            return;
        }

        bool canSend;
        lock (_gate)
        {
            canSend = _started && !_paused;
            if (width > 0 && height > 0)
            {
                _width = width;
                _height = height;
            }
        }

        if (!canSend)
        {
            return;
        }

        try
        {
            width = _width;
            height = _height;
            bgra ??= CreatePlaceholderBgra(width, height, Interlocked.Increment(ref _frameCount));

            OnVideoSourceRawSample?.Invoke(durationMs, width, height, bgra, VideoPixelFormatsEnum.Bgra);

            var i420 = PixelConverter.ToI420(width, height, width * 4, bgra, VideoPixelFormatsEnum.Bgra);
            var encoded = _encoder.EncodeVideo(width, height, i420, VideoPixelFormatsEnum.I420, _formats.SelectedFormat.Codec);
            if (encoded is { Length: > 0 })
            {
                OnVideoSourceEncodedSample?.Invoke(durationMs, encoded);
            }

            _lastSampleUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            OnVideoSourceError?.Invoke(ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _encoder.Dispose();
    }

    private static byte[] CreatePlaceholderBgra(int width, int height, long frame)
    {
        var buffer = new byte[checked(width * height * 4)];
        var shade = (byte)(frame % 200);
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = shade;
            buffer[i + 1] = (byte)(255 - shade);
            buffer[i + 2] = 40;
            buffer[i + 3] = 255;
        }

        return buffer;
    }
}
