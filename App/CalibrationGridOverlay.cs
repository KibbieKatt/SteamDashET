using System.Runtime.InteropServices;
using Valve.VR;

namespace SteamDashET;

internal sealed class CalibrationGridOverlay : IDisposable
{
    internal const float GridPlaneDistance = CalibrationGridImage.PlaneDistance;
    private readonly CVROverlay api;
    private byte[] pixels;
    private GCHandle pixelPin;
    private ulong handle;
    private bool shown;
    private bool loading;
    private int previousSelected = -1;
    private bool previousCollecting;
    private readonly bool[] previousSaved = new bool[CalibrationGrid.Count];

    internal bool Visible => shown && handle != 0 && api.IsOverlayVisible(handle);

    internal CalibrationGridOverlay(CVROverlay api)
    {
        this.api = api;
        try
        {
            Check(api.CreateOverlay($"steamdash.et.{Environment.ProcessId}.calibration",
                "SteamDashET calibration targets", ref handle));
            Check(api.SetOverlayInputMethod(handle, VROverlayInputMethod.None));
            Check(api.SetOverlayFlag(handle, VROverlayFlags.VisibleInDashboard, true));
            Check(api.SetOverlayFlag(handle, VROverlayFlags.NoBackside, true));
            Check(api.SetOverlaySortOrder(handle, uint.MaxValue - 1));
            Check(api.SetOverlayWidthInMeters(handle, CalibrationGridImage.PlaneWidth));
            var frame = new HmdMatrix34_t
            {
                m0 = 1, m5 = 1, m10 = 1,
                m7 = CalibrationGridImage.PlaneCenterY, m11 = -GridPlaneDistance
            };
            Check(api.SetOverlayTransformTrackedDeviceRelative(handle, OpenVR.k_unTrackedDeviceIndex_Hmd, ref frame));
            pixels = new byte[CalibrationGridImage.Width * CalibrationGridImage.Height * 4];
            pixelPin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            Update(CalibrationGrid.CenterIndex, false, new bool[CalibrationGrid.Count]);
        }
        catch { Dispose(); throw; }
    }

    internal void Update(int selected, bool collecting, IReadOnlyList<bool> savedPoints)
    {
        ArgumentNullException.ThrowIfNull(savedPoints);
        if (savedPoints.Count != CalibrationGrid.Count)
            throw new ArgumentException("One saved point status is required for each calibration target.", nameof(savedPoints));
        if (selected < 0 || selected >= CalibrationGrid.Count) throw new ArgumentOutOfRangeException(nameof(selected));
        VREvent_t ev = default;
        for (int count = 0; count < 64 && api.PollNextOverlayEvent(handle, ref ev, (uint)Marshal.SizeOf<VREvent_t>()); count++)
        {
            if ((EVREventType)ev.eventType == EVREventType.VREvent_ImageLoaded) loading = false;
            if ((EVREventType)ev.eventType == EVREventType.VREvent_ImageFailed)
                throw new InvalidOperationException("SteamVR failed to load the calibration targets.");
        }
        if (loading) return;
        bool changed = selected != previousSelected || collecting != previousCollecting;
        for (int index = 0; !changed && index < savedPoints.Count; index++)
            changed = savedPoints[index] != previousSaved[index];
        if (!changed) return;
        // Raw upload publishes each changed image instead of reusing a shared GPU texture that
        // SteamVR can keep cached. Keep this pinned buffer immutable until ImageLoaded arrives.
        CalibrationGridImage.DrawInto(pixels, selected, collecting, savedPoints);
        Check(api.SetOverlayRaw(handle, pixelPin.AddrOfPinnedObject(), CalibrationGridImage.Width, CalibrationGridImage.Height, 4));
        loading = true;
        previousSelected = selected;
        previousCollecting = collecting;
        for (int index = 0; index < savedPoints.Count; index++)
            previousSaved[index] = savedPoints[index];
    }

    internal void Show(bool visible)
    {
        if (shown == visible) return;
        Check(visible ? api.ShowOverlay(handle) : api.HideOverlay(handle));
        shown = visible;
    }

    private static void Check(EVROverlayError error)
    {
        if (error != EVROverlayError.None)
            throw new InvalidOperationException("Calibration overlay: " + error);
    }

    public void Dispose()
    {
        try
        {
            if (handle != 0)
            {
                api.HideOverlay(handle);
                api.ClearOverlayTexture(handle);
                api.DestroyOverlay(handle);
                handle = 0;
                shown = false;
            }
        }
        finally
        {
            if (pixelPin.IsAllocated) pixelPin.Free();
            pixels = null;
        }
    }

    internal static void RunChecks(Action<bool, string> require)
    {
        using var native = new FakeOverlay();
        var grid = new CalibrationGridOverlay(native.Api);
        var savedPoints = new bool[CalibrationGrid.Count];
        require(native.Images.Count == 1 && native.Images[0].AsSpan().SequenceEqual(
            CalibrationGridImage.CreatePixels(CalibrationGrid.CenterIndex, false, savedPoints)),
            "Calibration creates a raw gray grid without a DirectX renderer");
        byte[] initial = native.Images[0];
        grid.Update(7, true, savedPoints);
        grid.Update(2, true, savedPoints);
        var pending = new byte[initial.Length];
        Marshal.Copy(native.PixelAddress, pending, 0, pending.Length);
        require(native.Images.Count == 1 && pending.AsSpan().SequenceEqual(initial),
            "In-flight calibration image bytes stay pinned and immutable across selection changes");
        savedPoints[7] = true;
        native.Events.Enqueue(EVREventType.VREvent_ImageLoaded);
        grid.Update(1, false, savedPoints);
        require(native.Images.Count == 2 && native.Images[^1].AsSpan().SequenceEqual(
            CalibrationGridImage.CreatePixels(1, false, savedPoints)),
            "Completion publishes the latest selected, collecting and saved target state");
        native.Events.Enqueue(EVREventType.VREvent_ImageLoaded);
        grid.Update(1, false, savedPoints);
        grid.Update(1, false, savedPoints);
        require(native.Images.Count == 2, "Unchanged calibration state does not restart raw image loading");
        savedPoints[^1] = true;
        grid.Update(1, false, savedPoints);
        require(native.Images.Count == 3 && native.Images[^1].AsSpan().SequenceEqual(
            CalibrationGridImage.CreatePixels(1, false, savedPoints)),
            "Saved status changes on the last calibration row publish a fresh raw image");
        bool PublishSaved(int index, bool value)
        {
            int previousCount = native.Images.Count;
            savedPoints[index] = value;
            native.Events.Enqueue(EVREventType.VREvent_ImageLoaded);
            grid.Update(1, false, savedPoints);
            return native.Images.Count == previousCount + 1 && native.Images[^1].AsSpan().SequenceEqual(
                CalibrationGridImage.CreatePixels(1, false, savedPoints));
        }
        require(PublishSaved(0, true) && PublishSaved(32, true) && PublishSaved(64, true),
            "Saved targets beyond indices 31 and 63 publish independently of colliding low target indices");
        require(PublishSaved(0, false) && PublishSaved(32, false),
            "Clearing targets 0 and 32 keeps target 64 saved and publishes each independent color change");
        int uploadsBeforeFailure = native.Images.Count;
        savedPoints[65] = true;
        native.Events.Enqueue(EVREventType.VREvent_ImageLoaded);
        native.UploadError = EVROverlayError.RequestFailed;
        bool rejectedChange = false;
        try { grid.Update(1, false, savedPoints); }
        catch (InvalidOperationException) { rejectedChange = true; }
        native.UploadError = EVROverlayError.None;
        grid.Update(1, false, savedPoints);
        require(rejectedChange && native.Images.Count == uploadsBeforeFailure + 2 &&
            native.Images[^1].AsSpan().SequenceEqual(CalibrationGridImage.CreatePixels(1, false, savedPoints)),
            "A rejected changed upload does not cache saved colors or suppress a later successful retry");
        grid.Show(true);
        require(grid.Visible, "Calibration grid visibility remains independent of raw upload state");
        native.Events.Enqueue(EVREventType.VREvent_ImageFailed);
        bool failed = false;
        try { grid.Update(1, false, savedPoints); }
        catch (InvalidOperationException) { failed = true; }
        require(failed, "SteamVR image-load failures are reported instead of silently keeping stale targets");
        grid.Dispose();
        grid.Dispose();
        require(native.Destroyed == 1 && !grid.pixelPin.IsAllocated,
            "Calibration destroys its overlay before freeing the single pinned image buffer");
        using var broken = new FakeOverlay { UploadError = EVROverlayError.RequestFailed };
        bool rejected = false;
        try { _ = new CalibrationGridOverlay(broken.Api); }
        catch (InvalidOperationException) { rejected = true; }
        require(rejected && broken.Destroyed == 1, "A rejected raw upload cleans up the partial calibration overlay");
    }

    private sealed class FakeOverlay : IDisposable
    {
        private readonly IVROverlay table;
        private readonly nint pointer;
        private bool visible;
        internal readonly List<byte[]> Images = new();
        internal readonly Queue<EVREventType> Events = new();
        internal EVROverlayError UploadError;
        internal nint PixelAddress;
        internal int Destroyed;
        internal CVROverlay Api { get; }

        internal FakeOverlay()
        {
            table = new IVROverlay
            {
                CreateOverlay = Create,
                SetOverlayInputMethod = (_, _) => EVROverlayError.None,
                SetOverlayFlag = (_, _, _) => EVROverlayError.None,
                SetOverlaySortOrder = (_, _) => EVROverlayError.None,
                SetOverlayWidthInMeters = (_, _) => EVROverlayError.None,
                SetOverlayTransformTrackedDeviceRelative = Transform,
                SetOverlayRaw = Upload,
                PollNextOverlayEvent = Poll,
                ShowOverlay = _ => { visible = true; return EVROverlayError.None; },
                HideOverlay = _ => { visible = false; return EVROverlayError.None; },
                IsOverlayVisible = _ => visible,
                ClearOverlayTexture = _ => EVROverlayError.None,
                DestroyOverlay = _ => { Destroyed++; return EVROverlayError.None; }
            };
            pointer = Marshal.AllocHGlobal(Marshal.SizeOf<IVROverlay>());
            Marshal.StructureToPtr(table, pointer, false);
            Api = new CVROverlay(pointer);
        }

        private static EVROverlayError Create(nint key, nint name, ref ulong handle)
        {
            handle = 10;
            return EVROverlayError.None;
        }

        private static EVROverlayError Transform(ulong handle, uint device, ref HmdMatrix34_t frame) => EVROverlayError.None;

        private EVROverlayError Upload(ulong handle, nint buffer, uint width, uint height, uint depth)
        {
            PixelAddress = buffer;
            var image = new byte[checked((int)(width * height * depth))];
            Marshal.Copy(buffer, image, 0, image.Length);
            Images.Add(image);
            return UploadError;
        }

        private bool Poll(ulong handle, ref VREvent_t value, uint size)
        {
            if (!Events.TryDequeue(out var ev)) return false;
            value = new VREvent_t { eventType = (uint)ev };
            return true;
        }

        public void Dispose()
        {
            Marshal.DestroyStructure<IVROverlay>(pointer);
            Marshal.FreeHGlobal(pointer);
            GC.KeepAlive(table);
        }
    }
}
