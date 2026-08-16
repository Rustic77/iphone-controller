using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RemotePhone_Agent.Capture;
using RemotePhone_Agent.UI;
using Windows.Graphics.Imaging;

namespace RemotePhone_Agent;

public sealed partial class MainPage : Page
{
    private CapturePreviewBridge? _previewBridge;

    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = App.CurrentApp.CreateMainViewModel(DispatcherQueue);
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _previewBridge = new CapturePreviewBridge(DispatcherQueue, maxFps: 30);
        PreviewImage.Source = _previewBridge.EnsureSource();
        ViewModel.PreviewFrameReady += OnPreviewFrameReady;
        ViewModel.RefreshReceiversCommand.Execute(null);

        if (Environment.GetCommandLineArgs().Any(a =>
                string.Equals(a, "--start-airplay", StringComparison.OrdinalIgnoreCase)))
        {
            _ = ViewModel.StartBuiltInAirPlayCommand.ExecuteAsync(null);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PreviewFrameReady -= OnPreviewFrameReady;
        _previewBridge?.Clear();
        _ = ViewModel.DisposeAsync();
    }

    private void OnPreviewFrameReady(object? sender, SoftwareBitmap? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        _previewBridge?.TryUpdate(bitmap);
    }
}
