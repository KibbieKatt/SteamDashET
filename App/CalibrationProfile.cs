using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json.Serialization;

namespace SteamDashET;

internal static class CalibrationGrid
{
    internal const int Rows = 12;
    internal const int Columns = 9;
    internal const int Count = Rows * Columns;
    internal const int CenterRow = 4;
    internal const int CenterIndex = CenterRow * Columns + Columns / 2;
    internal const float Degrees = MathF.PI / 180;

    // Index zero is the upper left target. OpenVR looks forward along negative Z.
    internal static Vector2 TargetAngles(int index)
    {
        if ((uint)index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        return new Vector2((index % Columns - Columns / 2) * 6 * Degrees,
            (CenterRow - index / Columns) * 4.5f * Degrees);
    }

    internal static bool TryAngles(Vector3 direction, out Vector2 angles)
    {
        angles = default;
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) ||
            !float.IsFinite(direction.Z) || direction.Z >= -0.000001f) return false;
        float length = direction.Length();
        if (!float.IsFinite(length) || length < 0.000001f) return false;
        direction /= length;
        angles = new Vector2(MathF.Atan2(direction.X, -direction.Z),
            MathF.Atan2(direction.Y, MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z)));
        return ValidAngles(angles);
    }

    internal static Vector3 FromAngles(Vector2 angles)
    {
        float horizontal = MathF.Cos(angles.Y);
        return new Vector3(MathF.Sin(angles.X) * horizontal, MathF.Sin(angles.Y),
            -MathF.Cos(angles.X) * horizontal);
    }

    // Reject corrupt or almost sideways/backward readings, outside the useful headset view.
    internal static bool ValidAngles(Vector2 angles) => float.IsFinite(angles.X) && float.IsFinite(angles.Y) &&
        MathF.Abs(angles.X) <= 85 * Degrees && MathF.Abs(angles.Y) <= 75 * Degrees;
}

internal readonly record struct CalibrationSample(double TimeSeconds, float Yaw, float Pitch,
    float TargetYaw, float TargetPitch);

internal readonly record struct CalibrationPoint([property: JsonRequired] float Yaw,
    [property: JsonRequired] float Pitch, [property: JsonRequired] float TargetYaw,
    [property: JsonRequired] float TargetPitch);

// The worker publishes immutable profiles to the UI and saves complete snapshots.
internal sealed class CalibrationProfile
{
    private readonly ReadOnlyCollection<CalibrationPoint?> targets;

    private CalibrationProfile(string headsetId, CalibrationPoint?[] targets)
    {
        HeadsetId = headsetId ?? "";
        this.targets = Array.AsReadOnly(targets);
        PopulatedTargetCount = targets.Count(target => target.HasValue);
    }

    internal string HeadsetId { get; }
    internal IReadOnlyList<CalibrationPoint?> Targets => targets;
    internal int PopulatedTargetCount { get; }

    internal static CalibrationProfile Empty(string headsetId) => new(headsetId, new CalibrationPoint?[CalibrationGrid.Count]);

    internal CalibrationProfile WithCapture(int index, IEnumerable<CalibrationSample> samples)
    {
        ValidateIndex(index);
        return CalibrationAveraging.TryAverage(samples, out var point) ? WithPoint(index, point) : this;
    }

    internal CalibrationProfile WithPoint(int index, CalibrationPoint point)
    {
        ValidateIndex(index);
        if (!Valid(point)) throw new ArgumentException("Calibration averages must have valid gaze and target angles.", nameof(point));
        var next = targets.ToArray();
        next[index] = point;
        return new CalibrationProfile(HeadsetId, next);
    }

    internal CalibrationProfile WithoutTarget(int index)
    {
        ValidateIndex(index);
        var next = targets.ToArray();
        next[index] = null;
        return new CalibrationProfile(HeadsetId, next);
    }

    internal static bool Valid(CalibrationSample sample) => double.IsFinite(sample.TimeSeconds) &&
        sample.TimeSeconds >= 0 && CalibrationGrid.ValidAngles(new Vector2(sample.Yaw, sample.Pitch)) &&
        CalibrationGrid.ValidAngles(new Vector2(sample.TargetYaw, sample.TargetPitch));

    internal static bool Valid(CalibrationPoint point) => CalibrationGrid.ValidAngles(new Vector2(point.Yaw, point.Pitch)) &&
        CalibrationGrid.ValidAngles(new Vector2(point.TargetYaw, point.TargetPitch));

    private static void ValidateIndex(int index)
    {
        if ((uint)index >= CalibrationGrid.Count) throw new ArgumentOutOfRangeException(nameof(index));
    }
}
