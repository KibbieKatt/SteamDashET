using System.Numerics;
using Valve.VR;

namespace SteamDashET;

internal sealed class GazeDot : IDisposable
{
    private readonly CVROverlay api;
    private readonly byte[] image = DotImage.CreatePixels();
    private ulong handle;
    private OverlayTexture texture;
    private bool shown;
    internal bool Visible => shown && handle != 0 && api.IsOverlayVisible(handle);

    internal GazeDot(CVROverlay api, CVRSystem system)
    {
        this.api = api;
        try
        {
            Check(api.CreateOverlay($"steamdash.et.{Environment.ProcessId}.gaze",
                "SteamDashET gaze dot", ref handle));
            Check(api.SetOverlayInputMethod(handle, VROverlayInputMethod.None));
            Check(api.SetOverlayFlag(handle, VROverlayFlags.VisibleInDashboard, true));
            Check(api.SetOverlayFlag(handle, VROverlayFlags.NoBackside, true));
            Check(api.SetOverlaySortOrder(handle, uint.MaxValue));
            Check(api.SetOverlayAlpha(handle, DotImage.Alpha));
            Check(api.SetOverlayWidthInMeters(handle, DotImage.PlaneWidth));
            texture = new OverlayTexture(system, DotImage.Width, DotImage.Height);
            Upload();
        }
        catch { Dispose(); throw; }
    }

    internal bool Update(Vector3 origin, Vector3 center, Vector3 normal)
    {
        if (Vector3.Dot(normal, origin - center) < 0) normal = -normal;
        if (!DotImage.TrySurfaceFrame(center, normal, out var frame)) return false;
        Check(api.SetOverlayTransformAbsolute(handle, ETrackingUniverseOrigin.TrackingUniverseStanding, ref frame));
        Upload();
        return true;
    }

    private void Upload()
    {
        texture.Update(image);
        var submitted = new Texture_t { handle = texture.Handle,
            eType = ETextureType.DirectX, eColorSpace = EColorSpace.Gamma };
        Check(api.SetOverlayTexture(handle, ref submitted));
    }

    internal void Show(bool visible)
    {
        if (shown == visible) return;
        Check(visible ? api.ShowOverlay(handle) : api.HideOverlay(handle));
        shown = visible;
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
                api.ClearOverlayTexture(handle);
                api.DestroyOverlay(handle);
                handle = 0;
            }
        }
        finally { texture?.Dispose(); texture = null; }
    }
}
