using System.Numerics;

namespace SteamDashET;

// Interpolate angular correction offsets over the measured gaze locations. The visible
// target grid is regular, but its measured locations can be irregular, sparse or collinear.
internal sealed class LinearTriangleCalibrationField : ICalibrationCorrectionField
{
    private const double NodeToleranceSquared = 1e-14;
    private readonly Node[] nodes;
    private readonly Triangle[] triangles;
    private readonly Edge[] boundary;
    private readonly Edge[] lineSegments;

    internal LinearTriangleCalibrationField(CalibrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var unique = new Dictionary<(float Yaw, float Pitch), Node>();
        foreach (var saved in profile.Targets)
        {
            if (saved is not CalibrationPoint point) continue;
            // Two identical inputs cannot have different exact outputs. A later target
            // index wins deterministically; recapturing either target can resolve this.
            unique[(point.Yaw, point.Pitch)] = new Node(new Point(point.Yaw, point.Pitch),
                point.TargetYaw - (double)point.Yaw, point.TargetPitch - (double)point.Pitch,
                new Vector2(point.TargetYaw, point.TargetPitch));
        }
        nodes = unique.Values.OrderBy(node => node.Measured.X).ThenBy(node => node.Measured.Y).ToArray();
        triangles = [];
        boundary = [];
        lineSegments = [];
        if (nodes.Length < 2) return;

        int[] hull = ConvexHull();
        if (hull.Length < 3)
        {
            lineSegments = Enumerable.Range(0, nodes.Length - 1).Select(index => new Edge(index, index + 1)).ToArray();
            return;
        }

        var mesh = new List<Triangle>();
        for (int index = 1; index + 1 < hull.Length; index++)
            AddTriangle(mesh, hull[0], hull[index], hull[index + 1]);

        // Starting with the real hull avoids a finite supertriangle discarding very
        // skinny triangles whose circumcircles reach arbitrarily far from the data.
        var hullNodes = hull.ToHashSet();
        for (int index = 0; index < nodes.Length; index++)
            if (!hullNodes.Contains(index)) Insert(mesh, index);
        MakeDelaunay(mesh);
        triangles = mesh.ToArray();
        // Hull sides can contain additional saved nodes with their own corrections.
        // Use the final mesh edges so outside continuation preserves those constraints.
        var edgeCounts = new Dictionary<Edge, int>();
        foreach (var triangle in triangles)
            foreach (var edge in triangle.Edges)
                edgeCounts[edge] = edgeCounts.GetValueOrDefault(edge) + 1;
        boundary = edgeCounts.Where(pair => pair.Value == 1).Select(pair => pair.Key)
            .OrderBy(edge => edge.A).ThenBy(edge => edge.B).ToArray();
    }

    public Vector3 Correct(Vector3 direction)
    {
        if (nodes.Length == 0 || !CalibrationGrid.TryAngles(direction, out var measured)) return direction;
        var query = new Point(measured.X, measured.Y);
        int nearest = 0;
        double nearestDistance = DistanceSquared(query, nodes[0].Measured);
        for (int index = 1; index < nodes.Length; index++)
        {
            double distance = DistanceSquared(query, nodes[index].Measured);
            if (distance < nearestDistance) { nearest = index; nearestDistance = distance; }
        }
        // Angle -> unit vector -> angle can change the last float bits. This tolerance
        // only restores the saved constraint within 0.000006 degrees of its location.
        if (nearestDistance <= NodeToleranceSquared) return Direction(nodes[nearest].Target, direction);

        (double Yaw, double Pitch) correction;
        if (nodes.Length == 1)
            correction = (nodes[0].YawOffset, nodes[0].PitchOffset);
        else if (triangles.Length == 0)
            correction = OnNearestSegment(query, lineSegments);
        else if (!TryInside(query, out correction))
            // Continue the nearest boundary correction outside the calibrated region.
            // At an edge this agrees with the adjacent triangle, without extrapolation.
            correction = OnNearestSegment(query, boundary);

        var corrected = new Vector2((float)(measured.X + correction.Yaw),
            (float)(measured.Y + correction.Pitch));
        // Inside triangles the output is a convex combination of valid saved targets,
        // so these limits only affect extreme directions outside the calibrated region.
        corrected.X = Math.Clamp(corrected.X, -89 * CalibrationGrid.Degrees, 89 * CalibrationGrid.Degrees);
        corrected.Y = Math.Clamp(corrected.Y, -89 * CalibrationGrid.Degrees, 89 * CalibrationGrid.Degrees);
        return Direction(corrected, direction);
    }

    private static Vector3 Direction(Vector2 angles, Vector3 fallback)
    {
        Vector3 result = CalibrationGrid.FromAngles(angles);
        float lengthSquared = result.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 0.000001f
            ? Vector3.Normalize(result) : fallback;
    }

    private bool TryInside(Point query, out (double Yaw, double Pitch) correction)
    {
        foreach (var triangle in triangles)
        {
            Node a = nodes[triangle.A], b = nodes[triangle.B], c = nodes[triangle.C];
            double aWeight = Cross(b.Measured, c.Measured, query);
            double bWeight = Cross(c.Measured, a.Measured, query);
            double cWeight = Cross(a.Measured, b.Measured, query);
            if (aWeight < 0 || bWeight < 0 || cWeight < 0) continue;
            double total = aWeight + bWeight + cWeight;
            aWeight /= total; bWeight /= total; cWeight /= total;
            correction = (aWeight * a.YawOffset + bWeight * b.YawOffset + cWeight * c.YawOffset,
                aWeight * a.PitchOffset + bWeight * b.PitchOffset + cWeight * c.PitchOffset);
            return true;
        }
        correction = default;
        return false;
    }

    private (double Yaw, double Pitch) OnNearestSegment(Point query, Edge[] edges)
    {
        double nearestDistance = double.PositiveInfinity;
        (double Yaw, double Pitch) correction = default;
        foreach (var edge in edges)
        {
            Node a = nodes[edge.A], b = nodes[edge.B];
            double dx = b.Measured.X - a.Measured.X, dy = b.Measured.Y - a.Measured.Y;
            double fraction = Math.Clamp(((query.X - a.Measured.X) * dx +
                (query.Y - a.Measured.Y) * dy) / (dx * dx + dy * dy), 0, 1);
            double x = a.Measured.X + fraction * dx, y = a.Measured.Y + fraction * dy;
            double distance = (query.X - x) * (query.X - x) + (query.Y - y) * (query.Y - y);
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            correction = (a.YawOffset + fraction * (b.YawOffset - a.YawOffset),
                a.PitchOffset + fraction * (b.PitchOffset - a.PitchOffset));
        }
        return correction;
    }

    private int[] ConvexHull()
    {
        var lower = new List<int>();
        for (int index = 0; index < nodes.Length; index++) Append(lower, index);
        var upper = new List<int>();
        for (int index = nodes.Length - 1; index >= 0; index--) Append(upper, index);
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower.ToArray();

        void Append(List<int> chain, int index)
        {
            while (chain.Count >= 2 && Cross(nodes[chain[^2]].Measured,
                nodes[chain[^1]].Measured, nodes[index].Measured) <= 0) chain.RemoveAt(chain.Count - 1);
            chain.Add(index);
        }
    }

    private void Insert(List<Triangle> mesh, int index)
    {
        Point query = nodes[index].Measured;
        for (int triangleIndex = 0; triangleIndex < mesh.Count; triangleIndex++)
        {
            Triangle triangle = mesh[triangleIndex];
            double ab = Cross(nodes[triangle.A].Measured, nodes[triangle.B].Measured, query);
            double bc = Cross(nodes[triangle.B].Measured, nodes[triangle.C].Measured, query);
            double ca = Cross(nodes[triangle.C].Measured, nodes[triangle.A].Measured, query);
            if (ab < 0 || bc < 0 || ca < 0) continue;
            if (ab == 0 || bc == 0 || ca == 0)
            {
                Edge edge = ab == 0 ? new Edge(triangle.A, triangle.B) :
                    bc == 0 ? new Edge(triangle.B, triangle.C) : new Edge(triangle.C, triangle.A);
                // An interior edge has two owners; a hull edge has one. Split both
                // owners together so a point on an existing edge remains in the mesh.
                for (int owner = mesh.Count - 1; owner >= 0; owner--)
                {
                    int third = Opposite(mesh[owner], edge);
                    if (third < 0) continue;
                    mesh.RemoveAt(owner);
                    AddTriangle(mesh, edge.A, index, third);
                    AddTriangle(mesh, index, edge.B, third);
                }
            }
            else
            {
                mesh.RemoveAt(triangleIndex);
                AddTriangle(mesh, triangle.A, triangle.B, index);
                AddTriangle(mesh, triangle.B, triangle.C, index);
                AddTriangle(mesh, triangle.C, triangle.A, index);
            }
            return;
        }
        throw new InvalidOperationException("A calibration point could not be inserted into its measured gaze hull.");
    }

    private void MakeDelaunay(List<Triangle> mesh)
    {
        // Strict incircle comparisons leave cocircular diagonals unchanged. Each flip
        // improves the Delaunay triangulation and the finite set of diagonals terminates.
        while (true)
        {
            var owners = new Dictionary<Edge, int>();
            bool flipped = false;
            for (int index = 0; index < mesh.Count && !flipped; index++)
            {
                Triangle triangle = mesh[index];
                foreach (var edge in triangle.Edges)
                {
                    if (!owners.TryGetValue(edge, out int first)) { owners[edge] = index; continue; }
                    int p = Opposite(mesh[first], edge), q = Opposite(triangle, edge);
                    Point a = nodes[edge.A].Measured, b = nodes[edge.B].Measured;
                    Point c = nodes[p].Measured, d = nodes[q].Measured;
                    if (Math.Sign(Cross(c, d, a)) * Math.Sign(Cross(c, d, b)) >= 0) continue;
                    int orientation = Math.Sign(Cross(a, b, c));
                    if (orientation * InCircle(a, b, c, d) <= 0) continue;
                    mesh[first] = Oriented(p, q, edge.A);
                    mesh[index] = Oriented(q, p, edge.B);
                    flipped = true;
                    break;
                }
            }
            if (!flipped) return;
        }
    }

    private void AddTriangle(List<Triangle> mesh, int a, int b, int c)
    {
        double orientation = Cross(nodes[a].Measured, nodes[b].Measured, nodes[c].Measured);
        if (orientation != 0) mesh.Add(orientation > 0 ? new Triangle(a, b, c) : new Triangle(a, c, b));
    }

    private Triangle Oriented(int a, int b, int c) =>
        Cross(nodes[a].Measured, nodes[b].Measured, nodes[c].Measured) > 0
            ? new Triangle(a, b, c) : new Triangle(a, c, b);

    private static int Opposite(Triangle triangle, Edge edge)
    {
        if (!triangle.Contains(edge.A) || !triangle.Contains(edge.B)) return -1;
        return triangle.A != edge.A && triangle.A != edge.B ? triangle.A :
            triangle.B != edge.A && triangle.B != edge.B ? triangle.B : triangle.C;
    }

    private static double DistanceSquared(Point a, Point b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    private static double Cross(Point a, Point b, Point c)
    {
        double left = (b.X - a.X) * (c.Y - a.Y), right = (b.Y - a.Y) * (c.X - a.X);
        double determinant = left - right;
        if (left == 0 && right == 0) return 0;
        if (Math.Abs(determinant) > (Math.Abs(left) + Math.Abs(right)) * 1e-14) return determinant;
        // Saved coordinates and live angles are floats. Scaling by 2^149 expresses
        // every possible finite float exactly as an integer, including subnormals.
        BigInteger exact = (Exact(b.X) - Exact(a.X)) * (Exact(c.Y) - Exact(a.Y)) -
            (Exact(b.Y) - Exact(a.Y)) * (Exact(c.X) - Exact(a.X));
        return (double)exact * Math.ScaleB(1.0, -298);
    }

    private static int InCircle(Point a, Point b, Point c, Point d)
    {
        double ax = a.X - d.X, ay = a.Y - d.Y, bx = b.X - d.X, by = b.Y - d.Y;
        double cx = c.X - d.X, cy = c.Y - d.Y;
        double aLift = ax * ax + ay * ay, bLift = bx * bx + by * by, cLift = cx * cx + cy * cy;
        double determinant = aLift * (bx * cy - by * cx) + bLift * (cx * ay - cy * ax) +
            cLift * (ax * by - ay * bx);
        double permanent = aLift * (Math.Abs(bx * cy) + Math.Abs(by * cx)) +
            bLift * (Math.Abs(cx * ay) + Math.Abs(cy * ax)) + cLift * (Math.Abs(ax * by) + Math.Abs(ay * bx));
        if (Math.Abs(determinant) > permanent * 1e-14) return Math.Sign(determinant);
        BigInteger x1 = Exact(a.X) - Exact(d.X), y1 = Exact(a.Y) - Exact(d.Y);
        BigInteger x2 = Exact(b.X) - Exact(d.X), y2 = Exact(b.Y) - Exact(d.Y);
        BigInteger x3 = Exact(c.X) - Exact(d.X), y3 = Exact(c.Y) - Exact(d.Y);
        return ((x1 * x1 + y1 * y1) * (x2 * y3 - y2 * x3) +
            (x2 * x2 + y2 * y2) * (x3 * y1 - y3 * x1) +
            (x3 * x3 + y3 * y3) * (x1 * y2 - y1 * x2)).Sign;
    }

    private static BigInteger Exact(double coordinate)
    {
        uint bits = BitConverter.SingleToUInt32Bits((float)coordinate);
        int exponent = (int)((bits >> 23) & 255);
        BigInteger mantissa = exponent == 0 ? bits & 0x7fffff : (bits & 0x7fffff) | 0x800000;
        if (exponent != 0) mantissa <<= exponent - 1;
        return (bits & 0x80000000) == 0 ? mantissa : -mantissa;
    }

    private readonly record struct Point(double X, double Y);
    private readonly record struct Node(Point Measured, double YawOffset, double PitchOffset, Vector2 Target);
    private readonly record struct Edge
    {
        internal Edge(int a, int b) { A = Math.Min(a, b); B = Math.Max(a, b); }
        internal int A { get; }
        internal int B { get; }
    }
    private readonly record struct Triangle(int A, int B, int C)
    {
        internal bool Contains(int index) => A == index || B == index || C == index;
        internal Edge[] Edges => [new Edge(A, B), new Edge(B, C), new Edge(C, A)];
    }
}
