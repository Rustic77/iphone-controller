using System.Diagnostics;
using System.Text;
using RemotePhone.Agent.Core.Models;

namespace RemotePhone_Agent.AirPlay;

/// <summary>
/// Enumerates top-level visible windows into <see cref="ReceiverWindowInfo"/> records.
/// </summary>
public sealed class WindowEnumerator
{
    public List<ReceiverWindowInfo> Enumerate()
    {
        var results = new List<ReceiverWindowInfo>();

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            _ = lParam;
            try
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                {
                    return true;
                }

                var titleLength = NativeMethods.GetWindowTextLength(hWnd);
                if (titleLength <= 0)
                {
                    return true;
                }

                var exStyle = (int)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GwlExstyle);
                if ((exStyle & NativeMethods.WsExToolwindow) != 0)
                {
                    return true;
                }

                var titleBuilder = new StringBuilder(titleLength + 1);
                _ = NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                var title = titleBuilder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                if (!NativeMethods.GetWindowRect(hWnd, out var rect))
                {
                    return true;
                }

                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    return true;
                }

                _ = NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
                var processName = string.Empty;
                var exePath = string.Empty;

                if (processId != 0)
                {
                    try
                    {
                        using var process = Process.GetProcessById((int)processId);
                        processName = process.ProcessName;
                        try
                        {
                            exePath = process.MainModule?.FileName ?? string.Empty;
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // Access denied for MainModule path is common for elevated/system processes.
                            exePath = string.Empty;
                        }
                        catch (InvalidOperationException)
                        {
                            exePath = string.Empty;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Process exited between enumeration and lookup.
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                results.Add(new ReceiverWindowInfo(
                    Hwnd: hWnd,
                    Title: title,
                    ProcessName: processName,
                    ProcessId: (int)processId,
                    ExePath: exePath,
                    Width: rect.Width,
                    Height: rect.Height,
                    IsLikelyAirPlayReceiver: false));
            }
            catch
            {
                // Skip windows that throw during inspection.
            }

            return true;
        }, nint.Zero);

        return results;
    }
}
