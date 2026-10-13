using System.Numerics;

namespace SteamDashET;

internal static class RbfInterpolationChecks
{
    private const float AngularTolerance = 0.000001f;

    internal static void Run()
    {
        SparseChecks();
        KernelAndSmallResidualChecks();
        FullGridChecks();
        DuplicateAndClosePointChecks();
    }

    private static void SparseChecks()
    {
        var empty = new RbfCalibrationField(CalibrationProfile.Empty("headset"));
        Vector3 unchanged = new(0.1f, -0.2f, -2);
        Require(empty.Correct(unchanged) == unchanged && empty.Correct(Vector3.Zero) == Vector3.Zero,
            "Empty exact RBF calibration preserves the original input");

        Vector2 measured = new(0.07f, -0.04f), offset = new(0.02f, -0.01f);
        var one = Field(Point(measured, offset));
        Near(one, measured, measured + offset, AngularTolerance, "One exact RBF point aligns with its saved average");
        Vector2 away = new(-0.4f, 0.3f);
        Near(one, away, away + offset, AngularTolerance, "One exact RBF point continues its constant offset elsewhere");
        Require(one.Correct(Vector3.Zero) == Vector3.Zero && one.Correct(Vector3.UnitZ) == Vector3.UnitZ &&
            float.IsNaN(one.Correct(new Vector3(float.NaN, 0, -1)).X),
            "Exact RBF calibration leaves invalid gaze invalid");

        CalibrationPoint[] sparsePoints =
        [
            Point(new(-0.16f, 0.09f), new(0.017f, -0.009f)),
            Point(new(0.12f, 0.07f), new(0.031f, 0.006f)),
            Point(new(0.03f, -0.11f), new(-0.011f, 0.021f))
        ];
        CheckAnchors(Field(sparsePoints), sparsePoints, "Sparse exact RBF anchor");

        foreach (bool vertical in new[] { false, true })
        {
            CalibrationPoint[] points = Enumerable.Range(0, 9).Select(index =>
            {
                Vector2 center = vertical ? new(0, (index - 4) * 0.08f) : new((index - 4) * 0.08f, 0);
                Vector2 correction = new(0.015f + 0.006f * MathF.Sin(index * 0.8f),
                    -0.009f + 0.005f * MathF.Cos(index * 1.1f));
                return Point(center, correction);
            }).ToArray();
            CheckAnchors(Field(points), points, $"Collinear {(vertical ? "vertical" : "horizontal")} exact RBF anchor");
        }
    }

    private static void KernelAndSmallResidualChecks()
    {
        // At a perpendicular displacement of one kernel width from the first of
        // two centers, its fitted residual is reduced by exp(-1/2). The mean
        // offset remains, so this also verifies the intended far-field behavior.
        Vector2 left = Degrees(-6, 0), right = Degrees(6, 0);
        Vector2 leftOffset = new(0.02f, 0.01f), rightOffset = new(0.06f, 0.01f);
        var two = Field(Point(left, leftOffset), Point(right, rightOffset));
        Vector2 query = left + Degrees(0, 6);
        Vector2 expectedOffset = (leftOffset + rightOffset) / 2 +
            (leftOffset - rightOffset) / 2 * (float)Math.Exp(-0.5);
        Near(two, query, query + expectedOffset, AngularTolerance,
            "Exact RBF uses the six-degree Gaussian width with an unchanged mean continuation");
        Vector2 distant = Degrees(70, 50);
        Near(two, distant, distant + (leftOffset + rightOffset) / 2, AngularTolerance,
            "Exact RBF local detail fades back to the saved mean far from all centers");

        // These distinct offsets differ by less than the old near-constant
        // shortcut, but their difference is still measurable at float precision.
        Vector2 tinyLeft = new(-0.01f, 0), tinyRight = new(0.01f, 0);
        Vector2 tinyLeftOffset = new(0.0002f, -0.0001f);
        Vector2 tinyRightOffset = tinyLeftOffset + new Vector2(0.0000008f, -0.0000004f);
        var tiny = Field(Point(tinyLeft, tinyLeftOffset), Point(tinyRight, tinyRightOffset));
        Near(tiny, tinyLeft, tinyLeft + tinyLeftOffset, 0.00000006f,
            "Exact RBF preserves the first offset even when residual variation is below one microradian");
        Near(tiny, tinyRight, tinyRight + tinyRightOffset, 0.00000006f,
            "Exact RBF preserves the second offset instead of replacing tiny differences with their mean");
    }

    private static void FullGridChecks()
    {
        CalibrationPoint[] points = Enumerable.Range(0, CalibrationGrid.Count).Select(index =>
        {
            Vector2 target = CalibrationGrid.TargetAngles(index);
            Vector2 correction = new(0.018f + 0.008f * MathF.Sin(target.X * 5) + 0.002f * MathF.Sin(index * 1.7f),
                -0.012f + 0.006f * MathF.Cos(target.Y * 6) + 0.0015f * MathF.Cos(index * 2.3f));
            return Point(target - correction, correction);
        }).ToArray();
        var field = Field(points);
        CheckAnchors(field, points, "Exact RBF irregular full-grid anchor");

        bool continuous = true;
        foreach (var point in points)
        {
            Vector2 measured = new(point.Yaw, point.Pitch);
            Vector2 expectedOffset = new(point.TargetYaw - point.Yaw, point.TargetPitch - point.Pitch);
            foreach (Vector2 delta in new[] { new Vector2(0.0001f, 0), new Vector2(-0.0001f, 0),
                new Vector2(0, 0.0001f), new Vector2(0, -0.0001f) })
            {
                float farError = Vector2.Distance(Correction(field, measured + delta), expectedOffset);
                float nearError = Vector2.Distance(Correction(field, measured + delta / 4), expectedOffset);
                // Compare convergence rather than requiring an arbitrary maximum
                // gradient: a valid exact fit can have a steep local slope.
                continuous &= nearError <= farError * 0.6f + 0.0000005f;
            }
        }
        Require(continuous, "The exact RBF field approaches every saved grid correction continuously");

        var reversed = Field(points.Reverse().ToArray());
        bool finite = true, bounded = true, orderIndependent = true;
        float maximumOffset = points.Max(point => new Vector2(point.TargetYaw - point.Yaw,
            point.TargetPitch - point.Pitch).Length());
        for (int index = 0; index < 70; index++)
        {
            Vector2 query = new(-0.8f + (index % 10) * 0.18f, -0.8f + (index / 10) * 0.23f);
            Vector3 input = CalibrationGrid.FromAngles(query), result = field.Correct(input);
            finite &= IsFiniteUnit(result);
            bounded &= Correction(field, query).Length() <= maximumOffset + AngularTolerance;
            orderIndependent &= Vector3.Distance(result, reversed.Correct(input)) < AngularTolerance;
        }
        Require(finite, "The exact RBF field stays finite and normalized inside and outside the measured grid");
        Require(bounded, "The exact RBF field retains correction-magnitude limits away from saved anchors");
        Require(orderIndependent, "Reordering distinct saved points leaves the exact RBF field unchanged");
    }

    private static void DuplicateAndClosePointChecks()
    {
        Vector2 duplicateCenter = new(0.04f, -0.02f), lastOffset = new(0.035f, -0.015f);
        var profile = CalibrationProfile.Empty("headset")
            .WithPoint(1, Point(duplicateCenter, new(-0.01f, 0.01f)))
            .WithPoint(CalibrationGrid.Count - 1, Point(duplicateCenter, lastOffset))
            .WithPoint(0, Point(new(-0.2f, -0.1f), new(0.01f, 0.005f)));
        Near(new RbfCalibrationField(profile), duplicateCenter, duplicateCenter + lastOffset, AngularTolerance,
            "Exact duplicate RBF inputs deterministically use the highest target index");
        var reversedWrites = CalibrationProfile.Empty("headset")
            .WithPoint(CalibrationGrid.Count - 1, Point(duplicateCenter, lastOffset))
            .WithPoint(1, Point(duplicateCenter, new(-0.01f, 0.01f)));
        Near(new RbfCalibrationField(reversedWrites), duplicateCenter, duplicateCenter + lastOffset, AngularTolerance,
            "Exact RBF duplicate precedence is independent of the order points were written");

        Vector2 closeA = new(0.1f, 0.05f), closeB = new(0.101f, 0.05f), closeC = new(-0.12f, -0.08f);
        CalibrationPoint[] closePoints = [Point(closeA, new(0.02f, -0.005f)),
            Point(closeB, new(0.0202f, -0.0048f)), Point(closeC, new(0.01f, 0.008f))];
        var close = Field(closePoints);
        CheckAnchors(close, closePoints, "Closely spaced distinct exact RBF anchor");
        Vector2 middle = (closeA + closeB) / 2;
        Vector3 result = close.Correct(CalibrationGrid.FromAngles(middle));
        Require(IsFiniteUnit(result), "Closely spaced RBF points produce a finite unit direction between their centers");
        foreach (Vector2 delta in new[] { new Vector2(0.00001f, 0), new Vector2(0, 0.00001f) })
        {
            Vector2 aOffset = new(closePoints[0].TargetYaw - closePoints[0].Yaw,
                closePoints[0].TargetPitch - closePoints[0].Pitch);
            float farError = Vector2.Distance(Correction(close, closeA + delta), aOffset);
            float nearError = Vector2.Distance(Correction(close, closeA + delta / 4), aOffset);
            Require(nearError <= farError * 0.6f + 0.0000005f,
                $"The exact RBF fit is continuous near closely spaced anchors along {delta}");
        }
    }

    private static bool IsFiniteUnit(Vector3 direction) => float.IsFinite(direction.X) &&
        float.IsFinite(direction.Y) && float.IsFinite(direction.Z) && MathF.Abs(direction.Length() - 1) < 0.000001f;

    private static Vector2 Degrees(float yaw, float pitch) => new(yaw * CalibrationGrid.Degrees, pitch * CalibrationGrid.Degrees);

    private static CalibrationPoint Point(Vector2 measured, Vector2 correction)
    {
        Vector2 expected = measured + correction;
        return new CalibrationPoint(measured.X, measured.Y, expected.X, expected.Y);
    }

    private static RbfCalibrationField Field(params CalibrationPoint[] points)
    {
        var profile = CalibrationProfile.Empty("headset");
        for (int index = 0; index < points.Length; index++) profile = profile.WithPoint(index, points[index]);
        return new RbfCalibrationField(profile);
    }

    private static void CheckAnchors(RbfCalibrationField field, CalibrationPoint[] points, string label)
    {
        for (int index = 0; index < points.Length; index++)
        {
            CalibrationPoint point = points[index];
            Near(field, new(point.Yaw, point.Pitch), new(point.TargetYaw, point.TargetPitch), AngularTolerance,
                label + " " + index);
        }
    }

    private static Vector2 Correction(RbfCalibrationField field, Vector2 query)
    {
        if (!CalibrationGrid.TryAngles(field.Correct(CalibrationGrid.FromAngles(query)), out var corrected))
            throw new InvalidOperationException("Exact RBF produced an invalid gaze direction.");
        return corrected - query;
    }

    private static void Near(RbfCalibrationField field, Vector2 measured, Vector2 expected, float tolerance, string message)
    {
        bool valid = CalibrationGrid.TryAngles(field.Correct(CalibrationGrid.FromAngles(measured)), out var corrected);
        Require(valid && Vector2.Distance(corrected, expected) <= tolerance,
            message + $" (angular error {Vector2.Distance(corrected, expected):G3} rad)");
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
