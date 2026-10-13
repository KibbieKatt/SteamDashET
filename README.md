# SteamDashET

A small Windows application that reads eye tracking from SteamVR and places a purple dot where your gaze intersects its dashboard panels, including desktop views and menus.

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
- **Gaze dot** reports whether SteamVR considers the indicator visible.
- A **Limited panel discovery** warning appears when the UI logs cannot be read. Hover over it for details. Known panel keys remain available, and the warning clears when log access recovers.
- **Settings** (gear icon) opens a separate window for the smoothing period. The slider ranges from 0.00 to 1.00 seconds, and the numeric arrows adjust it in 0.01-second steps. Changes apply immediately and are saved between launches.
- **Stop** removes the overlay and disconnects. **Start** reconnects.
- Closing the window stops the worker, removes the overlay, and releases its OpenVR connection.

Start SteamVR separately. While running, the application retries a missing or disconnected runtime. Invalid gaze or headset tracking hides the dot until tracking returns. Only one copy of the control application runs at a time.

Gaze-input connection failures, errors from the application's own overlay, and frame synchronization failures other than timeouts dispose the current session and trigger reconnection. Disappearing dashboard panels leave the live cache and are rediscovered or retried without reconnecting the session. Updates use SteamVR's frame synchronization; an interruptible delay is used only when it times out.

## Current behavior

The dot is a flat circle with a nebula purple fill (`#A878F0`), a dark gray border (`#292D32`), 30% opacity, an 8 mm outer radius, and a 2 mm border. It appears at the nearest valid hit on a visible dashboard panel, follows the panel surface, and sits 1 mm in front of it to avoid Z fighting. It appears only while the dashboard is open and your gaze hits a detected panel. A miss hides it immediately.

Desktop-game tabs (`valve.steam.desktopgame.<appid>`) are supported for any numeric app ID, including applications shown in their own dashboard tabs. Their cursor and pointer overlays remain excluded.

Smoothing defaults to 0.07 seconds; 0.00 disables it. Panel intersection uses a time-weighted average of recent eye tracking directions and the latest eye position. Longer periods reduce jitter and add delay. History resets when tracking is lost, the period changes, or the tracking reference changes. Settings are stored in `%LOCALAPPDATA%/SteamDashET/settings.json`.

The overlay provides a gaze indicator. Clicking is not implemented yet.

## Technical details

The combined gaze ray and panel intersections use SteamVR Standing coordinates. Discovery combines known dashboard keys, live overlay events, and bounded reads of SteamVR's UI logs for dynamic app/window/menu keys. Handles are resolved through `FindOverlay`; each candidate must be visible and have a finite positive opacity. Pointer overlays, transparent helpers, and the application's own indicator are excluded. Optional texture getters are not used to decide whether a handle is alive.

UI logs are located through the OpenVR path registry's current `log` entry, honoring `VR_LOG_PATH` and `VR_PATHREG_OVERRIDE`. Discovery refreshes every second, or every half second briefly after dashboard transitions. Each refresh rereads the logs and rebuilds a bounded candidate list, prioritizing known keys, recent overlay events, live cached panels, and the newest logged keys. Destroyed and unresolved overlays leave the live cache. The previous UI log is optional.

The logs supply overlay key names only. Gaze, panel visibility, and intersection geometry come from OpenVR APIs. Log hints allow discovery of dynamic panels that already existed when the application connected or are reused without creation events. Removing this source would reduce menu and floating-window coverage because the public API cannot enumerate all overlay keys.

Unresolved event handles and keys are retried for up to six discovery passes, using the half-second refresh interval while retries remain. Previously live panels get the same retry window after a failed lookup. Pending entries are bounded and removed on resolution, destruction, or expiry; they never become gaze targets until a native lookup succeeds.

`ComputeOverlayIntersection` supplies the hit point directly. Results must have finite coordinates and UVs, positive distance, and agree with the input ray. The nearest valid hit wins; nearly coincident hits prefer the higher overlay sort order. Every frame computes a fresh hit, so misses and dashboard closing leave no retained marker. Public APIs cannot enumerate all overlays or identify every internal control in the shared `system.systemui` atlas; coverage depends on the surfaces SteamVR exposes.

One static circle image is uploaded to a persistent Direct3D 11 texture on SteamVR's graphics adapter and submitted each frame to a flat OpenVR overlay. The intersection's surface normal is oriented toward the gaze origin, and the dot's center is offset 1 mm along that normal. The overlay's plane follows the same normal so it does not cut across a locally flat panel when viewed at an angle. It has no shading or simulated sphere surface. Opacity is applied once through `SetOverlayAlpha(0.30)`.

The indicator requests the maximum `SetOverlaySortOrder`. This is a rendering order request, and does not guarantee visibility above every dashboard element. [A reported SteamVR 2.17 regression](https://steamcommunity.com/app/250820/discussions/3/563659290304476304/) causes overlapping overlays to clip despite this ordering and also affects transparency. This matches the observed clipping on 2.17.10, but has not been isolated with a runtime version comparison here. OpenVR exposes no separate depth-test override to bypass it. The 1 mm surface offset avoids a coplanar depth tie; it does not override SteamVR's depth handling.

Overlay input is disabled (`None`), the gaze action set uses priority `0`, and the application sends no clicks or cursor events.

## Project layout

- `App/`: control window, OpenVR session, panel discovery, gaze input, dot rendering, and self-checks.
- `vendor/openvr/`: bundled bindings, native library, license, and provenance.

Self-checks cover gaze validation, panel filtering and hit geometry, bounded candidate refresh and lifecycle behavior, registered log paths and discovery recovery, flat dot colors and placement, generated bindings, smoothing, settings persistence, connection recovery, and cancellation using fake sessions.
