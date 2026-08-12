using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using RemotePhone.Agent.Core.Models;

namespace RemotePhone_Agent.Services;

/// <summary>
/// Collects agent diagnostics for the UI panel (never includes frame bytes).
/// </summary>
public sealed class DiagnosticsService
{
    private readonly object _gate = new();
    private string? _selectedReceiver;
    private int _width;
    private int _height;
    private ScreenOrientation _orientation = ScreenOrientation.Portrait;
    private double _fps;
    private long _dropped;
    private string? _lastError;
    private string? _gpuName;

    public string AppVersion { get; } = GetAppVersion();
    public string WindowsVersion { get; } = Environment.OSVersion.VersionString;
    public string DotNetVersion { get; } = RuntimeInformation.FrameworkDescription;

    public string GpuName
    {
        get
        {
            lock (_gate)
            {
                return _gpuName ??= ResolveGpuName();
            }
        }
    }

    public void UpdateCapture(
        string? selectedReceiver,
        int width,
        int height,
        ScreenOrientation orientation,
        double fps,
        long dropped,
        string? lastError)
    {
        lock (_gate)
        {
            _selectedReceiver = selectedReceiver;
            _width = width;
            _height = height;
            _orientation = orientation;
            _fps = fps;
            _dropped = dropped;
            _lastError = lastError;
        }
    }

    public string BuildReport()
    {
        lock (_gate)
        {
            var sb = new StringBuilder(512);
            sb.AppendLine($"AppVersion: {AppVersion}");
            sb.AppendLine($"WindowsVersion: {WindowsVersion}");
            sb.AppendLine($"DotNetVersion: {DotNetVersion}");
            sb.AppendLine($"GpuName: {GpuName}");
            sb.AppendLine($"SelectedReceiver: {_selectedReceiver ?? "(none)"}");
            sb.AppendLine($"Resolution: {_width}x{_height}");
            sb.AppendLine($"Orientation: {_orientation}");
            sb.AppendLine($"Fps: {_fps:F1}");
            sb.AppendLine($"Dropped: {_dropped}");
            sb.AppendLine($"LastError: {_lastError ?? "(none)"}");
            return sb.ToString();
        }
    }

    private static string GetAppVersion()
    {
        var asm = typeof(DiagnosticsService).Assembly;
        var info = FileVersionInfo.GetVersionInfo(asm.Location);
        return info.ProductVersion
               ?? info.FileVersion
               ?? asm.GetName().Version?.ToString()
               ?? "1.0.0";
    }

    private static string ResolveGpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    var name = obj["Name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name!;
                    }
                }
            }
        }
        catch
        {
            // WMI may be unavailable in some sandboxed contexts.
        }

        return "Unknown";
    }
}
