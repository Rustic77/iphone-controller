using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace RemotePhone_Agent.Capture;

/// <summary>
/// WinRT interop helpers to create <see cref="GraphicsCaptureItem"/> from an HWND.
/// </summary>
public static class GraphicsCaptureHelper
{
    // IGraphicsCaptureItem IID
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632FD5D62700");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(nint window, ref Guid iid, out nint result);

        void CreateForMonitor(nint monitor, ref Guid iid, out nint result);
    }

    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            throw new ArgumentException("HWND must be non-zero.", nameof(hwnd));
        }

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        interop.CreateForWindow(hwnd, ref iid, out var itemPtr);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }
}
