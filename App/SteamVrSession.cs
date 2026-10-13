using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Numerics;
using Valve.VR;

namespace SteamDashET;

internal sealed class SteamVrSession : IVrSession
{
    private readonly AppSettings settings;
    private readonly CalibrationService calibration;
    private readonly GazeSmoothing smoothing = new();
    private CVRSystem system;
    private CVRInput input;
    private CVROverlay overlay;
    private GazeDot dot;
    private DashboardPanels panels;
    private GazeClick clicks;
    private ControllerButtonInput controllerButtons;
    private CalibrationRoutine calibrationRoutine;
    private CalibrationGridOverlay calibrationGrid;
    private long calibrationRevision = -1;
    private bool calibrationWasActive;
    private ulong action;
    private VRActiveActionSet_t[] sets;
    private readonly TrackedDevicePose_t[] poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private bool initialized;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    internal SteamVrSession(AppSettings settings, CalibrationService calibration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        this.calibration = calibration;
        try
        {
            GazeInput.CheckAbi();
            Process[] servers = Process.GetProcessesByName("vrserver");
            bool running = servers.Length > 0;
            foreach (var server in servers) server.Dispose();
            if (!running) throw new InvalidOperationException("Start SteamVR to connect.");
            EVRInitError error = EVRInitError.None;
            system = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Overlay);
            if (error != EVRInitError.None || system == null) throw new InvalidOperationException("SteamVR connection: " + error);
            initialized = true;
            input = OpenVR.Input ?? throw new InvalidOperationException("SteamVR gaze input is unavailable.");
            overlay = OpenVR.Overlay ?? throw new InvalidOperationException("SteamVR overlays are unavailable.");
            var serial = new StringBuilder(1024);
            var serialError = ETrackedPropertyError.TrackedProp_Success;
            system.GetStringTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_SerialNumber_String, serial, 1024, ref serialError);
            if (serialError != ETrackedPropertyError.TrackedProp_Success || serial.Length == 0)
                throw new InvalidOperationException("Waiting for headset identity to load its calibration.");
            calibration.BindHeadset(serial.ToString());
            var typeError = ETrackedPropertyError.TrackedProp_Success;
            var type = new StringBuilder(1024);
            system.GetStringTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_ControllerType_String, type, 1024, ref typeError);
            // Keep generated input files in user app data, where SteamVR can read them.
            string manifest = GazeInput.WriteManifest(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamDashET", "runtime-input"),
                typeError == ETrackedPropertyError.TrackedProp_Success ? type.ToString() : "");
            Check(input.SetActionManifestPath(manifest), "Gaze bindings");
            ulong actionSet = 0;
            Check(input.GetActionSetHandle(GazeInput.ActionSet, ref actionSet), "Gaze action set");
            Check(input.GetActionHandle(GazeInput.Action, ref action), "Gaze action");
            sets = new[] { new VRActiveActionSet_t { ulActionSet = actionSet, nPriority = 0 } };
            controllerButtons = new ControllerButtonInput(system, input, OpenVR.RenderModels);
            calibrationRoutine = new CalibrationRoutine(calibration);
            panels = new DashboardPanels(overlay);
            dot = new GazeDot(overlay, system);
            clicks = new GazeClick(overlay, OpenVR.OverlayView ??
                throw new InvalidOperationException("SteamVR overlay input is unavailable."));
        }
        catch { Dispose(); throw; }
    }

    public VrStatus Poll(CancellationToken token)
    {
        VREvent_t ev = default;
        for (int count = 0; count < 256 && !token.IsCancellationRequested &&
            system.PollNextEvent(ref ev, (uint)Marshal.SizeOf<VREvent_t>()); count++)
        {
            panels.ObserveEvent(ev);
            if ((EVREventType)ev.eventType == EVREventType.VREvent_TrackedDeviceRoleChanged)
            {
                controllerButtons.Invalidate();
            }
            else if ((EVREventType)ev.eventType is EVREventType.VREvent_TrackedDeviceActivated or
                EVREventType.VREvent_TrackedDeviceDeactivated or EVREventType.VREvent_TrackedDeviceUpdated)
            {
                controllerButtons.Invalidate(ev.trackedDeviceIndex);
            }
            if ((EVREventType)ev.eventType == EVREventType.VREvent_Quit)
            {
                system.AcknowledgeQuit_Exiting();
                throw new InvalidOperationException("SteamVR disconnected.");
            }
            if ((EVREventType)ev.eventType is EVREventType.VREvent_ChaperoneUniverseHasChanged or
                EVREventType.VREvent_ChaperoneDataHasChanged or EVREventType.VREvent_SeatedZeroPoseReset or
                EVREventType.VREvent_StandingZeroPoseReset)
                smoothing.Reset();
        }
        bool dashboard = overlay.IsDashboardVisible();
        panels.Refresh(clock.Elapsed.TotalSeconds, dashboard, token);
        system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, poses);
        bool headValid = poses[0].bDeviceIsConnected && poses[0].bPoseIsValid && GazeInput.Finite(poses[0].mDeviceToAbsoluteTracking);
        var sync = input.UpdateActionState(sets, (uint)Marshal.SizeOf<VRActiveActionSet_t>());
        CheckInputConnection(sync, "Updating gaze actions");
        var buttons = controllerButtons.Read();
        VREyeTrackingData_t gaze = default;
        var read = input.GetEyeTrackingDataRelativeToNow(action, ETrackingUniverseOrigin.TrackingUniverseStanding,
            0, ref gaze, (uint)Marshal.SizeOf<VREyeTrackingData_t>());
        CheckInputConnection(read, "Reading eye tracking");
        bool gazeValid = GazeInput.TryDirection(gaze, out var direction) && sync == EVRInputError.None && read == EVRInputError.None;
        var origin = new Vector3(gaze.vGazeOrigin.v0, gaze.vGazeOrigin.v1, gaze.vGazeOrigin.v2);
        var pose = poses[0].mDeviceToAbsoluteTracking;
        Vector3 headOrigin = default, rawHeadDirection = default;
        if (headValid && gazeValid)
        {
            headOrigin = CalibrationSpace.ToHeadOrigin(origin, pose);
            rawHeadDirection = CalibrationSpace.ToHeadDirection(direction, pose);
        }
        calibration.SetInterpolationMethod(settings.InterpolationMethod);
        var calibrationState = calibration.Snapshot;
        bool activeAtStart = calibrationState.Active;
        double now = clock.Elapsed.TotalSeconds;
        Vector3 rawDirection = direction;
        void UpdatePreview()
        {
            if (calibrationState.Revision != calibrationRevision)
            {
                smoothing.Reset();
                calibrationRevision = calibrationState.Revision;
            }
            // Always start with raw tracking: clearing the final target removes the old correction too.
            direction = rawDirection;
            if (headValid && gazeValid)
            {
                if (calibrationState.PopulatedTargets > 0)
                    direction = CalibrationSpace.ToTrackingDirection(calibration.Correct(rawHeadDirection), pose);
                direction = smoothing.Apply(direction, now, settings.SmoothingSeconds);
            }
            else smoothing.Reset();
        }
        UpdatePreview();
        Vector3? selectionPoint = activeAtStart && headValid && gazeValid &&
            CalibrationGridImage.TryProject(headOrigin, CalibrationSpace.ToHeadDirection(direction, pose), out var previewPoint)
                ? previewPoint : null;
        calibrationRoutine.Update(buttons, headValid && gazeValid, headOrigin, rawHeadDirection, selectionPoint, now);
        calibrationState = calibration.Snapshot;
        // Saved or cleared points change correction immediately; selection resumes on the next frame.
        if (calibrationState.Revision != calibrationRevision) UpdatePreview();
        PanelHit hit = default;
        bool panelHit = !calibrationState.Active && dashboard && headValid && gazeValid && panels.TryIntersect(origin, direction, out hit);
        bool show;
        if (calibrationState.Active)
        {
            calibrationGrid ??= new CalibrationGridOverlay(overlay);
            calibrationGrid.Update(calibrationState.Selected, calibrationState.Collecting, calibrationState.HasSavedPoints);
            calibrationGrid.Show(headValid);
            show = headValid && gazeValid && CalibrationGridImage.TryProject(headOrigin,
                CalibrationSpace.ToHeadDirection(direction, pose), out var point) && dot.UpdateHeadRelative(headOrigin, point);
        }
        else
        {
            calibrationGrid?.Show(false);
            show = panelHit && dot.Update(origin, hit.Point, hit.Normal);
        }
        dot.Show(show);
        // Reset while calibrating and on its final frame; held A/B must release before clicking resumes.
        if (activeAtStart || calibrationState.Active || calibrationWasActive) clicks.Reset();
        else clicks.Update(buttons, dashboard && !token.IsCancellationRequested, show, hit);
        calibrationWasActive = calibrationState.Active;

        string eye = gazeValid ? "Receiving gaze" : "No gaze data";
        string detail = !headValid ? "Waiting for headset tracking." :
            !gazeValid ? "Waiting for valid eye tracking." :
            !dashboard ? "Open the SteamVR dashboard to see the dot." :
            !panelHit ? "No dashboard panel at your gaze. The dot is hidden." :
            !show ? "Waiting for a usable dot view." : "Gaze dot placed on a dashboard panel.";
        if (sync != EVRInputError.None || read != EVRInputError.None)
        {
            eye = "Input unavailable";
            detail = $"SteamVR gaze input: sync {sync}, read {read}.";
        }
        if (calibrationState.Active)
        {
            detail = (calibrationState.Collecting ? $"Collecting {calibrationState.PendingSamples:N0} samples." : "Look at a calibration dot.") +
                "\nLook to select · Hold A: collect · Tap B: clear\nHold B for 1 second: finish.\n" +
                (!headValid || !gazeValid ? "Waiting for valid headset and eye tracking." : calibrationState.Hint);
        }
        if (calibrationState.Error.Length > 0) detail = calibrationState.Error;
        else if (calibrationState.Warning.Length > 0) detail = calibrationState.Warning;
        var status = new VrStatus(true, eye, dashboard ? "Connected · Open" : "Connected · Closed",
            detail, gazeValid, dashboard, dot.Visible, string.Join(Environment.NewLine, panels.DiscoveryWarnings));
        var wait = overlay.WaitFrameSync(20);
        if (NeedsFrameSyncFallback(wait)) token.WaitHandle.WaitOne(10);
        return status;
    }

    internal static void CheckInputConnection(EVRInputError error, string call)
    {
        if (error is EVRInputError.NoSteam or EVRInputError.IPCError or EVRInputError.InvalidHandle)
            throw new InvalidOperationException($"SteamVR connection: {call}: {error}.");
    }

    internal static bool NeedsFrameSyncFallback(EVROverlayError error)
    {
        // A timeout can occur while the runtime is healthy. Other failures invalidate the session.
        if (error == EVROverlayError.None) return false;
        if (error == EVROverlayError.TimedOut) return true;
        throw new InvalidOperationException("SteamVR frame synchronization: " + error);
    }

    private static void Check(EVRInputError error, string call)
    {
        if (error != EVRInputError.None) throw new InvalidOperationException(call + ": " + error);
    }

    public void Dispose()
    {
        calibration.Finish();
        try { calibrationGrid?.Dispose(); calibrationGrid = null; }
        finally
        {
            try { dot?.Dispose(); dot = null; }
            finally
            {
                if (initialized) { initialized = false; OpenVR.Shutdown(); }
            }
        }
    }
}
