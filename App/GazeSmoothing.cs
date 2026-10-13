using System.Numerics;

namespace SteamDashET;

// A time-weighted box average of gaze directions.
// Store direction changes so duplicate polling callbacks add no extra weight.
internal sealed class GazeSmoothing
{
    private readonly record struct Sample(double Time, Vector3 Direction);
    private readonly List<Sample> samples = new();
    private double window;
    private double lastTime;

    internal static double Seconds(double value) => double.IsFinite(value)
        ? Math.Round(Math.Clamp(value, 0, 1) * 100) / 100 : 0;

    internal void Reset() => samples.Clear();

    // GazeInput supplies a unit direction; smoothing changes direction only.
    internal Vector3 Apply(Vector3 currentDirection, double nowSeconds, double windowSeconds)
    {
        windowSeconds = Seconds(windowSeconds);
        if (windowSeconds == 0 || !double.IsFinite(nowSeconds) || !IsFinite(currentDirection) ||
            currentDirection.LengthSquared() < 0.000001f)
        {
            Reset();
            return currentDirection;
        }
        if (windowSeconds != window || (samples.Count > 0 &&
            (nowSeconds < lastTime || nowSeconds - lastTime > 1))) Reset();
        window = windowSeconds;
        lastTime = nowSeconds;
        if (samples.Count == 0) samples.Add(new Sample(nowSeconds, currentDirection));
        else if (!currentDirection.Equals(samples[^1].Direction))
        {
            if (samples[^1].Time == nowSeconds) samples[^1] = new Sample(nowSeconds, currentDirection);
            else samples.Add(new Sample(nowSeconds, currentDirection));
        }

        double cutoff = nowSeconds - windowSeconds;
        int remove = 0;
        while (remove + 1 < samples.Count && samples[remove + 1].Time <= cutoff) remove++;
        if (remove > 0) samples.RemoveRange(0, remove);
        // Keep the segment crossing the cutoff, but clip its contribution.
        double x = 0, y = 0, z = 0, duration = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            double end = i + 1 < samples.Count ? samples[i + 1].Time : nowSeconds;
            double weight = Math.Max(0, end - Math.Max(cutoff, samples[i].Time));
            x += samples[i].Direction.X * weight;
            y += samples[i].Direction.Y * weight;
            z += samples[i].Direction.Z * weight;
            duration += weight;
        }
        if (duration <= 0) return currentDirection;
        var average = new Vector3((float)(x / duration), (float)(y / duration), (float)(z / duration));
        // Opposing rays have no meaningful average. Use the newest direction.
        return average.LengthSquared() < 0.000001f ? currentDirection : Vector3.Normalize(average);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
