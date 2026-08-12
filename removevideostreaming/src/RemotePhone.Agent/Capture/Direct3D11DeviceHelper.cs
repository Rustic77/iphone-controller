using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace RemotePhone_Agent.Capture;

/// <summary>
/// Creates a WinRT <see cref="IDirect3DDevice"/> backed by a D3D11 hardware device (BGRA).
/// </summary>
internal static class Direct3D11DeviceHelper
{
    private const uint D3D11SdkVersion = 7;
    private const uint D3DDriverTypeHardware = 1;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private static readonly Guid IidIdxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint pAdapter,
        uint driverType,
        nint software,
        uint flags,
        nint pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out nint ppDevice,
        out uint pFeatureLevel,
        out nint ppImmediateContext);

    [DllImport(
        "d3d11.dll",
        EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public static IDirect3DDevice CreateDevice()
    {
        var hr = D3D11CreateDevice(
            nint.Zero,
            D3DDriverTypeHardware,
            nint.Zero,
            D3D11CreateDeviceBgraSupport,
            nint.Zero,
            0,
            D3D11SdkVersion,
            out var d3dDevice,
            out _,
            out var context);

        if (hr < 0 || d3dDevice == nint.Zero)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            if (context != nint.Zero)
            {
                Marshal.Release(context);
            }

            var iid = IidIdxgiDevice;
            hr = Marshal.QueryInterface(d3dDevice, in iid, out var dxgiDevice);
            if (hr < 0 || dxgiDevice == nint.Zero)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            try
            {
                hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
                if (hr < 0 || inspectable == nint.Zero)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(d3dDevice);
        }
    }
}
