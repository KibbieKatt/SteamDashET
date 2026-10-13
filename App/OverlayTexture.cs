using Valve.VR;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SteamDashET;

// Created, updated, and disposed by the OpenVR worker that submits the texture.
internal sealed class OverlayTexture : IDisposable
{
    private readonly int rowPitch;
    private readonly int byteCount;
    private ID3D11Device device;
    private ID3D11DeviceContext context;
    private ID3D11Texture2D texture;
    private bool disposed;

    public OverlayTexture(CVRSystem system, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        rowPitch = checked(width * 4);
        byteCount = checked(rowPitch * height);

        try
        {
            int adapterIndex = -1;
            system.GetDXGIOutputInfo(ref adapterIndex);
            if (adapterIndex < 0)
                throw new InvalidOperationException("SteamVR did not identify its graphics adapter.");

            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1((uint)adapterIndex, out var adapter).CheckError();
            using (adapter)
            {
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 },
                    out device, out context).CheckError();
            }

            texture = device.CreateTexture2D(new Texture2DDescription(
                Format.R8G8B8A8_UNorm, (uint)width, (uint)height,
                arraySize: 1, mipLevels: 1,
                bindFlags: BindFlags.ShaderResource | BindFlags.RenderTarget,
                usage: ResourceUsage.Default, cpuAccessFlags: CpuAccessFlags.None,
                miscFlags: ResourceOptionFlags.Shared));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IntPtr Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return texture.NativePointer;
        }
    }

    public void Update(ReadOnlySpan<byte> pixels)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pixels.Length != byteCount)
            throw new ArgumentException($"Expected {byteCount} RGBA bytes.", nameof(pixels));

        context.UpdateSubresource<byte>(pixels, texture, 0, (uint)rowPitch);
        context.Flush();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        texture?.Dispose();
        context?.Dispose();
        device?.Dispose();
    }
}
