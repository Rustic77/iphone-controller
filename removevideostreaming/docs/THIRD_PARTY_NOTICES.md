# Third-party notices (lab AirPlay receiver)

This Windows agent can download and run a **built-in AirPlay mirror receiver**
so AirServer / Reflector are not required.

## AirPlay-Windows (sidecar)

| | |
|---|---|
| Project | [moieric11/AirPlay-Windows](https://github.com/moieric11/AirPlay-Windows) |
| Release | v0.1.0 (`airplay-windows-v0.1.0-x64.zip`) |
| License | **GPL-3.0** |
| SHA-256 | `e9350ca262ceb3967bda817d09a8d28b45327ec020fb6049cf6453097cfd8bab` |
| Install dir | `%LOCALAPPDATA%\RemotePhone\airplay-windows\` |

It is a native Windows port of [UxPlay](https://github.com/FDH2/UxPlay) (also GPL-3.0),
which uses reverse-engineered **PlayFair** to decrypt AirPlay *mirror* streams.

**Not included:** FairPlay Streaming DRM (Netflix / YouTube app / Apple TV+).
Those apps will not mirror.

The zip is **fetched on first use**, not committed to git. The C# agent launches
the exe as a separate process and captures its window.

If Apple or the upstream authors request removal of the pinned download, stop
using the built-in button and fall back to a licensed receiver window.

This tree is a **lab / non-commercial** tool. Do not ship the sidecar as a
closed-source product without a license review.
