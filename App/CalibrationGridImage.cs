using System.Drawing;
using System.Numerics;

namespace SteamDashET;

internal static class CalibrationGridImage
{
    internal const int Width = 1024;
    internal const int Height = 1152;
    // Scale the whole grid with its distance so target angles and apparent dot sizes stay constant.
    internal const float PlaneDistance = 1f;
    internal const float PlaneWidth = PlaneDistance * 1.024f;
    internal const float PlaneHeight = PlaneWidth * Height / Width;
    // The plane extends farther below the forward target than above it.
    internal const float PlaneTop = PlaneDistance * 0.384f;
    internal const float PlaneCenterY = PlaneTop - PlaneHeight / 2;
    private const float DotRadius = PlaneDistance * 0.007f;
    private const float OutlineInnerRadius = PlaneDistance * 0.011f;
    private const float OutlineOuterRadius = PlaneDistance * 0.014f;
    private static readonly Color EmptyColor = Color.FromArgb(92, 172, 177, 184);
    private static readonly Color SavedColor = Color.FromArgb(105, 65, 205, 116);
    private static readonly Color SelectedColor = Color.FromArgb(220, 240, 243, 248);
    private static readonly Color CollectingColor = Color.FromArgb(235, 255, 198, 64);

    // Centers are projected onto the plane using the same yaw/pitch convention as the correction field.
    internal static Vector3 TargetPoint(int index)
    {
        Vector2 angles = CalibrationGrid.TargetAngles(index);
        return new Vector3(MathF.Tan(angles.X) * PlaneDistance,
            MathF.Tan(angles.Y) / MathF.Cos(angles.X) * PlaneDistance, -PlaneDistance);
    }

    internal static Vector2 PixelCenter(int index)
    {
        Vector3 point = TargetPoint(index);
        return new Vector2((point.X / PlaneWidth + 0.5f) * Width,
            (PlaneTop - point.Y) / PlaneHeight * Height);
    }

    internal static bool TryProject(Vector3 headOrigin, Vector3 headDirection, out Vector3 headPoint)
    {
        headPoint = default;
        if (!GazeInput.Finite(headOrigin) || !GazeInput.Finite(headDirection) ||
            headDirection.Z >= -0.000001f) return false;
        float distance = (-PlaneDistance - headOrigin.Z) / headDirection.Z;
        if (!float.IsFinite(distance) || distance <= 0) return false;
        Vector3 point = headOrigin + headDirection * distance;
        // The indicator is a separate overlay: keep off-grid errors visible for diagnosis.
        if (!GazeInput.Finite(point)) return false;
        headPoint = point;
        return true;
    }

    internal static byte[] CreatePixels(int selected, bool collecting, IReadOnlyList<bool> savedPoints)
    {
        var pixels = new byte[Width * Height * 4];
        DrawInto(pixels, selected, collecting, savedPoints);
        return pixels;
    }

    internal static void DrawInto(byte[] pixels, int selected, bool collecting, IReadOnlyList<bool> savedPoints)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != Width * Height * 4)
            throw new ArgumentException("The pixel buffer must match the calibration grid dimensions.", nameof(pixels));
        if (selected < 0 || selected >= CalibrationGrid.Count) throw new ArgumentOutOfRangeException(nameof(selected));
        ArgumentNullException.ThrowIfNull(savedPoints);
        if (savedPoints.Count != CalibrationGrid.Count)
            throw new ArgumentException("One saved point status is required for each calibration target.", nameof(savedPoints));
        Array.Clear(pixels);
        for (int index = 0; index < CalibrationGrid.Count; index++)
        {
            Vector2 center = PixelCenter(index);
            DrawCircle(pixels, center.X, center.Y, DotRadius, 0, savedPoints[index] ? SavedColor : EmptyColor);
            if (index == selected)
                DrawCircle(pixels, center.X, center.Y, OutlineOuterRadius, OutlineInnerRadius,
                    collecting ? CollectingColor : SelectedColor);
        }
    }

    private static void DrawCircle(byte[] pixels, float centerX, float centerY, float outerRadius,
        float innerRadius, Color color)
    {
        float pixelsPerMeter = Width / PlaneWidth;
        float outer = outerRadius * pixelsPerMeter;
        float inner = innerRadius * pixelsPerMeter;
        int startX = Math.Max(0, (int)MathF.Floor(centerX - outer - 1));
        int endX = Math.Min(Width - 1, (int)MathF.Ceiling(centerX + outer + 1));
        int startY = Math.Max(0, (int)MathF.Floor(centerY - outer - 1));
        int endY = Math.Min(Height - 1, (int)MathF.Ceiling(centerY + outer + 1));
        for (int y = startY; y <= endY; y++)
        for (int x = startX; x <= endX; x++)
        {
            float dx = x + 0.5f - centerX, dy = y + 0.5f - centerY;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            float coverage = Math.Clamp(outer + 0.5f - distance, 0, 1);
            if (inner > 0) coverage *= Math.Clamp(distance - inner + 0.5f, 0, 1);
            if (coverage <= 0) continue;
            int offset = (y * Width + x) * 4;
            pixels[offset] = color.R;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.B;
            pixels[offset + 3] = (byte)MathF.Round(color.A * coverage);
        }
    }

    internal static void RunChecks(Action<bool, string> require)
    {
        bool allCentersMatch = true, allCentersVisible = true;
        for (int index = 0; index < CalibrationGrid.Count; index++)
        {
            Vector3 point = TargetPoint(index);
            Vector2 angles = CalibrationGrid.TargetAngles(index);
            float yaw = MathF.Atan2(point.X, -point.Z);
            float pitch = MathF.Atan2(point.Y, MathF.Sqrt(point.X * point.X + point.Z * point.Z));
            allCentersMatch &= Math.Abs(yaw - angles.X) < 0.000001f && Math.Abs(pitch - angles.Y) < 0.000001f;
            allCentersVisible &= Math.Abs(point.X) + OutlineOuterRadius < PlaneWidth / 2 &&
                point.Y + OutlineOuterRadius < PlaneTop &&
                point.Y - OutlineOuterRadius > PlaneTop - PlaneHeight;
        }
        require(allCentersMatch, "Calibration target pixels preserve yaw and pitch at the grid corners");
        require(allCentersVisible, "All calibration targets and selection outlines fit the transparent grid");
        require(PlaneDistance == 1f && TargetPoint(CalibrationGrid.CenterIndex) == new Vector3(0, 0, -1) &&
            Math.Abs(PlaneWidth - 1.024f) < 0.000001f && Math.Abs(PlaneHeight - 1.152f) < 0.000001f &&
            Math.Abs(DotRadius - 0.007f) < 0.000001f && Math.Abs(OutlineOuterRadius - 0.014f) < 0.000001f,
            "The calibration plane is one meter away with 7 mm dot radii and 14 mm outline radii");
        Vector3 upperLeft = TargetPoint(0), upperRight = TargetPoint(CalibrationGrid.Columns - 1);
        Vector3 lowerLeft = TargetPoint(CalibrationGrid.Count - CalibrationGrid.Columns);
        require(upperLeft.X < 0 && upperRight.X > 0 && upperLeft.Y > 0 && lowerLeft.Y < 0 &&
            (upperLeft.X / PlaneWidth + 0.5f) * Width < Width / 2f &&
            (upperRight.X / PlaneWidth + 0.5f) * Width > Width / 2f &&
            PixelCenter(0).Y < Height / 2f &&
            PixelCenter(CalibrationGrid.Count - CalibrationGrid.Columns).Y > Height / 2f,
            "Calibration columns increase to the right and rows increase down the unflipped texture");
        bool planePixelsMatch = true;
        for (int index = 0; index < CalibrationGrid.Count; index++)
        {
            Vector2 pixel = PixelCenter(index);
            Vector3 rendered = new((pixel.X / Width - .5f) * PlaneWidth,
                PlaneCenterY + (.5f - pixel.Y / Height) * PlaneHeight, -PlaneDistance);
            planePixelsMatch &= Vector3.Distance(rendered, TargetPoint(index)) < .000001f;
        }
        require(planePixelsMatch,
            "Every calibration target renders at its expected headset coordinates");
        Vector3 eye = new(0.015f, 0.008f, -0.02f);
        Vector3 target = TargetPoint(0);
        require(CalibrationGrid.TryAngles(target - eye, out var expectedAngles) &&
            TryProject(eye, CalibrationGrid.FromAngles(expectedAngles), out var hit) &&
            Vector3.Distance(hit, target) < 0.000001f,
            "Calibration indicator projection matches the target from expected angles and tracked eye origin");
        require(!TryProject(Vector3.Zero, Vector3.UnitZ, out _) &&
            !TryProject(Vector3.Zero, Vector3.UnitY, out _) &&
            !TryProject(Vector3.Zero, new Vector3(float.NaN, 0, -1), out _) &&
            TryProject(Vector3.Zero, Vector3.Normalize(new Vector3(1, 0, -1)), out _),
            "Calibration projection rejects invalid rays and keeps off-grid mismatches visible");
        var saved = new bool[CalibrationGrid.Count];
        var emptyPixels = CreatePixels(CalibrationGrid.CenterIndex, false, saved);
        Vector2 first = PixelCenter(0);
        int firstX = (int)first.X;
        int firstY = (int)first.Y;
        int offset = (firstY * Width + firstX) * 4;
        require(emptyPixels[offset] == EmptyColor.R && emptyPixels[offset + 3] == EmptyColor.A &&
            emptyPixels[3] == 0, "Uncaptured calibration dots are translucent gray on a transparent background");
        saved[0] = true;
        var savedPixels = CreatePixels(0, true, saved);
        require(savedPixels[offset] == SavedColor.R && savedPixels[offset + 1] == SavedColor.G &&
            savedPixels[offset + 3] == SavedColor.A,
            "Saved calibration targets become green while collecting keeps their center visible");
        int outerOffset = ((firstY * Width) + firstX + 12) * 4;
        require(savedPixels[outerOffset] == CollectingColor.R && savedPixels[outerOffset + 1] == CollectingColor.G &&
            savedPixels[outerOffset + 3] > 0, "The selected target has a distinct outline during collection");
        saved[0] = false;
        saved[1] = true;
        DrawInto(savedPixels, 1, false, saved);
        Vector2 second = PixelCenter(1);
        int secondOffset = ((int)second.Y * Width + (int)second.X) * 4;
        int secondOutlineOffset = secondOffset + 12 * 4;
        require(savedPixels[outerOffset] == 0 && savedPixels[outerOffset + 3] == 0 &&
            savedPixels[offset] == EmptyColor.R && savedPixels[offset + 3] == EmptyColor.A &&
            savedPixels[secondOffset] == SavedColor.R && savedPixels[secondOffset + 1] == SavedColor.G &&
            savedPixels[secondOutlineOffset] == SelectedColor.R && savedPixels[secondOutlineOffset + 3] > 0,
            "Redrawing the same calibration buffer removes old outlines and restores gray and green point colors");
        DrawInto(savedPixels, 0, false, saved);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 8; index++) DrawInto(savedPixels, index, false, saved);
        long allocatedDuringRedraw = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        require(allocatedDuringRedraw < savedPixels.Length,
            "Repeated calibration redraws reuse the pixel buffer without allocating an additional image");
    }
}
