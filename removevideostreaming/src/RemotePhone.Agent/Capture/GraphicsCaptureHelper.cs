using Microsoft.UI;
using Windows.Graphics.Capture;

namespace RemotePhone_Agent.Capture;

/// <summary>
/// Creates a <see cref="GraphicsCaptureItem"/> from an HWND (unpackaged WinUI).
/// </summary>
public static class GraphicsCaptureHelper
{
    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            throw new ArgumentException("HWND must be non-zero.", nameof(hwnd));
        }

        var appWindowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var windowId = new Windows.UI.WindowId { Value = appWindowId.Value };
        var item = GraphicsCaptureItem.TryCreateFromWindowId(windowId);
        if (item is null)
        {
            throw new InvalidOperationException(
                $"Windows Graphics Capture cannot attach to HWND 0x{hwnd:X}.");
        }

        return item;
    }
}
