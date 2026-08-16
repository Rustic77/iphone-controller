using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using RemotePhone.Agent.Core.AirPlay;
using RemotePhone.Agent.Core.Configuration;
using RemotePhone_Agent.AirPlay;
using RemotePhone_Agent.Capture;
using RemotePhone_Agent.Services;
using RemotePhone_Agent.Signaling;
using RemotePhone_Agent.UI;
using RemotePhone_Agent.WebRtc;

namespace RemotePhone_Agent;

/// <summary>
/// Application composition root: logging, configuration, and service wiring.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Configure();
    }

    public static App CurrentApp => (App)Current;

    public ILoggerFactory LoggerFactory { get; private set; } = default!;
    public AgentOptions Options { get; private set; } = default!;
    public DiagnosticsService Diagnostics { get; private set; } = default!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    public MainViewModel CreateMainViewModel(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        var logger = LoggerFactory.CreateLogger("RemotePhone.Agent");
        var airPlay = new AirPlayWindowService(LoggerFactory.CreateLogger("AirPlay"));
        var capture = new WindowCaptureService();
        var sidecarSpec = new AirPlaySidecarSpec
        {
            DownloadUrl = Options.AirPlaySidecarUrl,
            Sha256 = Options.AirPlaySidecarSha256,
            Arguments = Options.AirPlaySidecarArguments,
        };
        var sidecar = new AirPlaySidecarHost(sidecarSpec, LoggerFactory.CreateLogger("AirPlaySidecar"));
        var signaling = new WebSocketSignalingClient(Options, LoggerFactory.CreateLogger("Signaling"));
        var webRtc = new SipSorceryStreamingService(Options, signaling, LoggerFactory.CreateLogger("WebRtc"));
        return new MainViewModel(airPlay, capture, sidecar, Diagnostics, webRtc, Options, logger, dispatcher);
    }

    private void Configure()
    {
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Information)
                .AddConsole()
                .AddDebug();
        });

        var basePath = AppContext.BaseDirectory;
        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("Configuration/appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        Options = AgentOptions.FromConfiguration(configuration);
        Diagnostics = new DiagnosticsService();

        var logger = LoggerFactory.CreateLogger("App");
        AgentLogging.ApplicationStarted(logger, Diagnostics.AppVersion, Options.DeviceId, Options.AgentId);
    }
}
