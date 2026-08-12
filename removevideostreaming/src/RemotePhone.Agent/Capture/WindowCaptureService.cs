using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemotePhone.Agent.Core.Capture;
using RemotePhone.Agent.Core.Models;
using RemotePhone_Agent.Services;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace RemotePhone_Agent.Capture;

public sealed class FrameArrivedEventArgs : EventArgs
{
    public FrameArrivedEventArgs(int width, int height, SoftwareBitmap? previewBitmap)
    {
        Width = width;
        Height = height;
        PreviewBitmap = previewBitmap;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// Optional CPU preview bitmap (ownership transfers to subscriber; dispose after use).
    /// Streaming path should use GPU textures in a later phase.
    /// </summary>
    public SoftwareBitmap? PreviewBitmap { get; }
}

/// <summary>
/// Windows.Graphics.Capture session around a single HWND.
/// Preview converts surfaces to SoftwareBitmap at a reduced rate; frame queue holds lightweight size markers
/// as a stand-in for GPU-side frames until the streaming encoder path consumes textures directly.
/// </summary>
public sealed class WindowCaptureService : IAsyncDisposable
{
    private readonly CaptureStateMachine _stateMachine = new();
    private readonly object _gate = new();
    private readonly Stopwatch _fpsWatch = new();
    private int _framesInWindow;
    private long _lastPreviewTick;
    private const long PreviewIntervalTicks = TimeSpan.TicksPerSecond / 30;

    private ILogger? _logger;
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private BoundedFrameQueue<object>? _frameQueue;
    private SizeInt32 _lastSize;
    private int _queueCapacity = 3;
    private bool _previewEnabled = true;

    public CaptureStats Stats { get; } = new();
    public CaptureState State => _stateMachine.Current;

    public event EventHandler<FrameArrivedEventArgs>? FrameArrived;
    public event EventHandler<CaptureState>? StateChanged;
    public event EventHandler<VideoMetadata>? MetadataChanged;
    public event EventHandler? SourceLost;

    public WindowCaptureService()
    {
        _stateMachine.StateChanged += (_, state) =>
        {
            Stats.Status = state;
            StateChanged?.Invoke(this, state);
        };
    }

    public async Task StartAsync(nint hwnd, ILogger logger, int frameQueueCapacity = 3, bool enablePreview = true)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (hwnd == nint.Zero)
        {
            throw new ArgumentException("HWND must be non-zero.", nameof(hwnd));
        }

        await StopAsync().ConfigureAwait(false);

        _logger = logger;
        _queueCapacity = Math.Max(1, frameQueueCapacity);
        _previewEnabled = enablePreview;
        _frameQueue = new BoundedFrameQueue<object>(_queueCapacity);

        if (!_stateMachine.TryTransition(CaptureState.Selecting) &&
            _stateMachine.Current != CaptureState.Selecting)
        {
            _ = _stateMachine.TryTransition(CaptureState.Idle);
            _ = _stateMachine.TryTransition(CaptureState.Selecting);
        }

        try
        {
            _device = Direct3D11DeviceHelper.CreateDevice();
            _item = GraphicsCaptureHelper.CreateItemForWindow(hwnd);
            _item.Closed += OnItemClosed;
            _lastSize = _item.Size;

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _lastSize);
            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            _session.IsBorderRequired = false;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                _session.IsCursorCaptureEnabled = false;
            }

            _session.StartCapture();

            ApplySize(_lastSize.Width, _lastSize.Height, raiseEvents: true);

            if (!_stateMachine.TryTransition(CaptureState.Capturing))
            {
                throw new InvalidOperationException($"Unable to enter Capturing from {_stateMachine.Current}.");
            }

            _fpsWatch.Restart();
            _framesInWindow = 0;
            AgentLogging.CaptureStarted(logger, hwnd, _lastSize.Width, _lastSize.Height);
        }
        catch (Exception ex)
        {
            AgentLogging.CaptureError(logger, ex, "StartAsync");
            Stats.LastError = ex.Message;
            _ = _stateMachine.TryTransition(CaptureState.Error);
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        var logger = _logger;
        await CleanupAsync().ConfigureAwait(false);

        if (_stateMachine.Current is CaptureState.Capturing or CaptureState.SourceLost)
        {
            _ = _stateMachine.TryTransition(CaptureState.Stopped);
        }
        else if (_stateMachine.Current is CaptureState.Selecting)
        {
            _ = _stateMachine.TryTransition(CaptureState.Idle);
        }
        else if (_stateMachine.Current is CaptureState.Error or CaptureState.Stopped)
        {
            _ = _stateMachine.TryTransition(CaptureState.Idle);
        }

        Stats.Status = _stateMachine.Current;
        if (logger is not null)
        {
            AgentLogging.CaptureStopped(logger, "StopAsync");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        var logger = _logger;
        if (logger is not null)
        {
            AgentLogging.SourceLost(logger, "GraphicsCaptureItem.Closed");
        }

        Stats.LastError = "Source window closed";
        if (_stateMachine.TryTransition(CaptureState.SourceLost))
        {
            SourceLost?.Invoke(this, EventArgs.Empty);
        }

        _ = CleanupAsync();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var contentSize = frame.ContentSize;
            if (contentSize.Width != _lastSize.Width || contentSize.Height != _lastSize.Height)
            {
                RecreateFramePool(contentSize);
            }

            var width = contentSize.Width;
            var height = contentSize.Height;
            ApplySize(width, height, raiseEvents: true);

            lock (_gate)
            {
                _framesInWindow++;
                if (_fpsWatch.ElapsedMilliseconds >= 1000)
                {
                    Stats.Fps = _framesInWindow * 1000.0 / _fpsWatch.ElapsedMilliseconds;
                    _framesInWindow = 0;
                    _fpsWatch.Restart();
                }
            }

            var queue = _frameQueue;
            if (queue is not null)
            {
                // Placeholder for GPU frame handle; streaming will replace with texture references.
                queue.Enqueue(new FrameSizeMarker(width, height));
                Stats.DroppedFrames = queue.DroppedCount;
            }

            SoftwareBitmap? preview = null;
            if (_previewEnabled)
            {
                var now = Stopwatch.GetTimestamp();
                if (now - Interlocked.Read(ref _lastPreviewTick) >= PreviewIntervalTicks)
                {
                    Interlocked.Exchange(ref _lastPreviewTick, now);
                    preview = SoftwareBitmapFrameConverter.CopyFrameToSoftwareBitmapAsync(frame)
                        .GetAwaiter()
                        .GetResult();
                }
            }

            FrameArrived?.Invoke(this, new FrameArrivedEventArgs(width, height, preview));
        }
        catch (Exception ex)
        {
            var logger = _logger;
            if (logger is not null)
            {
                AgentLogging.CaptureError(logger, ex, "OnFrameArrived");
            }

            Stats.LastError = ex.Message;
            _ = _stateMachine.TryTransition(CaptureState.Error);
        }
    }

    private void RecreateFramePool(SizeInt32 size)
    {
        if (_device is null || _framePool is null)
        {
            return;
        }

        _lastSize = size;
        _framePool.Recreate(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            size);
    }

    private void ApplySize(int width, int height, bool raiseEvents)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var orientation = OrientationHelper.FromSize(width, height);
        var resolutionChanged = Stats.Width != width || Stats.Height != height;
        var orientationChanged = Stats.Orientation != orientation;

        Stats.Width = width;
        Stats.Height = height;
        Stats.Orientation = orientation;

        if (!raiseEvents)
        {
            return;
        }

        var logger = _logger;
        if (resolutionChanged && logger is not null)
        {
            AgentLogging.ResolutionChanged(logger, width, height);
        }

        if (orientationChanged && logger is not null)
        {
            AgentLogging.OrientationChanged(logger, orientation);
        }

        if (resolutionChanged || orientationChanged)
        {
            MetadataChanged?.Invoke(
                this,
                new VideoMetadata(width, height, orientation, Stats.Fps));
        }
    }

    private Task CleanupAsync()
    {
        try
        {
            if (_session is not null)
            {
                _session.Dispose();
                _session = null;
            }
        }
        catch
        {
            _session = null;
        }

        try
        {
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
                _framePool.Dispose();
                _framePool = null;
            }
        }
        catch
        {
            _framePool = null;
        }

        try
        {
            if (_item is not null)
            {
                _item.Closed -= OnItemClosed;
                _item = null;
            }
        }
        catch
        {
            _item = null;
        }

        try
        {
            if (_device is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // ignore
        }

        _device = null;
        _frameQueue?.Clear();
        _frameQueue = null;
        return Task.CompletedTask;
    }

    private sealed class FrameSizeMarker
    {
        public FrameSizeMarker(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public int Width { get; }
        public int Height { get; }
    }
}
