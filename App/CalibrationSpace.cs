using System.Numerics;
using Valve.VR;

namespace SteamDashET;

internal static class CalibrationSpace
{
    internal static Vector3 ToHeadDirection(Vector3 direction, HmdMatrix34_t pose) => new(
        pose.m0 * direction.X + pose.m4 * direction.Y + pose.m8 * direction.Z,
        pose.m1 * direction.X + pose.m5 * direction.Y + pose.m9 * direction.Z,
        pose.m2 * direction.X + pose.m6 * direction.Y + pose.m10 * direction.Z);

    internal static Vector3 ToHeadOrigin(Vector3 origin, HmdMatrix34_t pose) =>
        ToHeadDirection(origin - new Vector3(pose.m3, pose.m7, pose.m11), pose);

    internal static Vector3 ToTrackingDirection(Vector3 direction, HmdMatrix34_t pose) => Vector3.Normalize(new(
        pose.m0 * direction.X + pose.m1 * direction.Y + pose.m2 * direction.Z,
        pose.m4 * direction.X + pose.m5 * direction.Y + pose.m6 * direction.Z,
        pose.m8 * direction.X + pose.m9 * direction.Y + pose.m10 * direction.Z));

    internal static bool TryIndicatorFrame(Vector3 origin, Vector3 point, out HmdMatrix34_t frame)
    {
        frame = default;
        Vector3 ray = point - origin;
        if (!GazeInput.Finite(origin) || !GazeInput.Finite(point) || ray.Z >= -0.000001f) return false;
        // Move toward the viewer along the gaze ray, maintaining angular alignment with the target.
        Vector3 front = point + ray * (DotImage.SurfaceOffset / ray.Z);
        return DotImage.TrySurfaceFrame(front - Vector3.UnitZ * DotImage.SurfaceOffset, Vector3.UnitZ, out frame);
    }
}
