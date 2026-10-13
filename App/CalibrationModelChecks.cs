using System.Numerics;
using System.Text.Json;

namespace SteamDashET;

internal static class CalibrationModelChecks
{
    internal static void Run()
    {
        for (int index = 0; index < CalibrationGrid.Count; index++)
        {
            var expected = CalibrationGrid.TargetAngles(index);
            Require(CalibrationGrid.TryAngles(CalibrationGrid.FromAngles(expected), out var actual) &&
                Vector2.Distance(actual, expected) < 0.000001f, "Calibration target angular coordinates round trip " + index);
        }
        Require(CalibrationGrid.TargetAngles(0).X < 0 && CalibrationGrid.TargetAngles(0).Y > 0 &&
            CalibrationGrid.TargetAngles(CalibrationGrid.Count - 1).X > 0 && CalibrationGrid.TargetAngles(CalibrationGrid.Count - 1).Y < 0,
            "Calibration grid uses upper-left to lower-right ordering");
        Require(CalibrationGrid.Rows == 12 && CalibrationGrid.Columns == 9 && CalibrationGrid.Count == 108 &&
            CalibrationGrid.CenterIndex == 40 && CalibrationGrid.TargetAngles(CalibrationGrid.CenterIndex) == Vector2.Zero &&
            Vector2.Distance(CalibrationGrid.TargetAngles(1) - CalibrationGrid.TargetAngles(0), Degrees(6, 0)) < 0.000001f &&
            Vector2.Distance(CalibrationGrid.TargetAngles(CalibrationGrid.Columns) - CalibrationGrid.TargetAngles(0), Degrees(0, -4.5f)) < 0.000001f,
            "The calibration grid has 108 targets with a forward center and six-degree by four-and-a-half-degree spacing");
        Require(Enumerable.Range(CalibrationGrid.Count - CalibrationGrid.Columns, CalibrationGrid.Columns)
                .All(index => Vector2.Distance(CalibrationGrid.TargetAngles(index),
                    Degrees((index % CalibrationGrid.Columns - CalibrationGrid.Columns / 2) * 6, -31.5f)) < 0.000001f),
            "The bottom calibration row spans the full width at minus thirty-one-and-a-half degrees pitch");
        Require(!CalibrationGrid.TryAngles(Vector3.Zero, out _) &&
            !CalibrationGrid.TryAngles(Vector3.UnitZ, out _) &&
            !CalibrationGrid.TryAngles(new Vector3(float.NaN, 0, -1), out _) &&
            !CalibrationGrid.TryAngles(Vector3.UnitX, out _),
            "Invalid, backward and sideways calibration directions are rejected");

        var empty = CalibrationProfile.Empty("headset");
        var uncorrected = new RbfCalibrationField(empty);
        Vector3 direction = CalibrationGrid.FromAngles(Degrees(7, -3));
        Require(uncorrected.Correct(direction) == direction && uncorrected.Correct(Vector3.Zero) == Vector3.Zero,
            "No calibration leaves gaze unchanged");

        var target = Degrees(1, -0.5f);
        var measured = Degrees(3, -1.5f);
        var first = empty.WithCapture(CalibrationGrid.CenterIndex, new[] { Sample(0, measured, target) });
        var constant = new RbfCalibrationField(first);
        NearAngles(constant.Correct(CalibrationGrid.FromAngles(measured)), target, 0.0001f,
            "A single calibration reading applies the expected constant angular offset");
        NearAngles(constant.Correct(CalibrationGrid.FromAngles(Degrees(20, 8))), Degrees(18, 9), 0.0001f,
            "One target supplies a useful correction away from that target");
        Require(MathF.Abs(constant.Correct(new Vector3(0, 0, -2)).Length() - 1) < 0.00001f,
            "Calibration produces a unit gaze direction");
        Require(constant.Correct(Vector3.UnitZ) == Vector3.UnitZ && constant.Correct(Vector3.Zero) == Vector3.Zero,
            "Invalid live rays remain invalid instead of producing fabricated gaze");
        Require(empty.PopulatedTargetCount == 0 && first.PopulatedTargetCount == 1 &&
            PointNear(first.Targets[CalibrationGrid.CenterIndex], Point(measured, target)),
            "Saving a capture stores one mean and leaves previous calibration snapshots unchanged");
        var replacement = first.WithCapture(CalibrationGrid.CenterIndex,
            new[] { Sample(0, Degrees(5, 1), target), Sample(0.1, Degrees(5, 1), target) });
        var cleared = replacement.WithoutTarget(CalibrationGrid.CenterIndex);
        Require(replacement.PopulatedTargetCount == 1 &&
            PointNear(replacement.Targets[CalibrationGrid.CenterIndex], Point(Degrees(5, 1), target)) &&
            PointNear(first.Targets[CalibrationGrid.CenterIndex], Point(measured, target)) &&
            cleared.PopulatedTargetCount == 0 && cleared.Targets.All(point => !point.HasValue),
            "Repeated captures replace a target mean and clearing affects only the new snapshot");
        var twoPoints = replacement.WithPoint(0, Point(Degrees(-22, 17), Degrees(-24, 18)));
        Require(twoPoints.WithoutTarget(CalibrationGrid.CenterIndex).PopulatedTargetCount == 1 &&
            twoPoints.WithoutTarget(CalibrationGrid.CenterIndex).Targets[0] == twoPoints.Targets[0],
            "Clearing one target preserves other saved calibration points");

        var invalid = first.WithCapture(4, new[]
        {
            new CalibrationSample(double.NaN, 0, 0, 0, 0), new CalibrationSample(-1, 0, 0, 0, 0),
            new CalibrationSample(1, float.NaN, 0, 0, 0), new CalibrationSample(1, 0, 0, float.NaN, 0),
            new CalibrationSample(1, 2, 0, 0, 0), new CalibrationSample(1, 0, 0, 0, 2)
        });
        Require(invalid.PopulatedTargetCount == 1 && invalid.Targets[CalibrationGrid.CenterIndex] ==
            first.Targets[CalibrationGrid.CenterIndex] && !invalid.Targets[4].HasValue,
            "Non-finite and out-of-view measured or expected calibration readings are rejected");
        Require(first.WithCapture(CalibrationGrid.CenterIndex, Array.Empty<CalibrationSample>())
            .Targets[CalibrationGrid.CenterIndex] == first.Targets[CalibrationGrid.CenterIndex] &&
            first.WithCapture(CalibrationGrid.CenterIndex,
                new[] { new CalibrationSample(double.NaN, 0, 0, 0, 0) })
                .Targets[CalibrationGrid.CenterIndex] == first.Targets[CalibrationGrid.CenterIndex],
            "An empty or invalid-only capture preserves the prior calibration point");
        var bounded = empty.WithCapture(0, Enumerable.Range(0, CalibrationAveraging.MaximumSamples * 2)
            .Select(i => Sample(i * 0.01, Degrees(i < CalibrationAveraging.MaximumSamples ? -4 : 4, 0), Vector2.Zero)));
        Require(bounded.PopulatedTargetCount == 1 && PointNear(bounded.Targets[0], Point(Degrees(4, 0), Vector2.Zero)),
            "A long capture averages only the newest bounded transient readings and stores one point");

        var sparse = CalibrationProfile.Empty("headset");
        int[] sparseIndices = [CalibrationGrid.CenterIndex - CalibrationGrid.Columns / 2,
            CalibrationGrid.CenterIndex, CalibrationGrid.CenterIndex + CalibrationGrid.Columns / 2];
        foreach (int index in sparseIndices)
        {
            Vector2 expected = CalibrationGrid.TargetAngles(index);
            Vector2 offset = Degrees(index == CalibrationGrid.CenterIndex ? 0.5f : 1.4f, index == sparseIndices[0] ? -0.8f : 0.7f);
            sparse = sparse.WithPoint(index, Point(expected - offset, expected));
        }
        var partial = new RbfCalibrationField(sparse);
        foreach (int index in sparseIndices)
        {
            var sample = sparse.Targets[index].Value;
            NearAngles(partial.Correct(CalibrationGrid.FromAngles(new Vector2(sample.Yaw, sample.Pitch))),
                new Vector2(sample.TargetYaw, sample.TargetPitch), 0.0001f,
                "Partial collinear target rows support a nonlinear correction " + index);
        }
        var duplicate = empty.WithPoint(0, Point(Vector2.Zero, Degrees(-1, 0)))
            .WithPoint(CalibrationGrid.Count - 1, Point(Vector2.Zero, Degrees(1, 0)));
        Vector3 contradictory = new RbfCalibrationField(duplicate).Correct(-Vector3.UnitZ);
        Require(float.IsFinite(contradictory.X) && float.IsFinite(contradictory.Y) && float.IsFinite(contradictory.Z) &&
            Math.Abs(contradictory.Length() - 1) < 0.00001,
            "Duplicate measured centers remain finite with deterministic precedence");

        var full = empty;
        for (int index = 0; index < CalibrationGrid.Count; index++)
        {
            Vector2 expected = CalibrationGrid.TargetAngles(index);
            Vector2 offset = Degrees(1 + 0.9f * MathF.Sin(expected.X * 4),
                -0.5f + 0.7f * MathF.Sin(expected.Y * 5));
            full = full.WithPoint(index, Point(expected - offset, expected));
        }
        var nonlinear = new RbfCalibrationField(full);
        double squareError = 0;
        foreach (var point in full.Targets)
        {
            var sample = point.Value;
            CalibrationGrid.TryAngles(nonlinear.Correct(CalibrationGrid.FromAngles(new Vector2(sample.Yaw, sample.Pitch))),
                out var corrected);
            squareError += Vector2.DistanceSquared(corrected, new Vector2(sample.TargetYaw, sample.TargetPitch));
        }
        Require(Math.Sqrt(squareError / CalibrationGrid.Count) / CalibrationGrid.Degrees < 0.0001,
            "A full grid recovers spatially varying offsets with an exact fit within numerical precision");
        CalibrationGrid.TryAngles(nonlinear.Correct(CalibrationGrid.FromAngles(Degrees(70, 50))), out var extrapolated);
        Require(Vector2.Distance(extrapolated, Degrees(70, 50)) / CalibrationGrid.Degrees < 2.5,
            "Sparse extrapolation stays within observed correction magnitudes");

        var robustSamples = Enumerable.Range(0, 100)
            .Select(i => Sample(i * 0.01, Degrees(2 + (i % 2 == 0 ? -0.02f : 0.02f), 0), Vector2.Zero))
            .Append(Sample(1, Degrees(30, 0), Vector2.Zero)).Append(Sample(1.01, Degrees(2, 0), Vector2.Zero));
        var robust = new RbfCalibrationField(empty.WithCapture(CalibrationGrid.CenterIndex, robustSamples));
        NearAngles(robust.Correct(CalibrationGrid.FromAngles(Degrees(2, 0))), Vector2.Zero, 0.03f,
            "Occasional large gaze outliers do not pull a target's correction");

        var coarse = new[]
        {
            Sample(0, Degrees(2, 0), Vector2.Zero), Sample(0.1, Degrees(2.4f, 0), Vector2.Zero),
            Sample(0.2, Degrees(2.4f, 0), Vector2.Zero), Sample(0.3, Degrees(2.4f, 0), Vector2.Zero)
        };
        var dense = Enumerable.Range(0, 31)
            .Select(i => Sample(i * 0.01, Degrees(i < 10 ? 2 : 2.4f, 0), Vector2.Zero));
        Vector3 coarseResult = new RbfCalibrationField(empty.WithCapture(CalibrationGrid.CenterIndex, coarse)).Correct(-Vector3.UnitZ);
        Vector3 denseResult = new RbfCalibrationField(empty.WithCapture(CalibrationGrid.CenterIndex, dense)).Correct(-Vector3.UnitZ);
        Require(Vector3.Distance(coarseResult, denseResult) < 0.00001,
            "Time weighting gives repeated polling the same influence at different polling rates");
        var gapped = coarse.Concat(new[]
        {
            Sample(100, Degrees(2, 0), Vector2.Zero), Sample(100.1, Degrees(2.4f, 0), Vector2.Zero),
            Sample(100.2, Degrees(2.4f, 0), Vector2.Zero), Sample(100.3, Degrees(2.4f, 0), Vector2.Zero)
        });
        var restarted = coarse.Concat(coarse);
        Require(Vector3.Distance(coarseResult, new RbfCalibrationField(empty.WithCapture(CalibrationGrid.CenterIndex, gapped)).Correct(-Vector3.UnitZ)) < 0.00001 &&
            Vector3.Distance(coarseResult, new RbfCalibrationField(empty.WithCapture(CalibrationGrid.CenterIndex, restarted)).Correct(-Vector3.UnitZ)) < 0.00001,
            "Tracking gaps and sampling time resets add no artificial dwell weight");
        var originAware = empty.WithCapture(CalibrationGrid.CenterIndex, new[]
        {
            Sample(0, Degrees(2, 0), Degrees(0.3f, -0.2f))
        });
        NearAngles(new RbfCalibrationField(originAware).Correct(CalibrationGrid.FromAngles(Degrees(2, 0))),
            Degrees(0.3f, -0.2f), 0.0001f,
            "Per-sample expected directions preserve eye-origin-dependent target alignment");
        StoreChecks(full);
    }

    private static void StoreChecks(CalibrationProfile profile)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "SteamDashET-calibration-check-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(temporary, "calibration.json");
        try
        {
            CalibrationStore.Save(path, profile);
            var loaded = CalibrationStore.Load(path, "headset");
            Require(loaded.PopulatedTargetCount == profile.PopulatedTargetCount &&
                loaded.Targets.Count == 108 && Enumerable.Range(0, CalibrationGrid.Count)
                    .All(index => loaded.Targets[index] == profile.Targets[index]),
                "All 108 target averages and headset identity survive atomic saving and loading");
            string saved = File.ReadAllText(path);
            using (var document = JsonDocument.Parse(saved))
            {
                JsonElement root = document.RootElement;
                Require(root.EnumerateObject().Count() == 4 &&
                    root.GetProperty("Rows").GetInt32() == CalibrationGrid.Rows &&
                    root.GetProperty("Columns").GetInt32() == CalibrationGrid.Columns &&
                    root.GetProperty("HeadsetId").GetString() == profile.HeadsetId &&
                    root.GetProperty("Targets").GetArrayLength() == CalibrationGrid.Count &&
                    root.GetProperty("Targets").EnumerateArray().All(point => point.ValueKind == JsonValueKind.Object &&
                        point.EnumerateObject().Count() == 4 && point.TryGetProperty("Yaw", out _) &&
                        point.TryGetProperty("Pitch", out _) && point.TryGetProperty("TargetYaw", out _) &&
                        point.TryGetProperty("TargetPitch", out _)) && !saved.Contains("TimeSeconds", StringComparison.Ordinal) &&
                    new FileInfo(path).Length < 32 * 1024,
                    "Calibration files contain the current grid and headset header with four averaged angles per populated target and no raw samples or timestamps");
            }
            Require(CalibrationStore.Load(path, "different-headset").PopulatedTargetCount == 0 && File.ReadAllText(path) == saved,
                "A different headset cannot use calibration and loading preserves the existing file");
            Require(CalibrationStore.Load(path, null).HeadsetId == "headset" &&
                CalibrationStore.Load(path, null).PopulatedTargetCount == profile.PopulatedTargetCount,
                "The UI can inspect saved calibration before a headset connects");
            var rerecorded = loaded.WithCapture(CalibrationGrid.CenterIndex,
                new[] { Sample(0, Degrees(3, 1), Vector2.Zero), Sample(0.1, Degrees(3, 1), Vector2.Zero) });
            CalibrationStore.Save(path, rerecorded);
            var rerecordedLoaded = CalibrationStore.Load(path, "headset");
            Require(PointNear(rerecordedLoaded.Targets[CalibrationGrid.CenterIndex], Point(Degrees(3, 1), Vector2.Zero)) &&
                Enumerable.Range(0, CalibrationGrid.Count).Where(index => index != CalibrationGrid.CenterIndex)
                    .All(index => rerecordedLoaded.Targets[index] == loaded.Targets[index]),
                "A replacement capture remains the sole target average after saving and relaunch loading");
            var onePoint = CalibrationProfile.Empty("headset").WithPoint(CalibrationGrid.CenterIndex,
                Point(Degrees(2, 0), Vector2.Zero));
            CalibrationStore.Save(path, onePoint);
            using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Require(document.RootElement.GetProperty("Targets").EnumerateArray()
                        .Count(point => point.ValueKind == JsonValueKind.Null) == CalibrationGrid.Count - 1 &&
                    CalibrationStore.Load(path, "headset").Targets.SequenceEqual(onePoint.Targets),
                    "Unrecorded targets save as null and a partial profile round trips its averages");
            }
            File.WriteAllText(path, saved.Replace("\"TargetYaw\":", "\"MissingTargetYaw\":", StringComparison.Ordinal));
            Require(CalibrationStore.Load(path, "headset").PopulatedTargetCount == 0,
                "Missing expected-angle fields are rejected instead of defaulting to a center target");
            File.WriteAllText(path, "{ invalid JSON");
            Require(CalibrationStore.Load(path, "headset").PopulatedTargetCount == 0,
                "A malformed calibration file does not prevent startup");
            File.Delete(path);
            Require(CalibrationStore.Load(path, "headset").PopulatedTargetCount == 0,
                "A missing calibration file starts with no correction");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(temporary)) Directory.Delete(temporary);
        }
    }

    private static Vector2 Degrees(float yaw, float pitch) => new(yaw * CalibrationGrid.Degrees, pitch * CalibrationGrid.Degrees);
    private static CalibrationSample Sample(double time, Vector2 measured, Vector2 expected) =>
        new(time, measured.X, measured.Y, expected.X, expected.Y);
    private static CalibrationPoint Point(Vector2 measured, Vector2 expected) =>
        new(measured.X, measured.Y, expected.X, expected.Y);
    private static float PointDistance(CalibrationPoint first, CalibrationPoint second) => MathF.Max(
        Vector2.Distance(new Vector2(first.Yaw, first.Pitch), new Vector2(second.Yaw, second.Pitch)),
        Vector2.Distance(new Vector2(first.TargetYaw, first.TargetPitch), new Vector2(second.TargetYaw, second.TargetPitch)));
    private static bool PointNear(CalibrationPoint? actual, CalibrationPoint expected) =>
        actual.HasValue && PointDistance(actual.Value, expected) < 0.000001f;

    private static void NearAngles(Vector3 actual, Vector2 expected, float toleranceDegrees, string message)
    {
        Require(CalibrationGrid.TryAngles(actual, out var angles) &&
            Vector2.Distance(angles, expected) <= toleranceDegrees * CalibrationGrid.Degrees, message);
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
