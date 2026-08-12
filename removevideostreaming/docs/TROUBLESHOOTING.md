# Troubleshooting

## AirServer / Reflector not detected

**Symptoms:** Receiver list empty or missing the mirror window.

**Checks:**

1. Confirm AirServer or Reflector is running and the phone is actively mirroring.
2. Window must be a **top-level visible** HWND (minimized / cloaked windows may be skipped).
3. Detector matches (case-insensitive):
   - Process or exe path containing `airserver` or `reflector`
   - Title containing `AirServer`, `Reflector`, or `AirPlay`
4. If the product rebranded the process name, add a hint via `Agent:ReceiverProcessHints` in config (Phase 1+) and/or extend detector hints in Core.
5. Run the Agent elevated only if your environment normally requires it for window enumeration (usually not required for standard desktop apps).
6. Refresh the list after starting the mirror — the useful HWND often appears only after AirPlay connects.

**False positives:** Other apps with “AirPlay” in the title can score via title-only (+40). Prefer process/exe matches (+100 / +80).

---

## Capture is black

**Symptoms:** Capture status looks OK / FPS ticking but preview is solid black.

**Checks:**

1. Confirm the selected HWND is the **content** window showing the phone UI, not a launcher / tray / empty shell.
2. Receiver window must not be fully covered in a way that produces empty surfaces; try bringing it to the foreground.
3. Disable exclusive fullscreen / exotic overlay modes on the receiver if available.
4. GPU driver crash/reset (see below) can leave a dead surface — stop capture, restart receiver, reselect, start again.
5. Ensure Graphics Capture is allowed for the packaged app (`graphicsCapture` capability — next section).
6. HDR / exotic color spaces: try forcing the receiver to SDR if the product offers it.
7. Verify you are not capturing a wrong multi-monitor / virtual desktop window.

---

## `graphicsCapture` capability

Packaged WinUI / MSIX apps need the Windows Graphics Capture restricted capability (or equivalent declaration for your packaging model) so `GraphicsCaptureItem` / `CreateForWindow` succeeds.

**If missing or denied:**

- Capture session creation fails or returns empty frames.
- Last error / logs mention access denied, capability, or capture item creation failure.

**Fix:**

1. Open `src/RemotePhone.Agent/Package.appxmanifest`.
2. Under `<Capabilities>`, declare the Graphics Capture capability required by your Windows App SDK / packaging docs (commonly the `graphicsCapture` restricted capability for desktop bridge / full-trust WinUI packages).
3. Redeploy / re-register the package (`dotnet run` with WinApp run support, or Visual Studio deploy).
4. Confirm the app identity is the one you just rebuilt (stale AUMID installs are a common footgun).

Unpackaged debugging may behave differently; prefer the supported packaged run path for capability-sensitive APIs.

---

## GPU reset / TDR

**Symptoms:** Sudden black preview, FPS collapse, D3D device lost, or Windows “Display driver stopped responding” toast.

**Checks:**

1. Stop capture in the Agent; do not leave a half-dead frame pool running.
2. Restart AirServer / Reflector and the Agent process if the device was lost.
3. Update GPU drivers; avoid stressing the GPU with simultaneous heavy games / encode during soak.
4. Watch `SoakMetrics` / diagnostics for rising `ErrorCount` and reconnect pressure (Phase 5).
5. After reset: reselect HWND → start capture → confirm non-black frames before starting WebRTC (Phase 2).

**Pass condition after recovery:** Preview shows live mirrored UI again without restarting Windows.
