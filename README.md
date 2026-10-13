# SteamDashET

A small Windows application that reads eye tracking from SteamVR and draws a solid magenta gaze dot over the SteamVR dashboard, including its desktop views.

## Build and run

Requires Windows x64, the .NET 10 SDK to build, SteamVR, and an eye-tracking provider that exposes OpenVR gaze input. Running a built copy requires the .NET 10 Windows Desktop Runtime. OpenVR SDK 2.15.6 bindings and its native library are bundled under `vendor/openvr`.

From the project root:

```powershell
# Build and run the checks without connecting to SteamVR.
pwsh -File .\Build.ps1

# Build, run the checks, and open the control window.
pwsh -File .\Run.ps1
```

You can also open `bin/Release/net10.0-windows/SteamDashET.exe` directly after building. Keep its accompanying files together. The application creates its gaze action manifest and bindings in `runtime-input` beside the executable, so that directory must be writable.

## Control window

The application starts connecting automatically when the window opens.

- **Eye tracking** shows whether valid gaze data is arriving.
- **Dashboard** shows the SteamVR connection and whether the dashboard is open.
- **Gaze dot** reports whether SteamVR considers the overlay visible.
- **Settings** (gear icon) opens a separate window for the smoothing period. The slider ranges from 0.00 to 1.00 seconds, and the numeric arrows adjust it in 0.01-second steps. Changes apply immediately and are saved between launches.
- **Stop** removes the overlay and disconnects. **Start** reconnects.
- Closing the window stops the worker, removes the overlay, and releases its OpenVR connection.

Start SteamVR separately. While running, the application retries a missing or disconnected runtime. Invalid gaze or headset tracking hides the dot until tracking returns. Only one copy of the control application runs at a time.

Connection and invalid-handle errors dispose the current session and trigger reconnection. Updates use SteamVR's frame synchronization; an interruptible delay is used only when it times out.

## Current behavior

The dot has a dark outline and is visible while the SteamVR dashboard is open. It uses SteamVR eye tracking.

Smoothing defaults to 0.07 seconds; 0.00 disables it. The dot uses a time-weighted average of recent eye tracking directions, following the latest eye position. Longer periods reduce jitter and add delay. History resets when tracking is lost, the period changes, or the tracking reference changes. Settings are stored in `%LOCALAPPDATA%/SteamDashET/settings.json`.

The overlay provides a gaze indicator. Clicking is not implemented yet.

## Technical details

The flat dot sits one meter along the combined gaze ray in SteamVR Standing coordinates. Its depth is fixed; it does not yet intersect or align with individual dashboard panels.

Overlay input is disabled (`None`), the gaze action set uses priority `0`, and the application sends no clicks or cursor events.

## Project layout

- `App/`: control window, OpenVR session, gaze input, dot rendering, and self-checks.
- `vendor/openvr/`: bundled bindings, native library, license, and provenance.

Self-checks cover gaze validation, placement, generated bindings, the solid texture, smoothing, settings persistence, connection recovery, and cancellation using fake sessions.
