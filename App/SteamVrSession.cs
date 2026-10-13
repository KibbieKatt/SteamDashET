using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;

namespace SteamDashET;

internal sealed class SteamVrSession : IVrSession
{
    private readonly AppSettings settings;
    private readonly GazeSmoothing smoothing = new();
    private CVRSystem system;
    private CVRInput input;
    private CVROverlay overlay;
    private GazeDot dot;
    private DashboardPanels panels;
    private ulong action;
    private VRActiveActionSet_t[] sets;
    private readonly TrackedDevicePose_t[] poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private bool initialized;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    internal SteamVrSession(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
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
            var typeError = ETrackedPropertyError.TrackedProp_Success;
            var type = new StringBuilder(1024);
            system.GetStringTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_ControllerType_String, type, 1024, ref typeError);
            // SteamVR reads these files in its own process, so use the shared application directory.
            string manifest = GazeInput.WriteManifest(Path.Combine(AppContext.BaseDirectory, "runtime-input"),
                typeError == ETrackedPropertyError.TrackedProp_Success ? type.ToString() : "");
            Check(input.SetActionManifestPath(manifest), "Gaze bindings");
            ulong actionSet = 0;
            Check(input.GetActionSetHandle(GazeInput.ActionSet, ref actionSet), "Gaze action set");
            Check(input.GetActionHandle(GazeInput.Action, ref action), "Gaze action");
            sets = new[] { new VRActiveActionSet_t { ulActionSet = actionSet, nPriority = 0 } };
            panels = new DashboardPanels(overlay);
            dot = new GazeDot(overlay, system);
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
        VREyeTrackingData_t gaze = default;
        var read = input.GetEyeTrackingDataRelativeToNow(action, ETrackingUniverseOrigin.TrackingUniverseStanding,
            0, ref gaze, (uint)Marshal.SizeOf<VREyeTrackingData_t>());
        CheckInputConnection(read, "Reading eye tracking");
        bool gazeValid = GazeInput.TryDirection(gaze, out var direction) && sync == EVRInputError.None && read == EVRInputError.None;
        if (headValid && gazeValid)
            direction = smoothing.Apply(direction, clock.Elapsed.TotalSeconds, settings.SmoothingSeconds);
        else
            smoothing.Reset();
        var origin = new System.Numerics.Vector3(gaze.vGazeOrigin.v0, gaze.vGazeOrigin.v1, gaze.vGazeOrigin.v2);
        PanelHit hit = default;
        bool panelHit = dashboard && headValid && gazeValid && panels.TryIntersect(origin, direction, out hit);
        bool show = panelHit && dot.Update(origin, hit.Point, hit.Normal);
        dot.Show(show);

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
        try { dot?.Dispose(); dot = null; }
        finally
        {
            if (initialized) { initialized = false; OpenVR.Shutdown(); }
        }
    }
}
