using System.Numerics;

namespace SteamDashET;

internal static class SmoothingChecks
{
    internal static void Run()
    {
        var smoother = new GazeSmoothing();
        Vector3 forward = Vector3.UnitZ, right = Vector3.UnitX;
        Near(smoother.Apply(forward, 0, 0.4), forward, "Smoothing starts with the first valid direction immediately");
        Near(smoother.Apply(right, 0.1, 0.4), forward, "New gaze changes gain weight as time passes");
        Near(smoother.Apply(right, 0.4, 0.4), Vector3.Normalize(new Vector3(3, 0, 1)),
            "Gaze smoothing weights directions by duration");
        Near(smoother.Apply(right, 0.5, 0.4), right, "History expires while current gaze stays unchanged");

        smoother.Reset();
        smoother.Apply(forward, 0, 0.4);
        smoother.Apply(right, 0.2, 0.4);
        Near(smoother.Apply(right, 0.5, 0.4), Vector3.Normalize(new Vector3(3, 0, 1)),
            "Smoothing clips the oldest segment at the exact cutoff");

        var sparse = new GazeSmoothing();
        var dense = new GazeSmoothing();
        sparse.Apply(forward, 0, 1);
        dense.Apply(forward, 0, 1);
        sparse.Apply(right, 0.2, 1);
        dense.Apply(right, 0.2, 1);
        for (int i = 1; i < 60; i++) dense.Apply(right, 0.2 + i * 0.01, 1);
        Vector3 expected = sparse.Apply(right, 0.8, 1);
        Near(dense.Apply(right, 0.8, 1), expected, "Duplicate polls and different polling rates preserve averaging weights");
        Near(sparse.Apply(right, 0.8, 1), expected, "Repeated polls at the same time do not advance smoothing");

        smoother.Reset();
        smoother.Apply(forward, 0, 0.4);
        smoother.Apply(right, 0.1, 0.4);
        Near(smoother.Apply(forward, 0.41, 0), forward, "Zero smoothing returns the exact current direction");
        Near(smoother.Apply(right, 0.42, 1), right, "Re-enabling smoothing discards disabled history");
        Near(smoother.Apply(forward, 0.43, 0.5), forward, "Changing the smoothing window clears history");
        smoother.Reset();
        Near(smoother.Apply(right, 0.44, 0.5), right, "Explicit tracking resets clear smoothing history");

        smoother.Reset();
        smoother.Apply(forward, 0, 0.4);
        smoother.Apply(-forward, 0.2, 0.4);
        Near(smoother.Apply(-forward, 0.4, 0.4), -forward, "Opposing directions use the newest ray instead of a zero vector");
        Near(smoother.Apply(right, 0.1, 0.4), right, "A backward clock clears smoothing history");
        Near(smoother.Apply(forward, 2, 0.4), forward, "A callback gap over one second clears smoothing history");
        Near(smoother.Apply(right, 2.1, double.NaN), right, "An invalid smoothing setting disables averaging");

        smoother.Apply(forward, 3, 0.4);
        Near(smoother.Apply(Vector3.Zero, 3.1, 0.4), Vector3.Zero, "A zero gaze direction clears smoothing history");
        Near(smoother.Apply(right, 3.2, 0.4), right, "Valid tracking restarts with current gaze after an invalid direction");
        smoother.Apply(forward, 3.3, 0.4);
        Near(smoother.Apply(right, double.NaN, 0.4), right, "A non-finite clock clears smoothing history");
        Near(smoother.Apply(forward, 3.4, 0.4), forward, "Valid time restarts with current gaze");

        smoother.Reset();
        smoother.Apply(forward, 0, 1);
        smoother.Apply(right, 0.2, 1);
        smoother.Apply(-forward, 0.2, 1);
        Near(smoother.Apply(-forward, 0.4, 1), -forward,
            "Direction changes at the same timestamp replace the latest segment");

        if (GazeSmoothing.Seconds(1.5) != 1 || GazeSmoothing.Seconds(-1) != 0 ||
            GazeSmoothing.Seconds(double.PositiveInfinity) != 0 ||
            GazeSmoothing.Seconds(0.267) != 0.27 || GazeSmoothing.Seconds(0.264) != 0.26)
            throw new InvalidOperationException("Smoothing settings must clamp to 0–1 seconds in 0.01-second increments.");
        Console.WriteLine("PASS Smoothing settings clamp to 0–1 seconds in 0.01-second increments");
    }

    private static void Near(Vector3 actual, Vector3 expected, string message)
    {
        if (Vector3.Distance(actual, expected) > 0.0001f)
            throw new InvalidOperationException($"{message}: {actual} versus {expected}");
        Console.WriteLine("PASS " + message);
    }
}
