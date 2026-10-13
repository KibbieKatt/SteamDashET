using System.Numerics;

namespace SteamDashET;

// Reduce a completed hold to one observation. Raw readings exist only during capture;
// the profile and correction model retain the mean.
internal static class CalibrationAveraging
{
    internal const int MaximumSamples = 12000;
    private const double MaximumSampleGap = 0.2;

    internal static bool TryAverage(IEnumerable<CalibrationSample> source, out CalibrationPoint point)
    {
        ArgumentNullException.ThrowIfNull(source);
        var retained = new Queue<CalibrationSample>();
        foreach (var sample in source)
        {
            if (!CalibrationProfile.Valid(sample)) continue;
            retained.Enqueue(sample);
            if (retained.Count > MaximumSamples) retained.Dequeue();
        }
        point = default;
        if (retained.Count == 0) return false;
        var samples = retained.ToArray();
        var weights = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            double interval = i + 1 < samples.Length ? samples[i + 1].TimeSeconds - samples[i].TimeSeconds : 0;
            // Tracking gaps and sampling time resets contribute no dwell weight.
            weights[i] = interval > 0 && interval <= MaximumSampleGap ? interval : 0;
        }
        if (weights.Sum() == 0) Array.Fill(weights, 1);
        var median = new Vector2((float)WeightedMedian(samples.Select(sample => (double)sample.Yaw).ToArray(), weights),
            (float)WeightedMedian(samples.Select(sample => (double)sample.Pitch).ToArray(), weights));
        var distances = samples.Select(sample => (double)Vector2.Distance(new Vector2(sample.Yaw, sample.Pitch), median)).ToArray();
        double cutoff = Math.Max(CalibrationGrid.Degrees, 4 * WeightedMedian(distances, weights));
        var offsets = samples.Select(sample => new Vector2(sample.TargetYaw - sample.Yaw,
            sample.TargetPitch - sample.Pitch)).ToArray();
        var medianOffset = new Vector2((float)WeightedMedian(offsets.Select(value => (double)value.X).ToArray(), weights),
            (float)WeightedMedian(offsets.Select(value => (double)value.Y).ToArray(), weights));
        var offsetDistances = offsets.Select(value => (double)Vector2.Distance(value, medianOffset)).ToArray();
        double offsetCutoff = Math.Max(CalibrationGrid.Degrees, 4 * WeightedMedian(offsetDistances, weights));
        double yaw = 0, pitch = 0, targetYaw = 0, targetPitch = 0, weight = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if (distances[i] > cutoff || offsetDistances[i] > offsetCutoff) continue;
            yaw += samples[i].Yaw * weights[i];
            pitch += samples[i].Pitch * weights[i];
            targetYaw += samples[i].TargetYaw * weights[i];
            targetPitch += samples[i].TargetPitch * weights[i];
            weight += weights[i];
        }
        point = weight > 0
            ? new((float)(yaw / weight), (float)(pitch / weight), (float)(targetYaw / weight), (float)(targetPitch / weight))
            : new(median.X, median.Y,
                (float)WeightedMedian(samples.Select(sample => (double)sample.TargetYaw).ToArray(), weights),
                (float)WeightedMedian(samples.Select(sample => (double)sample.TargetPitch).ToArray(), weights));
        return true;
    }

    private static double WeightedMedian(double[] values, double[] weights)
    {
        int[] order = Enumerable.Range(0, values.Length).OrderBy(index => values[index]).ToArray();
        double threshold = weights.Sum() / 2, cumulative = 0;
        foreach (int index in order)
        {
            cumulative += weights[index];
            if (cumulative >= threshold && weights[index] > 0) return values[index];
        }
        return values[order[order.Length / 2]];
    }
}
