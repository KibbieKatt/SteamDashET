using System.Numerics;
using Valve.VR;

namespace SteamDashET;

internal static class PanelChecks
{
    internal static void Run()
    {
        EligibilityChecks();
        IntersectionChecks();
        SelectionChecks();
        DotChecks();
    }

    private static void EligibilityChecks()
    {
        string[] content =
        {
            "valve.steam.gamepadui.main", "valve.steam.gamepadui.bar", "system.desktop.1",
            "system.systemui", "system.window.1234", "valve.steam.gamepadui.barpopup.1234",
            "valve.steam.gamepadui.frame.menu.1234"
        };
        Require(content.All(key => DashboardPanels.IsPanelCandidate(key) &&
            DashboardPanels.IsEligiblePanel(key, true, 1, VROverlayInputMethod.None)),
            "Known dashboard content remains eligible when it has no mouse input method");

        string[] appOverlays =
        {
            "valve.steam.gamepadui.appoverlay.438100.55608",
            "valve.steam.gamepadui.appoverlay.438100.59344"
        };
        Require(appOverlays.All(key => DashboardPanels.IsPanelCandidate(key) &&
            DashboardPanels.IsEligiblePanel(key, true, 1, VROverlayInputMethod.None)),
            "Observed app overlays with app and instance suffixes remain eligible without mouse input");
        string[] malformedAppOverlays =
        {
            "valve.steam.gamepadui.appoverlay.", "valve.steam.gamepadui.appoverlay.438100",
            "valve.steam.gamepadui.appoverlay..55608", "valve.steam.gamepadui.appoverlay.438100.",
            "valve.steam.gamepadui.appoverlay.438100.55608.extra",
            "valve.steam.gamepadui.appoverlay.cursor.55608", "valve.steam.gamepadui.appoverlay.438100.pointer",
            "valve.steam.gamepadui.appoverlay.438100.55608.helper"
        };
        Require(malformedAppOverlays.All(key => !DashboardPanels.IsPanelCandidate(key) &&
            !DashboardPanels.IsEligiblePanel(key, true, 1, VROverlayInputMethod.None) &&
            !DashboardPanels.IsEligiblePanel(key, true, 1, VROverlayInputMethod.Mouse)),
            "App overlays reject missing, extra, and helper suffixes even when they expose mouse input");

        string[] desktopGames = { "valve.steam.desktopgame.2467050", "valve.steam.desktopgame.3245490" };
        Require(desktopGames.All(key => DashboardPanels.IsEligiblePanel(key, true, 1, VROverlayInputMethod.Mouse) &&
            DashboardPanels.IsEligiblePanel(key, true, 1, VROverlayInputMethod.None) &&
            !DashboardPanels.IsEligiblePanel(key, false, 1, VROverlayInputMethod.Mouse) &&
            !DashboardPanels.IsEligiblePanel(key, true, 0, VROverlayInputMethod.Mouse)),
            "Desktop-game tabs with different app IDs are eligible only while visible and nontransparent");
        string[] invalidDesktopGames =
        {
            "valve.steam.desktopgame.", "valve.steam.desktopgame.pointer", "valve.steam.desktopgame.utility",
            "valve.steam.desktopgame.+2467050", "valve.steam.desktopgame.-2467050",
            "valve.steam.desktopgame.\uFF12\uFF14\uFF16\uFF17\uFF10\uFF15\uFF10", "valve.steam.desktopgame.2467050.extra",
            "valve.steam.desktopgame.2467050.pointer", "valve.steam.desktopgame.2467050.cursor",
            "valve.steam.desktopgame.2467050.helper"
        };
        Require(invalidDesktopGames.All(key => !DashboardPanels.IsPanelCandidate(key) &&
            !DashboardPanels.IsEligiblePanel(key, true, 1, VROverlayInputMethod.Mouse)),
            "Desktop-game pointer overlays and malformed app IDs cannot become gaze targets");

        string[] helpers =
        {
            "ETCalCursor", "ETCalTarget", "steamdash.et.123.gaze", "SteamDash.ET.123.sphere",
            "system.pointer", "system.desktop.pointer", "system.cursor.ibeam", "system.HeadsetView",
            "valve.steam.gamepadui.tooltip.1234", "com.example.cursor", "com.example.input.helper"
        };
        Require(helpers.All(key => !DashboardPanels.IsPanelCandidate(key) &&
            !DashboardPanels.IsEligiblePanel(key, true, 1, VROverlayInputMethod.Mouse)),
            "Calibration helpers, cursor overlays, and our own overlay cannot become gaze targets");
        Require(!DashboardPanels.IsEligiblePanel("system.desktop.1", true, 0, VROverlayInputMethod.Mouse) &&
            !DashboardPanels.IsEligiblePanel("system.desktop.1", false, 1, VROverlayInputMethod.Mouse) &&
            !DashboardPanels.IsEligiblePanel("system.desktop.1", true, float.NaN, VROverlayInputMethod.Mouse) &&
            !DashboardPanels.IsEligiblePanel("system.desktop.1", true, float.PositiveInfinity, VROverlayInputMethod.Mouse),
            "Hidden, fully transparent, and invalid-alpha panels are excluded despite valid identities");
        Require(DashboardPanels.IsEligiblePanel("com.example.panel", true, 0.5f, VROverlayInputMethod.Mouse) &&
            !DashboardPanels.IsEligiblePanel("com.example.panel", true, 0.5f, VROverlayInputMethod.None),
            "Foreign namespaced panels need evidence of UI input before becoming gaze targets");
        Require(new[] { "", " ", "system.window.", "system.window.1.helper", "system.desktop.pointer.extra",
            "com.example.\nmalformed", "unknown", new string('x', 128) }
            .All(key => !DashboardPanels.IsPanelCandidate(key)),
            "Malformed keys and helper suffixes cannot bypass panel discovery filters");
    }

    private static void IntersectionChecks()
    {
        Vector3 origin = new(1, 2, 3);
        Vector3 direction = Vector3.Normalize(new Vector3(1, -0.25f, -2));
        Vector3 point = origin + direction * 2;
        var valid = new VROverlayIntersectionResults_t
        {
            vPoint = Native(point), vNormal = Native(-direction),
            vUVs = new HmdVector2_t { v0 = 0.25f, v1 = 0.75f }, fDistance = 2
        };
        Require(DashboardPanels.IsValidHit(origin, direction, valid),
            "A translated oblique world-space ray accepts its consistent surface intersection");

        var invalid = valid;
        invalid.fDistance = 0;
        Require(!DashboardPanels.IsValidHit(origin, direction, invalid), "Zero-distance intersections are rejected");
        invalid.fDistance = -2;
        Require(!DashboardPanels.IsValidHit(origin, direction, invalid), "Intersections behind the eye are rejected");
        invalid.fDistance = float.NaN;
        Require(!DashboardPanels.IsValidHit(origin, direction, invalid), "Non-finite intersection distances are rejected");
        invalid = valid;
        invalid.vUVs.v0 = float.NaN;
        Require(!DashboardPanels.IsValidHit(origin, direction, invalid), "Non-finite horizontal UVs are rejected");
        invalid = valid;
        invalid.vUVs.v1 = float.PositiveInfinity;
        Require(!DashboardPanels.IsValidHit(origin, direction, invalid), "Non-finite vertical UVs are rejected");
        invalid = valid;
        invalid.vPoint.v1 = float.NaN;
        Require(!DashboardPanels.IsValidHit(origin, direction, invalid), "Non-finite hit positions are rejected");
        invalid = valid;
        invalid.vNormal = default;
        Require(!DashboardPanels.IsValidHit(origin, direction, invalid), "Missing intersection normals are rejected");
        invalid.vNormal.v2 = float.PositiveInfinity;
        Require(!DashboardPanels.IsValidHit(origin, direction, invalid), "Non-finite intersection normals are rejected");
        invalid = valid;
        invalid.vPoint = Native(point + Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY)) * 0.1f);
        Require(!DashboardPanels.IsValidHit(origin, direction, invalid),
            "A hit displaced sideways from the gaze ray is rejected");
        invalid = valid;
        invalid.fDistance += 0.1f;
        Require(!DashboardPanels.IsValidHit(origin, direction, invalid),
            "A hit whose reported distance disagrees with its position is rejected");
        Require(!DashboardPanels.IsValidHit(origin, direction * 2, valid) &&
            !DashboardPanels.IsValidHit(new Vector3(float.NaN, 0, 0), direction, valid),
            "Invalid origins and non-unit rays cannot produce accepted hits");
    }

    private static void SelectionChecks()
    {
        var near = Hit("com.example.near", 10, 1);
        var far = Hit("com.example.far", 20, 2);
        Require(DashboardPanels.IsPreferredHit(near, 0, far, uint.MaxValue) &&
            !DashboardPanels.IsPreferredHit(far, uint.MaxValue, near, 0),
            "The nearest surface wins even when a distant overlay has a higher sort order");
        var almostSame = Hit("com.example.other", 30, 1.0005f);
        Require(DashboardPanels.IsPreferredHit(almostSame, 5, near, 2) &&
            !DashboardPanels.IsPreferredHit(near, 2, almostSame, 5),
            "Nearly coincident panels use their sort order to break the distance tie");
        var alpha = Hit("com.example.a", 100, 1);
        var beta = Hit("com.example.b", 1, 1);
        Require(DashboardPanels.IsPreferredHit(alpha, 1, beta, 1) &&
            !DashboardPanels.IsPreferredHit(beta, 1, alpha, 1),
            "Equal geometry and sorting select the same panel regardless of discovery order");
        var lowerHandle = alpha with { Handle = 99 };
        Require(DashboardPanels.IsPreferredHit(lowerHandle, 1, alpha, 1) &&
            !DashboardPanels.IsPreferredHit(alpha, 1, alpha, 1),
            "Identical panel identities have a stable handle tie-break without preferring themselves");
    }

    private static void DotChecks()
    {
        byte[] pixels = DotImage.CreatePixels();
        int fill = 0;
        int border = 0;
        bool validPixels = pixels.Length == DotImage.Width * DotImage.Height * 4;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] == 255)
            {
                bool isFill = pixels[index] == 168 && pixels[index + 1] == 120 && pixels[index + 2] == 240;
                bool isBorder = pixels[index] == 41 && pixels[index + 1] == 45 && pixels[index + 2] == 50;
                if (isFill) fill++;
                if (isBorder) border++;
                validPixels &= isFill || isBorder;
            }
            else validPixels &= pixels[index] == 0 && pixels[index + 1] == 0 &&
                pixels[index + 2] == 0 && pixels[index + 3] == 0;
        }
        int centerPixel = ((DotImage.Height / 2) * DotImage.Width + DotImage.Width / 2) * 4;
        Require(validPixels && fill > 0 && border > 0 && fill + border > 1100 && fill + border < 1300 &&
            pixels[3] == 0 && pixels[centerPixel] == 168 && pixels[centerPixel + 1] == 120 &&
            pixels[centerPixel + 2] == 240 && pixels[centerPixel + 3] == 255,
            "The dot has a purple center, a dark gray border, and a transparent background");
        Require(Math.Abs(DotImage.Alpha - 0.30f) < 0.000001f,
            "The dot uses 30 percent overlay opacity without multiplying texture alpha twice");

        Vector3 center = new(0.18f, 0.06f, -0.7f);
        Vector3 normal = Vector3.Normalize(new Vector3(0.35f, 0.6f, 1));
        Require(DotImage.TrySurfaceFrame(center, normal, out var frame) &&
            Math.Abs(DotImage.SurfaceOffset - 0.001f) < 0.0000001f && SurfaceBias(frame, center, normal),
            "The dot is offset one millimeter along the panel normal with no tangential shift");
        Require(SurfaceFrameValid(frame, normal),
            "The dot frame has finite orthonormal axes with the supplied surface normal and right-handed orientation");
        Require(SurfaceCorners(frame, center, normal),
            "Every corner of the dot quad lies in the offset tangent plane, including an oblique surface");
        Require(DotImage.TrySurfaceFrame(center, normal * 7, out var scaledFrame) &&
            SurfaceFrameValid(scaledFrame, normal) && SurfaceCorners(scaledFrame, center, normal) &&
            SurfaceBias(scaledFrame, center, normal),
            "A non-unit surface normal preserves the same one-millimeter offset and plane alignment");

        bool verticalFrames = DotImage.TrySurfaceFrame(center, Vector3.UnitY, out var vertical) &&
            DotImage.TrySurfaceFrame(center, new Vector3(0.000001f, 1, -0.000001f), out var nearVertical) &&
            SurfaceFrameValid(vertical, Vector3.UnitY) && SurfaceBias(vertical, center, Vector3.UnitY) &&
            SurfaceFrameValid(nearVertical, Vector3.Normalize(new Vector3(0.000001f, 1, -0.000001f))) &&
            Vector3.Dot(new(vertical.m0, vertical.m4, vertical.m8),
                new(nearVertical.m0, nearVertical.m4, nearVertical.m8)) > 0.99999f;
        Require(verticalFrames && DotImage.TrySurfaceFrame(center, -Vector3.UnitY, out var downward) &&
            SurfaceFrameValid(downward, -Vector3.UnitY) && SurfaceCorners(downward, center, -Vector3.UnitY),
            "Vertical and nearly vertical panel normals produce stable, non-degenerate tangent frames");
        Require(DotImage.TrySurfaceFrame(Vector3.Zero, Vector3.UnitZ, out var originFrame) &&
            SurfaceFrameValid(originFrame, Vector3.UnitZ) &&
            SurfaceBias(originFrame, Vector3.Zero, Vector3.UnitZ),
            "A valid hit at the tracking-space origin does not require a headset-relative distance");
        Require(DotImage.TrySurfaceFrame(center, new Vector3(1e30f, -2e30f, 3e30f), out var largeFrame) &&
            SurfaceFrameValid(largeFrame, Vector3.Normalize(new Vector3(1, -2, 3))) &&
            DotImage.TrySurfaceFrame(center, new Vector3(1e-30f, -2e-30f, 3e-30f), out var smallFrame) &&
            SurfaceFrameValid(smallFrame, Vector3.Normalize(new Vector3(1, -2, 3))),
            "Finite normals with very large or small magnitudes normalize without overflow or underflow");

        Require(!DotImage.TrySurfaceFrame(new Vector3(float.NaN, 0, 0), normal, out _) &&
            !DotImage.TrySurfaceFrame(new Vector3(0, float.PositiveInfinity, 0), normal, out _) &&
            !DotImage.TrySurfaceFrame(center, Vector3.Zero, out _) &&
            !DotImage.TrySurfaceFrame(center, new Vector3(float.NaN, 0, 1), out _) &&
            !DotImage.TrySurfaceFrame(center, new Vector3(0, float.NegativeInfinity, 1), out _),
            "Invalid hit positions and zero or non-finite normals cannot produce a dot frame");
    }

    private static bool SurfaceBias(HmdMatrix34_t frame, Vector3 center, Vector3 normal)
    {
        Vector3 displacement = new Vector3(frame.m3, frame.m7, frame.m11) - center;
        float normalDistance = Vector3.Dot(displacement, normal);
        return Math.Abs(normalDistance - DotImage.SurfaceOffset) < 0.000001f &&
            (displacement - normal * normalDistance).Length() < 0.000001f;
    }

    private static bool SurfaceFrameValid(HmdMatrix34_t frame, Vector3 expectedNormal)
    {
        Vector3 right = new(frame.m0, frame.m4, frame.m8);
        Vector3 up = new(frame.m1, frame.m5, frame.m9);
        Vector3 normal = new(frame.m2, frame.m6, frame.m10);
        return GazeInput.Finite(frame) &&
            Math.Abs(right.Length() - 1) < 0.00001f && Math.Abs(up.Length() - 1) < 0.00001f &&
            Math.Abs(normal.Length() - 1) < 0.00001f &&
            Math.Abs(Vector3.Dot(right, up)) < 0.00001f &&
            Math.Abs(Vector3.Dot(right, normal)) < 0.00001f &&
            Math.Abs(Vector3.Dot(up, normal)) < 0.00001f &&
            Vector3.Distance(Vector3.Cross(right, up), normal) < 0.00001f &&
            Vector3.Distance(normal, expectedNormal) < 0.00001f;
    }

    private static bool SurfaceCorners(HmdMatrix34_t frame, Vector3 center, Vector3 normal)
    {
        foreach (float x in new[] { -DotImage.PlaneWidth / 2, DotImage.PlaneWidth / 2 })
        foreach (float y in new[] { -DotImage.PlaneWidth / 2, DotImage.PlaneWidth / 2 })
        {
            Vector3 corner = new(
                frame.m0 * x + frame.m1 * y + frame.m3,
                frame.m4 * x + frame.m5 * y + frame.m7,
                frame.m8 * x + frame.m9 * y + frame.m11);
            if (Math.Abs(Vector3.Dot(corner - center, normal) - DotImage.SurfaceOffset) > 0.000001f) return false;
        }
        return true;
    }

    private static HmdVector3_t Native(Vector3 vector) => new() { v0 = vector.X, v1 = vector.Y, v2 = vector.Z };
    private static PanelHit Hit(string key, ulong handle, float distance) =>
        new(key, handle, -Vector3.UnitZ * distance, Vector3.UnitZ, distance);

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
