using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using RemotePhone.Agent.Core.Calibration;
using RemotePhone.Agent.Core.Configuration;
using RemotePhone.Agent.Core.Models;
using RemotePhone.Agent.Core.WebRtc;
using RemotePhone_Agent.AirPlay;
using RemotePhone_Agent.Capture;
using RemotePhone_Agent.Services;
using RemotePhone_Agent.WebRtc;

namespace RemotePhone_Agent.UI;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AirPlayWindowService _airPlayService;
    private readonly WindowCaptureService _captureService;
    private readonly AirPlaySidecarHost _sidecar;
    private readonly DiagnosticsService _diagnostics;
    private readonly IWebRtcStreamingService _webRtc;
    private readonly AgentOptions _options;
    private readonly ILogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly PointerCalibrationModel _calibration = new();
    private readonly SoakTestService _soak;

    public MainViewModel(
        AirPlayWindowService airPlayService,
        WindowCaptureService captureService,
        AirPlaySidecarHost sidecar,
        DiagnosticsService diagnostics,
        IWebRtcStreamingService webRtc,
        AgentOptions options,
        ILogger logger,
        DispatcherQueue dispatcher)
    {
        _airPlayService = airPlayService ?? throw new ArgumentNullException(nameof(airPlayService));
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _sidecar = sidecar ?? throw new ArgumentNullException(nameof(sidecar));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _webRtc = webRtc ?? throw new ArgumentNullException(nameof(webRtc));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _soak = new SoakTestService(logger);

        _captureService.StateChanged += (_, state) => RunOnUi(() => CaptureStatus = state.ToString());
        _captureService.FrameArrived += OnCaptureFrameArrived;
        _captureService.MetadataChanged += OnMetadataChanged;
        _captureService.SourceLost += OnSourceLost;
        _webRtc.StateChanged += (_, state) => RunOnUi(() => WebRtcState = state.ToString());
        _webRtc.Faulted += (_, ex) => RunOnUi(() =>
        {
            LastError = ex.Message;
            RefreshDiagnostics();
        });

        _ = ConnectSignalingAsync();
    }

    public ObservableCollection<ReceiverWindowInfo> Receivers { get; } = new();

    public event EventHandler<Windows.Graphics.Imaging.SoftwareBitmap?>? PreviewFrameReady;

    [ObservableProperty]
    private ReceiverWindowInfo? selectedReceiver;

    [ObservableProperty]
    private string captureStatus = CaptureState.Idle.ToString();

    [ObservableProperty]
    private string sourceResolution = "-";

    [ObservableProperty]
    private string orientation = ScreenOrientation.Portrait.ToString();

    [ObservableProperty]
    private double captureFps;

    [ObservableProperty]
    private long droppedFrames;

    [ObservableProperty]
    private string diagnostics = string.Empty;

    [ObservableProperty]
    private string webRtcState = ConnectionState.Disconnected.ToString();

    [ObservableProperty]
    private double streamFps;

    [ObservableProperty]
    private double bitrate;

    [ObservableProperty]
    private double rtt;

    [ObservableProperty]
    private long webRtcDropped;

    [ObservableProperty]
    private string? lastError;

    [ObservableProperty]
    private string selectedDetails = "No receiver selected.";

    [ObservableProperty]
    private string calibrationStatus = CalibrationState.UNCALIBRATED.ToString();

    partial void OnSelectedReceiverChanged(ReceiverWindowInfo? value)
    {
        if (value is null)
        {
            SelectedDetails = "No receiver selected.";
            return;
        }

        SelectedDetails =
            $"Title: {value.Title}\n" +
            $"Process: {value.ProcessName} ({value.ProcessId})\n" +
            $"HWND: 0x{value.Hwnd:X}\n" +
            $"Size: {value.Width}x{value.Height}\n" +
            $"Exe: {value.ExePath}";
    }

    [RelayCommand]
    private void RefreshReceivers()
    {
        Receivers.Clear();
        foreach (var receiver in _airPlayService.Refresh())
        {
            Receivers.Add(receiver);
        }

        RefreshDiagnostics();
    }

    [RelayCommand]
    private async Task StartBuiltInAirPlayAsync()
    {
        LastError = "Installing built-in AirPlay receiver (first run downloads ~13 MB)…";
        RefreshDiagnostics();

        try
        {
            await _sidecar.StartAsync().ConfigureAwait(true);
            LastError = "Waiting for AirPlay-Windows window…";
            RefreshDiagnostics();

            ReceiverWindowInfo? found = null;
            for (var i = 0; i < 40; i++)
            {
                RefreshReceivers();
                found = Receivers.FirstOrDefault(r =>
                    r.ProcessName.Contains("airplay-windows", StringComparison.OrdinalIgnoreCase));
                if (found is not null)
                {
                    break;
                }

                await Task.Delay(250).ConfigureAwait(true);
            }

            if (found is null)
            {
                LastError =
                    "Sidecar started but no window yet. Allow the Windows firewall prompt, then Refresh.";
                RefreshDiagnostics();
                return;
            }

            SelectedReceiver = found;
            await SelectAirPlayWindowAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            FaultNote(ex);
        }

        RefreshDiagnostics();
    }

    [RelayCommand]
    private async Task StopBuiltInAirPlayAsync()
    {
        try
        {
            await _captureService.StopAsync().ConfigureAwait(true);
            await _sidecar.StopAsync().ConfigureAwait(true);
            CaptureStatus = _captureService.State.ToString();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        RefreshDiagnostics();
    }

    [RelayCommand]
    private async Task SelectAirPlayWindowAsync()
    {
        if (SelectedReceiver is null)
        {
            LastError = "Select a receiver from the list first.";
            return;
        }

        AgentLogging.ReceiverSelected(
            _logger,
            SelectedReceiver.Title,
            SelectedReceiver.ProcessName,
            SelectedReceiver.Hwnd);

        try
        {
            await _captureService.StartAsync(
                SelectedReceiver.Hwnd,
                _logger,
                _options.FrameQueueCapacity).ConfigureAwait(true);

            CaptureStatus = _captureService.State.ToString();
            SourceResolution = $"{_captureService.Stats.Width}x{_captureService.Stats.Height}";
            Orientation = _captureService.Stats.Orientation.ToString();
            LastError = null;
            _ = ConnectSignalingAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AgentLogging.CaptureError(_logger, ex, "SelectAirPlayWindow");
        }

        RefreshDiagnostics();
    }

    private async Task ConnectSignalingAsync()
    {
        try
        {
            await _webRtc.StartAsync().ConfigureAwait(true);
            RunOnUi(SyncWebRtcStats);
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                LastError = ex.Message;
                FaultNote(ex);
                RefreshDiagnostics();
            });
        }
    }

    [RelayCommand]
    private async Task StartStreamAsync()
    {
        try
        {
            await _webRtc.StartAsync().ConfigureAwait(true);
            SyncWebRtcStats();
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            FaultNote(ex);
        }

        RefreshDiagnostics();
    }

    [RelayCommand]
    private async Task StopStreamAsync()
    {
        try
        {
            await _webRtc.StopAsync().ConfigureAwait(true);
            SyncWebRtcStats();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        RefreshDiagnostics();
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        try
        {
            await _webRtc.ReconnectAsync().ConfigureAwait(true);
            SyncWebRtcStats();
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            FaultNote(ex);
        }

        RefreshDiagnostics();
    }

    [RelayCommand]
    private void CalibratePointer()
    {
        // Phase 4: full pointer calibration automation. For now, run homing plan bookkeeping.
        _calibration.BeginCalibrate();
        CalibrationStatus = _calibration.State.ToString();
        var steps = HomingPlan.BuildHomingSteps();
        _logger.LogInformation("CalibratePointer prepared {StepCount} homing steps (phase 4)", steps.Count);
        _calibration.MarkReady();
        CalibrationStatus = _calibration.State.ToString();
        RefreshDiagnostics();
    }

    [RelayCommand]
    private void ToggleSoakTest()
    {
        if (_soak.IsRunning)
        {
            _soak.Stop();
            _logger.LogInformation("Soak test stopped");
            return;
        }

        _soak.Start(
            () => new SoakSample(
                CaptureFps,
                StreamFps,
                DroppedFrames + WebRtcDropped,
                ReconnectCount: 0,
                SourceReconnectCount: 0,
                ErrorCount: string.IsNullOrEmpty(LastError) ? 0 : 1),
            TimeSpan.FromSeconds(30));
        _logger.LogInformation("Soak test started (30s samples)");
    }

    public async ValueTask DisposeAsync()
    {
        _soak.Stop();
        _captureService.FrameArrived -= OnCaptureFrameArrived;
        _captureService.MetadataChanged -= OnMetadataChanged;
        _captureService.SourceLost -= OnSourceLost;
        await _captureService.DisposeAsync().ConfigureAwait(false);
        await _sidecar.DisposeAsync().ConfigureAwait(false);
        _webRtc.Dispose();
    }

    private void OnCaptureFrameArrived(object? sender, FrameArrivedEventArgs e)
    {
        var preview = e.PreviewBitmap;
        if (preview is not null &&
            SoftwareBitmapFrameConverter.TryCopyToBgra(preview, out var bgra, out var w, out var h))
        {
            _webRtc.PushBgraFrame(w, h, bgra);
        }
        else
        {
            _webRtc.NotifyFrame(e.Width, e.Height);
        }

        RunOnUi(() =>
        {
            CaptureFps = _captureService.Stats.Fps;
            DroppedFrames = _captureService.Stats.DroppedFrames;
            SourceResolution = $"{e.Width}x{e.Height}";
            Orientation = _captureService.Stats.Orientation.ToString();
            SyncWebRtcStats();
            PreviewFrameReady?.Invoke(this, preview);
            RefreshDiagnostics();
        });
    }

    private void OnMetadataChanged(object? sender, VideoMetadata metadata)
    {
        RunOnUi(() =>
        {
            SourceResolution = $"{metadata.Width}x{metadata.Height}";
            Orientation = metadata.Orientation.ToString();
            if (_calibration.State == CalibrationState.READY)
            {
                _calibration.Invalidate();
                CalibrationStatus = _calibration.State.ToString();
            }

            RefreshDiagnostics();
        });
    }

    private void OnSourceLost(object? sender, EventArgs e)
    {
        if (_webRtc is SipSorceryStreamingService sip)
        {
            sip.NotifySourceLost("capture source lost");
        }

        RunOnUi(() =>
        {
            CaptureStatus = CaptureState.SourceLost.ToString();
            RefreshDiagnostics();
        });
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _ = _dispatcher.TryEnqueue(() => action());
    }

    private void SyncWebRtcStats()
    {
        WebRtcState = _webRtc.State.ToString();
        StreamFps = _webRtc.StreamFps;
        Bitrate = _webRtc.BitrateKbps;
        Rtt = _webRtc.RttMs;
        WebRtcDropped = _webRtc.DroppedFrames;
    }

    private void RefreshDiagnostics()
    {
        _diagnostics.UpdateCapture(
            SelectedReceiver?.Title,
            _captureService.Stats.Width,
            _captureService.Stats.Height,
            _captureService.Stats.Orientation,
            _captureService.Stats.Fps,
            _captureService.Stats.DroppedFrames,
            LastError ?? _captureService.Stats.LastError);
        Diagnostics = _diagnostics.BuildReport()
                      + $"WebRtcState: {WebRtcState}\n"
                      + $"StreamFps: {StreamFps:F1}\n"
                      + $"BitrateKbps: {Bitrate:F0}\n"
                      + $"RttMs: {Rtt:F0}\n"
                      + $"WebRtcDropped: {WebRtcDropped}\n"
                      + $"Calibration: {CalibrationStatus}\n";
    }

    private void FaultNote(Exception ex) => _logger.LogWarning(ex, "ViewModel operation failed");
}
