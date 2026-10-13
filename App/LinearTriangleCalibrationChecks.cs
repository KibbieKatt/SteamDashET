using System.Numerics;

namespace SteamDashET;

internal static class LinearTriangleCalibrationChecks
{
    private const float AngularTolerance = 0.000003f;

    internal static void Run()
    {
        SparseChecks();
        TriangleChecks();
        BoundaryChecks();
        FullGridChecks();
        DuplicateAndGeometryChecks();
    }

    private static void SparseChecks()
    {
        var empty = new LinearTriangleCalibrationField(CalibrationProfile.Empty("headset"));
        Vector3 unchanged = new(0.1f, 0.2f, -2);
        Require(empty.Correct(unchanged) == unchanged && empty.Correct(Vector3.Zero) == Vector3.Zero,
            "Empty triangle calibration leaves the input unchanged");

        Vector2 center = new(0.1f, -0.08f), offset = new(0.03f, -0.02f);
        var one = Field(Point(center, offset));
        Near(one, center, center + offset, "One triangle calibration anchor is exact");
        Vector2 distant = new(-0.4f, 0.3f);
        Near(one, distant, distant + offset, "One triangle calibration point supplies a constant correction everywhere");
        Require(one.Correct(Vector3.Zero) == Vector3.Zero && one.Correct(Vector3.UnitZ) == Vector3.UnitZ &&
            float.IsNaN(one.Correct(new Vector3(float.NaN, 0, -1)).X),
            "Triangle calibration preserves invalid live gaze instead of fabricating a valid ray");
        Require(MathF.Abs(one.Correct(new Vector3(0, 0, -2)).Length() - 1) < 0.000001f,
            "Triangle calibration returns a normalized corrected direction");

        Vector2 left = new(-0.2f, 0), right = new(0.2f, 0);
        Vector2 leftOffset = new(-0.03f, 0.02f), rightOffset = new(0.04f, -0.02f);
        var two = Field(Point(left, leftOffset), Point(right, rightOffset));
        Near(two, left, left + leftOffset, "Two-point triangle calibration preserves the first anchor");
        Near(two, right, right + rightOffset, "Two-point triangle calibration preserves the second anchor");
        Vector2 projected = new(0, 0.1f);
        Near(two, projected, projected + (leftOffset + rightOffset) / 2,
            "Two-point calibration blends offsets after projecting onto the measured segment");
        Vector2 beyond = new(0.4f, -0.1f);
        Near(two, beyond, beyond + rightOffset, "Two-point calibration clamps correction to the nearest endpoint outside the segment");

        foreach (bool vertical in new[] { false, true })
        {
            Vector2[] positions = Enumerable.Range(0, 5).Select(i => vertical ?
                new Vector2(0, (i - 2) * 0.1f) : new Vector2((i - 2) * 0.1f, 0)).ToArray();
            Vector2[] offsets = Enumerable.Range(0, 5)
                .Select(i => new Vector2(0.01f * i * i, -0.008f * i)).ToArray();
            var line = Field(positions.Select((position, index) => Point(position, offsets[index])).ToArray());
            string orientation = vertical ? "vertical" : "horizontal";
            for (int i = 0; i < positions.Length; i++)
                Near(line, positions[i], positions[i] + offsets[i],
                    $"Collinear {orientation} calibration preserves anchor {i}");
            for (int i = 0; i < positions.Length - 1; i++)
            {
                Vector2 middle = (positions[i] + positions[i + 1]) / 2;
                Vector2 offLine = middle + (vertical ? new Vector2(0.03f, 0) : new Vector2(0, 0.03f));
                Near(line, offLine, offLine + (offsets[i] + offsets[i + 1]) / 2,
                    $"Collinear {orientation} calibration uses the neighboring segment {i}");
            }
        }
    }

    private static void TriangleChecks()
    {
        Vector2 a = Vector2.Zero, b = new(0.4f, 0), c = new(0, 0.4f);
        Vector2 aOffset = new(0.02f, -0.01f), bOffset = new(0.05f, 0.03f), cOffset = new(-0.03f, 0.04f);
        var triangle = Field(Point(a, aOffset), Point(b, bOffset), Point(c, cOffset));
        Vector2 interior = new(0.1f, 0.08f);
        Near(triangle, interior, interior + aOffset * 0.55f + bOffset * 0.25f + cOffset * 0.2f,
            "Triangle interiors use barycentric blending of both angular correction components");
        Near(triangle, (a + b + c) / 3, (a + b + c + aOffset + bOffset + cOffset) / 3,
            "A triangle centroid receives the mean of its three correction offsets");

        Vector2[] locations = [new(-0.3f, -0.25f), new(0.35f, -0.2f), new(0.31f, 0.27f),
            new(-0.28f, 0.3f), new(0.025f, 0.035f), new(-0.11f, -0.03f)];
        var affine = Field(locations.Select(position => Point(position, AffineOffset(position))).ToArray());
        foreach (Vector2 query in new[] { new Vector2(-0.15f, 0.08f), new Vector2(0.18f, -0.1f),
            new Vector2(0.13f, 0.19f), Vector2.Zero, new Vector2(-0.2f, -0.15f) })
            Near(affine, query, query + AffineOffset(query),
                $"Triangle interpolation exactly reproduces an affine correction at {query}");
    }

    private static void BoundaryChecks()
    {
        // D lies inside the circumcircle of ABC, so BD is the Delaunay diagonal.
        Vector2 a = Vector2.Zero, b = new(0.4f, 0), c = new(0.4f, 0.4f), d = new(0, 0.2f);
        Vector2 aOffset = new(0.01f, 0.01f), bOffset = new(0.05f, 0),
            cOffset = new(-0.01f, 0.03f), dOffset = new(0, -0.03f);
        var field = Field(Point(a, aOffset), Point(b, bOffset), Point(c, cOffset), Point(d, dOffset));
        Vector2 shared = (b + d) / 2, sharedOffset = (bOffset + dOffset) / 2;
        Near(field, shared, shared + sharedOffset,
            "A shared Delaunay edge uses its two endpoint corrections");
        Vector2 perpendicular = Vector2.Normalize(new Vector2(1, 2)) * 0.000001f;
        Vector2 below = shared - perpendicular, above = shared + perpendicular;
        Require(Vector2.Distance(Correction(field, below), sharedOffset) < AngularTolerance &&
            Vector2.Distance(Correction(field, above), sharedOffset) < AngularTolerance,
            "The correction remains continuous across a shared triangle edge");

        Vector2 edge = new(0.1f, 0), edgeOffset = aOffset * 0.75f + bOffset * 0.25f;
        Near(field, edge, edge + edgeOffset, "Convex hull edges interpolate their endpoint offsets");
        Vector2 outside = new(0.1f, -0.05f);
        Near(field, outside, outside + edgeOffset,
            "Outside the calibrated hull the correction extends from the nearest boundary point");
        Vector2 farther = new(0.1f, -0.3f);
        Near(field, farther, farther + edgeOffset,
            "Moving normally away from the same hull edge retains its boundary correction");
        Require(Vector2.Distance(Correction(field, edge + new Vector2(0, 0.000001f)), edgeOffset) < AngularTolerance &&
            Vector2.Distance(Correction(field, edge - new Vector2(0, 0.000001f)), edgeOffset) < AngularTolerance,
            "The correction remains continuous when crossing the calibrated hull");
        Vector2 outsideCorner = new(-0.2f, -0.2f);
        Near(field, outsideCorner, outsideCorner + aOffset,
            "Outside a hull corner the nearest anchor supplies its correction");
    }

    private static void FullGridChecks()
    {
        var points = new CalibrationPoint[CalibrationGrid.Count];
        for (int index = 0; index < points.Length; index++)
        {
            Vector2 expected = CalibrationGrid.TargetAngles(index);
            Vector2 offset = new(0.02f + 0.013f * MathF.Sin(expected.X * 5) + 0.003f * MathF.Sin(index * 1.7f),
                -0.012f + 0.009f * MathF.Cos(expected.Y * 6) + 0.002f * MathF.Cos(index * 2.3f));
            points[index] = Point(expected - offset, offset);
        }
        var field = Field(points);
        for (int index = 0; index < points.Length; index++)
        {
            CalibrationPoint point = points[index];
            Near(field, new Vector2(point.Yaw, point.Pitch), new Vector2(point.TargetYaw, point.TargetPitch),
                "Triangle interpolation passes through irregular measured grid anchor " + index);
        }

        bool approachesAnchors = true;
        foreach (var point in points)
        {
            Vector2 measured = new(point.Yaw, point.Pitch);
            Vector2 offset = new(point.TargetYaw - point.Yaw, point.TargetPitch - point.Pitch);
            foreach (var delta in new[] { new Vector2(.000001f, 0), new Vector2(-.000001f, 0),
                new Vector2(0, .000001f), new Vector2(0, -.000001f) })
            {
                // Skinny triangles can legitimately have steep gradients. Verify that
                // nearby corrections converge to the anchor instead of capping their slope.
                // Both probes stay outside the exact-node tolerance, with room for float
                // error from converting the probe's angles to a direction and back.
                float fartherError = Vector2.Distance(Correction(field, measured + delta), offset);
                float nearerError = Vector2.Distance(Correction(field, measured + delta / 4), offset);
                approachesAnchors &= nearerError <= fartherError * .6f + .0000005f;
            }
        }
        Require(approachesAnchors,
            "The triangle field approaches every saved correction continuously without relying on exact-node handling");
        var reversed = Field(points.Reverse().ToArray());
        var shuffled = Field(Enumerable.Range(0, points.Length).OrderBy(index => (index * 47) % points.Length)
            .Select(index => points[index]).ToArray());
        bool stable = true, finite = true;
        for (int index = 0; index < 40; index++)
        {
            Vector2 query = new(-0.6f + (index % 8) * 0.17f, -0.66f + (index / 8) * 0.23f);
            Vector3 direction = CalibrationGrid.FromAngles(query);
            Vector3 corrected = field.Correct(direction);
            stable &= Vector3.Distance(corrected, reversed.Correct(direction)) < 0.000001f &&
                Vector3.Distance(corrected, shuffled.Correct(direction)) < 0.000001f;
            finite &= float.IsFinite(corrected.X) && float.IsFinite(corrected.Y) && float.IsFinite(corrected.Z) &&
                MathF.Abs(corrected.Length() - 1) < 0.000001f;
        }
        Require(stable, "Reordering distinct calibration anchors leaves the triangle correction field unchanged");
        Require(finite, "A full irregular grid produces finite unit directions inside and outside its calibrated region");
    }

    private static void DuplicateAndGeometryChecks()
    {
        Vector2 duplicateCenter = new(0.08f, -0.04f), firstOffset = new(-0.02f, 0), lastOffset = new(0.035f, -0.01f);
        var duplicateProfile = CalibrationProfile.Empty("headset")
            .WithPoint(1, Point(duplicateCenter, firstOffset))
            .WithPoint(CalibrationGrid.Count - 1, Point(duplicateCenter, lastOffset))
            .WithPoint(0, Point(new Vector2(-0.2f, -0.2f), new Vector2(0.01f, 0)))
            .WithPoint(2, Point(new Vector2(0.3f, 0.25f), new Vector2(0, 0.01f)));
        Near(new LinearTriangleCalibrationField(duplicateProfile), duplicateCenter, duplicateCenter + lastOffset,
            "Contradictory exact duplicates deterministically retain the highest target index");
        var reversedConstruction = CalibrationProfile.Empty("headset")
            .WithPoint(CalibrationGrid.Count - 1, Point(duplicateCenter, lastOffset))
            .WithPoint(1, Point(duplicateCenter, firstOffset));
        Near(new LinearTriangleCalibrationField(reversedConstruction), duplicateCenter, duplicateCenter + lastOffset,
            "Duplicate precedence depends on stored target index rather than profile construction order");

        Vector2 nearA = new(0.1f, 0.1f), nearB = new(0.10001f, 0.1f), nearC = new(0.1f, 0.15f);
        Vector2 nearOffsetA = new(0.01f, 0), nearOffsetB = new(0.02f, 0.01f), nearOffsetC = new(0.005f, -0.01f);
        var near = Field(Point(nearA, nearOffsetA), Point(nearB, nearOffsetB), Point(nearC, nearOffsetC));
        Near(near, nearA, nearA + nearOffsetA, "Near-distinct measured points retain their first exact correction");
        Near(near, nearB, nearB + nearOffsetB, "Near-distinct measured points retain their second exact correction");

        Vector2 skinnyA = new(-0.3f, 0), skinnyB = new(0.3f, 0), skinnyC = new(0.02f, 0.00001f);
        Vector2 skinnyOffsetA = new(-0.01f, 0.01f), skinnyOffsetB = new(0.04f, -0.01f), skinnyOffsetC = new(0.02f, 0.03f);
        var skinny = Field(Point(skinnyA, skinnyOffsetA), Point(skinnyB, skinnyOffsetB), Point(skinnyC, skinnyOffsetC));
        Vector2 skinnyInterior = skinnyA * 0.2f + skinnyB * 0.3f + skinnyC * 0.5f;
        Near(skinny, skinnyInterior, skinnyInterior + skinnyOffsetA * 0.2f + skinnyOffsetB * 0.3f + skinnyOffsetC * 0.5f,
            "A skinny non-collinear triangle retains its barycentric correction");

        CalibrationPoint[] nearLinePoints = Enumerable.Range(0, 12).Select(index =>
        {
            Vector2 measured = new((index - 5.5f) * 0.04f, 0.00001f * MathF.Sin(index * 1.3f));
            Vector2 correction = new(0.01f * MathF.Sin(index * 0.7f), 0.012f * MathF.Cos(index * 0.6f));
            return Point(measured, correction);
        }).ToArray();
        var nearLine = Field(nearLinePoints);
        for (int index = 0; index < nearLinePoints.Length; index++)
        {
            CalibrationPoint point = nearLinePoints[index];
            Near(nearLine, new Vector2(point.Yaw, point.Pitch), new Vector2(point.TargetYaw, point.TargetPitch),
                "Nearly collinear geometry preserves anchor " + index);
        }
    }

    private static Vector2 AffineOffset(Vector2 measured) =>
        new(0.02f + measured.X * 0.1f - measured.Y * 0.03f,
            -0.01f + measured.X * 0.04f + measured.Y * 0.08f);

    private static CalibrationPoint Point(Vector2 measured, Vector2 correction)
    {
        Vector2 expected = measured + correction;
        return new CalibrationPoint(measured.X, measured.Y, expected.X, expected.Y);
    }

    private static LinearTriangleCalibrationField Field(params CalibrationPoint[] points)
    {
        var profile = CalibrationProfile.Empty("headset");
        for (int index = 0; index < points.Length; index++) profile = profile.WithPoint(index, points[index]);
        return new LinearTriangleCalibrationField(profile);
    }

    private static Vector2 Correction(LinearTriangleCalibrationField field, Vector2 query)
    {
        bool valid = CalibrationGrid.TryAngles(field.Correct(CalibrationGrid.FromAngles(query)), out var corrected);
        if (!valid) throw new InvalidOperationException("Triangle calibration produced an invalid direction.");
        return corrected - query;
    }

    private static void Near(LinearTriangleCalibrationField field, Vector2 measured, Vector2 expected, string message)
    {
        bool valid = CalibrationGrid.TryAngles(field.Correct(CalibrationGrid.FromAngles(measured)), out var corrected);
        Require(valid && Vector2.Distance(corrected, expected) <= AngularTolerance,
            message + $" (angular error {Vector2.Distance(corrected, expected):G3} rad)");
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
