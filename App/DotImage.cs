using System.Drawing;
using System.Numerics;
using Valve.VR;

namespace SteamDashET;

internal static class DotImage
{
    internal const int Size = 64;
    internal const int Width = Size;
    internal const int Height = Size;
    internal const float Radius = 0.008f;
    internal const float BorderWidth = 0.002f;
    internal const float PlaneWidth = 0.026f;
    internal const float Alpha = 0.30f;
    internal const float SurfaceOffset = 0.001f;
    internal static readonly Color FillColor = Color.FromArgb(168, 120, 240);
    internal static readonly Color BorderColor = Color.FromArgb(41, 45, 50);

    internal static byte[] CreatePixels()
    {
        var pixels = new byte[Width * Height * 4];
        float fillRadius = Radius - BorderWidth;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float localX = ((x + 0.5f) / Width - 0.5f) * PlaneWidth;
            float localY = (0.5f - (y + 0.5f) / Height) * PlaneWidth;
            float distanceSquared = localX * localX + localY * localY;
            if (distanceSquared > Radius * Radius) continue;
            Color color = distanceSquared > fillRadius * fillRadius ? BorderColor : FillColor;

            int index = (y * Width + x) * 4;
            pixels[index] = color.R;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.B;
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    internal static bool TrySurfaceFrame(Vector3 center, Vector3 normal, out HmdMatrix34_t frame)
    {
        frame = default;
        if (!GazeInput.Finite(center) || !GazeInput.Finite(normal)) return false;

        // Scale first so finite normals can be normalized without overflow or underflow.
        float scale = Math.Max(Math.Abs(normal.X), Math.Max(Math.Abs(normal.Y), Math.Abs(normal.Z)));
        if (scale == 0) return false;
        normal = Vector3.Normalize(normal / scale);
        center += normal * SurfaceOffset;
        Vector3 seed = Math.Abs(normal.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 right = Vector3.Normalize(Vector3.Cross(seed, normal));
        Vector3 up = Vector3.Cross(normal, right);
        frame = new HmdMatrix34_t
        {
            m0 = right.X, m1 = up.X, m2 = normal.X, m3 = center.X,
            m4 = right.Y, m5 = up.Y, m6 = normal.Y, m7 = center.Y,
            m8 = right.Z, m9 = up.Z, m10 = normal.Z, m11 = center.Z
        };
        return GazeInput.Finite(frame);
    }
}
