# SteamDashET

A small Windows application that reads eye tracking from SteamVR, places a purple dot where your gaze intersects its dashboard panels, and lets you left click with controller A or right click with B.

## Build and run

Requires Windows x64, the .NET 10 SDK to build, SteamVR, and an eye-tracking provider that exposes OpenVR gaze input. OpenVR SDK 2.15.6 bindings and its native library are bundled under `vendor/openvr`.

From the project root:

```powershell
# Build and run the checks without connecting to SteamVR.
pwsh -File .\Build.ps1

# Build, run the checks, and open the control window.
pwsh -File .\Run.ps1

# Build, run the checks, and publish a self-contained executable.
pwsh -File .\BuildPublish.ps1
```

Both build workflows use optimized Release compilation:

- `Build.ps1` creates the ordinary build in `bin/Release/net10.0-windows/` and runs its self-checks. This copy requires the .NET 10 Windows Desktop Runtime; keep its accompanying files together.
- `Run.ps1` calls `Build.ps1`, then opens that ordinary build. You can also launch its `SteamDashET.exe` directly after building.
- `BuildPublish.ps1` calls `Build.ps1`, then publishes to `bin/Release/net10.0-windows/win-x64/publish/` and runs the published executable's self-checks. Distribute only this folder's `SteamDashET.exe`.

The published executable includes the .NET runtime, managed dependencies, and native libraries, including OpenVR. It needs no installed .NET runtime or separate application DLLs. Debugging symbols are embedded in the executable; no separate PDB is needed. Trimming and AOT compilation are disabled. Native libraries extract under `%TEMP%\.net` at runtime. SteamVR and an eye-tracking provider are still required.

Required third-party license notices are embedded in the executable and available through **Settings → Licenses**. These include OpenVR, Vortice, SharpGen, and the .NET runtime selected for the build, including its third-party notices. No separate license or provenance files are needed with the published executable; OpenVR source provenance remains in the repository.

The application creates its gaze action manifest and bindings automatically in `%LOCALAPPDATA%\SteamDashET\runtime-input`, alongside its settings and calibration data. Both builds use the same saved data. The application folder does not need write access.

## Control window

The application starts connecting automatically when the window opens.

- **Eye tracking** shows whether valid gaze data is arriving.
- **Dashboard** shows the SteamVR connection and whether the dashboard is open.
- **Gaze dot** reports whether SteamVR considers the indicator visible.
- **Calibration** shows the number of saved points. **Calibrate** opens the headset-fixed grid; **Clear** removes saved calibration when calibration is closed.
- A **Limited panel discovery** warning appears when the UI logs cannot be read. Hover over it for details. Known panel keys remain available, and the warning clears when log access recovers.
- **Settings** (gear icon) opens a separate window for the smoothing period and calibration interpolation method. The smoothing slider ranges from 0.00 to 1.00 seconds, and the numeric arrows adjust it in 0.01-second steps. Choose **Linear triangles** or **RBF (smooth fit)** for calibration. Changes apply immediately and are saved between launches.
- **Licenses** in settings opens a resizable window containing the embedded notices. The text can be scrolled, selected, and copied. Settings and licenses remain accessible when SteamVR is disconnected.
- **Stop** removes the overlays and disconnects. **Start** reconnects.
- Closing the window stops the worker, removes the overlays, and releases its OpenVR connection.

Start SteamVR separately. While running, the application retries a missing or disconnected runtime. Invalid gaze or headset tracking hides the dot until tracking returns. Only one copy of the control application runs at a time.

Gaze-input connection failures, errors from the application's own overlay, and frame synchronization failures other than timeouts dispose the current session and trigger reconnection. Disappearing dashboard panels leave the live cache and are rediscovered or retried without reconnecting the session. Updates use SteamVR's frame synchronization; an interruptible delay is used only when it times out.

## Current behavior

The dot is a flat circle with a nebula purple fill (`#A878F0`), a dark gray border (`#292D32`), 30% opacity, an 8 mm outer radius, and a 2 mm border. During normal use it appears at the nearest valid hit on a visible dashboard panel, follows the panel surface, and sits 1 mm in front of it to avoid Z fighting. It appears only while the dashboard is open and your gaze hits a detected panel. A miss hides it immediately. During calibration it follows gaze on the grid's plane, including outside the grid, without requiring a dashboard panel hit.

Desktop-game tabs (`valve.steam.desktopgame.<appid>`) are supported for any numeric app ID, including applications shown in their own dashboard tabs. Their cursor and pointer overlays remain excluded.

Smoothing defaults to 0.07 seconds; 0.00 disables it. Panel intersection uses a time-weighted average of recent eye tracking directions and the latest eye position. Longer periods reduce jitter and add delay. History resets when tracking is lost, the period changes, the tracking reference changes, saved calibration changes, or the interpolation method changes. Settings are stored in `%LOCALAPPDATA%/SteamDashET/settings.json`.

Press **A** while the dot is on a panel to submit a left click, or **B** to submit a right click. Both Index controllers are supported. Known Oculus Touch right-controller models are supported; their left X/Y buttons are excluded. Each button on each hand is tracked independently, and each press sends one complete click. Holding a button does not repeat or drag. Pressing while no dot is shown does nothing. Buttons held during startup, dashboard reopening, controller changes, or input recovery must be released before they can click.

Button polling uses the public `IVRRenderModels.GetComponentStateForDevicePath` API and its `IsPressed` flag. It has been verified with Index controllers while the dashboard is open, even when ordinary action input is unavailable. Touch mappings are based on installed model definitions and have not been tested live. Component names depend on controller models; unsupported models or failed reads disable the affected click input while gaze rendering continues. Polling observes button states and does not replace SteamVR's own button behavior or require experimental input overrides.

Clicking uses the public `IVROverlayView.PostOverlayEvent` route. Left and right clicks have been verified in the dashboard desktop view. Each panel decides how to handle those events; other panel types still need verification.

## Calibration

Choose **Calibrate** with eye tracking connected. All 108 targets appear in nine columns and twelve rows on a grid fixed to the headset, 1 meter away. Dots without a saved point are translucent gray; saved dots are green. An outline marks the selected dot and turns amber while collecting.

- Look at a target to select the dot nearest your gaze.
- Look at the selected dot and hold **A** to collect valid raw gaze samples. Release A to save that capture's average, replace the target's previous data, and immediately update the correction shown by the gaze dot. Selection stays locked during collection.
- Tap **B** to clear the selected target. Hold B for one second to finish without clearing it. **Finish calibration** in the control window also exits.
- Visit any targets in any order and repeat targets as needed. Saved points remain available when calibration is reopened.

Calibration collects gaze before correction and smoothing. The visible gaze dot uses the current saved correction followed by the same smoothing used during normal operation. Opening or closing calibration does not change smoothing behavior. Saving or clearing a point resets its history so the updated correction is visible immediately. Normal dashboard clicks are suspended during calibration; held buttons must be released before dashboard clicking resumes. SteamVR's own controller behavior is still active.

The selected interpolation method uses the same saved angular offsets:

- **Linear triangles** is the default. It connects measured gaze averages into triangles and blends their corrections within each triangle. It passes through the saved averages; changes in correction rate can occur at triangle edges. Outside the calibrated region, it extends the correction interpolated at the closest boundary edge. With two points or collinear points, it interpolates along the closest segment. With one saved point, either method applies a uniform offset; with no saved points, gaze remains uncorrected.
- **RBF (smooth fit)** uses Gaussian kernels with a 6° width and no regularization. It interpolates the saved averages within numerical precision, with smooth local variation between them. Corrections tend toward the average offset far from saved points, and their magnitude remains bounded by the observed calibration offsets.

Switching methods keeps saved calibration intact. At the end of each capture, samples are weighted by their valid dwell time, with unusually distant readings reduced in influence. Untracked intervals contribute no dwell time. Only one averaged gaze angle and its expected target angle are retained per target. New saves contain these averaged points; raw sample history is discarded after the capture. No minimum number of populated targets is required.

If two targets have exactly the same measured angles, both methods retain the target with the higher grid index for fitting. If distinct readings are too close for a reliable RBF calculation, loading or selecting RBF shows a fit warning and leaves gaze uncorrected while retaining the saved points. A failed new capture preserves the previous calibration. Select Linear triangles to repair saved points individually, then return to RBF, or clear calibration.

Data is saved to `%LOCALAPPDATA%/SteamDashET/calibration.json`, matched to the headset's serial number, and loaded automatically between launches. Files contain twelve rows and nine columns, retaining one averaged point or an empty entry for each target. Each successful A release saves its averaged point immediately. Direction is corrected relative to the headset and returned to SteamVR Standing coordinates before smoothing; the tracked eye position is preserved. **Clear** restores uncorrected gaze without changing smoothing settings. Stopping or disconnecting discards an unfinished A hold while retaining saved data.

If the calibration file cannot be read or contains invalid data, the control window shows a load warning and gaze remains uncorrected. The next successful capture or clear operation writes new calibration to the same file.

The calibration grid uploads a new RGBA image with `SetOverlayRaw` when selection or saved point status changes. The application waits for its image-loading event before reusing the pinned upload buffer and publishes the latest requested state after completion.

## Technical details

The combined gaze ray and panel intersections use SteamVR Standing coordinates. Discovery combines known dashboard keys, live overlay events, and bounded reads of SteamVR's UI logs for dynamic app/window/menu keys. Handles are resolved through `FindOverlay`; each candidate must be visible and have a finite positive opacity. Pointer overlays, transparent helpers, and the application's own indicator are excluded. Optional texture getters are not used to decide whether a handle is alive.

UI logs are located through the OpenVR path registry's current `log` entry, honoring `VR_LOG_PATH` and `VR_PATHREG_OVERRIDE`. Discovery refreshes every second, or every half second briefly after dashboard transitions. Each refresh rereads the logs and rebuilds a bounded candidate list, prioritizing known keys, recent overlay events, live cached panels, and the newest logged keys. Destroyed and unresolved overlays leave the live cache. The previous UI log is optional.

The logs supply overlay key names only. Gaze, panel visibility, and intersection geometry come from OpenVR APIs. Log hints allow discovery of dynamic panels that already existed when the application connected or are reused without creation events. Removing this source would reduce menu and floating-window coverage because the public API cannot enumerate all overlay keys.

Unresolved event handles and keys are retried for up to six discovery passes, using the half-second refresh interval while retries remain. Previously live panels get the same retry window after a failed lookup. Pending entries are bounded and removed on resolution, destruction, or expiry; they never become gaze targets until a native lookup succeeds.

`ComputeOverlayIntersection` supplies the hit point directly. Results must have finite coordinates and UVs, positive distance, and agree with the input ray. The nearest valid hit wins; nearly coincident hits prefer the higher overlay sort order. Every frame computes a fresh hit, so misses and dashboard closing leave no retained marker. Public APIs cannot enumerate all overlays or identify every internal control in the shared `system.systemui` atlas; coverage depends on the surfaces SteamVR exposes.

One static circle image is uploaded to a persistent Direct3D 11 texture on SteamVR's graphics adapter and submitted each frame to a flat OpenVR overlay. The intersection's surface normal is oriented toward the gaze origin, and the dot's center is offset 1 mm along that normal. The overlay's plane follows the same normal so it does not cut across a locally flat panel when viewed at an angle. It has no shading or simulated sphere surface. Opacity is applied once through `SetOverlayAlpha(0.30)`.

The indicator requests the maximum `SetOverlaySortOrder`. This is a rendering order request, and does not guarantee visibility above every dashboard element. [A reported SteamVR 2.17 regression](https://steamcommunity.com/app/250820/discussions/3/563659290304476304/) causes overlapping overlays to clip despite this ordering and also affects transparency. This matches the observed clipping on 2.17.10, but has not been isolated with a runtime version comparison here. OpenVR exposes no separate depth-test override to bypass it. The 1 mm surface offset avoids a coplanar depth tie; it does not override SteamVR's depth handling.

The indicator's own overlay input is disabled (`None`). The gaze action set uses priority `0`; controller button polling needs no action bindings. Clicks use successful hit UVs multiplied by the destination's `GetOverlayMouseScale`, then post mouse move, the selected mouse-button down, and its matching mouse-button up to that overlay. A mouse-up is attempted even if posting the down event throws. No button remains held between frames.

## Project layout

- `App/`: control window, OpenVR session, panel discovery, gaze input, calibration, dot rendering, and self-checks.
- `vendor/openvr/`: bundled bindings, native library, license, and provenance.

Self-checks cover gaze validation and bindings, controller-component polling and identity changes, left/right click coordinates and event order, overlapping presses, held and missed presses, input recovery, panel filtering and hit geometry, bounded candidate refresh and lifecycle behavior, registered log paths and discovery recovery, flat dot colors and placement, smoothing, settings persistence, calibration fitting and persistence, target selection and collection, connection recovery, and cancellation using fake sessions.
