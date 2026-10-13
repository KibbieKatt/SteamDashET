using System.Runtime.InteropServices;
using Valve.VR;

namespace SteamDashET;

internal sealed class GazeDot : IDisposable
{
    private readonly CVROverlay api;
    private ulong handle;
    private GCHandle pixels;
    private bool shown;
    internal bool Loaded { get; private set; }
    internal bool Visible => shown && api.IsOverlayVisible(handle);

    internal GazeDot(CVROverlay api)
    {
        this.api = api;
        try
        {
            Check(api.CreateOverlay($"steamdash.et.{Environment.ProcessId}.gaze", "SteamDashET gaze", ref handle));
            Check(api.SetOverlayInputMethod(handle, VROverlayInputMethod.None));
            Check(api.SetOverlayFlag(handle, VROverlayFlags.VisibleInDashboard, true));
            Check(api.SetOverlayFlag(handle, VROverlayFlags.NoBackside, true));
            Check(api.SetOverlaySortOrder(handle, uint.MaxValue));
            Check(api.SetOverlayWidthInMeters(handle, 0.020f));
            Check(api.SetOverlayAlpha(handle, 1));
            // Upload once and retain the buffer through the asynchronous upload and overlay lifetime.
            pixels = GCHandle.Alloc(MakePixels(), GCHandleType.Pinned);
            Check(api.SetOverlayRaw(handle, pixels.AddrOfPinnedObject(), 96, 96, 4));
        }
        catch { Dispose(); throw; }
    }

    internal void Update(HmdMatrix34_t transform) => Check(api.SetOverlayTransformAbsolute(handle,
        ETrackingUniverseOrigin.TrackingUniverseStanding, ref transform));

    internal void Show(bool visible)
    {
        if (shown == visible) return;
        Check(visible ? api.ShowOverlay(handle) : api.HideOverlay(handle));
        shown = visible;
    }

    internal void PollEvents()
    {
        VREvent_t ev = default;
        while (api.PollNextOverlayEvent(handle, ref ev, (uint)Marshal.SizeOf<VREvent_t>()))
        {
            if ((EVREventType)ev.eventType == EVREventType.VREvent_ImageLoaded) Loaded = true;
            if ((EVREventType)ev.eventType == EVREventType.VREvent_ImageFailed)
                throw new InvalidOperationException("SteamVR could not load the gaze dot.");
        }
    }

    internal static byte[] MakePixels()
    {
        var bytes = new byte[96 * 96 * 4];
        for (int y = 0; y < 96; y++)
        for (int x = 0; x < 96; x++)
        {
            float radius = MathF.Sqrt((x - 47.5f) * (x - 47.5f) + (y - 47.5f) * (y - 47.5f));
            if (radius > 43) continue;
            int i = (y * 96 + x) * 4;
            bool outline = radius > 38;
            bytes[i] = outline ? (byte)12 : (byte)255;
            bytes[i + 1] = outline ? (byte)12 : (byte)40;
            bytes[i + 2] = outline ? (byte)12 : (byte)220;
            bytes[i + 3] = 255;
        }
        return bytes;
    }

    private static void Check(EVROverlayError error)
    {
        if (error != EVROverlayError.None) throw new InvalidOperationException("Dashboard overlay: " + error);
    }

    public void Dispose()
    {
        try
        {
            if (handle != 0)
            {
                api.HideOverlay(handle);
                api.DestroyOverlay(handle);
                handle = 0;
            }
        }
        finally { if (pixels.IsAllocated) pixels.Free(); }
    }
}
