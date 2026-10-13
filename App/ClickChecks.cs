using System.Numerics;
using System.Runtime.InteropServices;
using Valve.VR;

namespace SteamDashET;

internal static class ClickChecks
{
    private static readonly PanelHit Hit = new("system.desktop.1", 41, default, Vector3.UnitZ, 1,
        new Vector2(0.25f, 0.75f));

    internal static void Run()
    {
        DispatchChecks();
        IndependentEdgesChecks();
        RecoveryChecks();
        InvalidTargetChecks();
        PostingFailureChecks();
    }

    private static void DispatchChecks()
    {
        using var native = new FakeInput();
        var clicks = new GazeClick(native.Overlay, native.View);
        clicks.Update(Frame(), true, true, Hit);
        clicks.Update(Frame(leftA: true), true, true, Hit);
        Require(ClickMatches(native.Events, EVRMouseButton.Left),
            "A posts move, left down, and left up at bottom-left panel coordinates");
        clicks.Update(Frame(leftA: true), true, true, Hit with { Handle = 42 });
        clicks.Update(Frame(), true, true, Hit);
        Require(native.Events.Count == 3, "Holding and releasing A cannot repeat or retarget its click");

        native.Events.Clear();
        clicks.Update(Frame(rightB: true), true, true, Hit);
        Require(ClickMatches(native.Events, EVRMouseButton.Right), "B posts a complete right click at the gaze hit");
        clicks.Update(Frame(), true, true, Hit);
        native.Events.Clear();
        clicks.Update(Frame(leftA: true, leftB: true, rightA: true, rightB: true), true, true, Hit);
        Require(native.Events.Count == 12 && Enumerable.Range(0, 4).All(index =>
            ClickMatches(native.Events.Skip(index * 3).Take(3).ToList(),
                index % 2 == 0 ? EVRMouseButton.Left : EVRMouseButton.Right)),
            "Simultaneous A and B presses on both hands each produce their own balanced click");
    }

    private static void IndependentEdgesChecks()
    {
        using var native = new FakeInput();
        var clicks = new GazeClick(native.Overlay, native.View);
        clicks.Update(Frame(), true, true, Hit);
        clicks.Update(Frame(leftA: true), true, true, Hit);
        clicks.Update(Frame(leftA: true, rightA: true), true, true, Hit);
        clicks.Update(Frame(leftA: true, leftB: true, rightA: true), true, true, Hit);
        clicks.Update(Frame(leftA: true, leftB: true, rightA: true, rightB: true), true, true, Hit);
        Require(native.Events.Count == 12, "A held on one hand cannot hide another hand or button's new press");
        clicks.Update(Frame(), true, true, Hit);
        native.Events.Clear();
        clicks.Update(Frame(leftA: true, leftB: true), true, false, default);
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        Require(native.Events.Count == 0, "Both button presses on a miss are consumed rather than clicking a later target");
        clicks.Update(Frame(), true, true, Hit);
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        Require(native.Events.Count == 6, "A release and a new press allow both buttons to click after a miss");

        clicks.Update(Frame(), true, true, Hit);
        native.Events.Clear();
        var failedA = Frame() with { Left = Frame().Left with { A = Button(false, false) } };
        clicks.Update(failedA, true, true, Hit);
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        Require(ClickMatches(native.Events, EVRMouseButton.Right),
            "An unavailable A resets only A while B can still produce a right click");
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        clicks.Update(Frame(leftB: true), true, true, Hit);
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        Require(native.Events.Count == 6, "A recovering held waits for release without repeating the independent held B");
    }

    private static void RecoveryChecks()
    {
        using var native = new FakeInput();
        var clicks = new GazeClick(native.Overlay, native.View);
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        Require(native.Events.Count == 0, "Starting with either button held waits for a valid release");
        clicks.Update(Frame(), true, true, Hit);
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        Require(native.Events.Count == 6, "Fresh presses following the startup release produce left and right clicks");

        clicks.Update(Frame(), true, true, Hit);
        clicks.Update(Frame(leftA: true, leftB: true), false, true, Hit);
        native.Events.Clear();
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        Require(native.Events.Count == 0, "Reopening the dashboard with held buttons cannot click");
        clicks.Update(Frame(), true, true, Hit);
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        Require(native.Events.Count == 6, "Dashboard reopen accepts new presses only after release");

        clicks.Reset();
        native.Events.Clear();
        clicks.Update(Frame(leftA: true), true, true, Hit);
        Require(native.Events.Count == 0, "Explicit reset discards button baselines without generating a held click");

        var original = Frame().Left;
        foreach (var changed in new[]
        {
            original.Identity with { DeviceIndex = 3 }, original.Identity with { ControllerType = "other" },
            original.Identity with { RenderModel = "new-model" }, original.Identity with { SerialNumber = "replacement" },
            original.Identity with { InputSourceHandle = 99 }, original.Identity with { ConnectionGeneration = 2 }
        })
        {
            clicks.Reset();
            clicks.Update(Frame(), true, true, Hit);
            var swapped = Frame(leftA: true, leftB: true) with
            {
                Left = new ControllerHandButtons(changed, Button(true), Button(true))
            };
            native.Events.Clear();
            clicks.Update(swapped, true, true, Hit);
            Require(native.Events.Count == 0, "A controller identity change cannot synthesize presses: " + changed);
            clicks.Update(swapped with { Left = swapped.Left with { A = Button(false), B = Button(false) } },
                true, true, Hit);
            clicks.Update(swapped, true, true, Hit);
            Require(native.Events.Count == 6, "The changed controller resumes after both buttons release");
        }
    }

    private static void InvalidTargetChecks()
    {
        using var native = new FakeInput();
        var clicks = new GazeClick(native.Overlay, native.View);
        foreach (var invalidUV in new[]
        {
            new Vector2(-0.1f, 0.5f), new Vector2(0.5f, 1.1f), new Vector2(float.NaN, 0),
            new Vector2(0, float.PositiveInfinity)
        })
        {
            clicks.Update(Frame(), true, true, Hit);
            clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit with { UV = invalidUV });
        }
        clicks.Update(Frame(), true, true, Hit);
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit with { Handle = 0 });
        Require(native.Events.Count == 0, "Invalid UVs and missing overlay handles reject both mouse buttons");

        foreach (var scale in new[]
        {
            new HmdVector2_t { v0 = 0, v1 = 1080 }, new HmdVector2_t { v0 = -1, v1 = 1080 },
            new HmdVector2_t { v0 = 1920, v1 = float.NaN },
            new HmdVector2_t { v0 = float.PositiveInfinity, v1 = 1080 }
        })
        {
            native.Scale = scale;
            clicks.Update(Frame(), true, true, Hit);
            clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        }
        native.Scale = new HmdVector2_t { v0 = 1920, v1 = 1080 };
        native.ScaleError = EVROverlayError.UnknownOverlay;
        clicks.Update(Frame(), true, true, Hit);
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        Require(native.Events.Count == 0, "Invalid mouse scales and disappearing overlays generate no input");
        native.ScaleError = EVROverlayError.None;
        clicks.Update(Frame(leftA: true, leftB: true), true, true, Hit);
        Require(native.Events.Count == 0, "Failed-target presses remain consumed after the overlay recovers");
    }

    private static void PostingFailureChecks()
    {
        using var native = new FakeInput();
        var clicks = new GazeClick(native.Overlay, native.View);
        clicks.Update(Frame(), true, true, Hit);
        native.ThrowOn = EVREventType.VREvent_MouseButtonDown;
        bool failed = false;
        try { clicks.Update(Frame(rightB: true), true, true, Hit); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed && ClickMatches(native.Events, EVRMouseButton.Right),
            "Right-button down failure still attempts matching right-button up");
        native.ThrowOn = null;
        clicks.Update(Frame(rightB: true), true, true, Hit);
        Require(native.Events.Count == 3, "An interrupted click does not repeat while its controller button remains held");

        clicks.Update(Frame(), true, true, Hit);
        native.Events.Clear();
        native.ThrowOn = EVREventType.VREvent_MouseButtonUp;
        failed = false;
        try { clicks.Update(Frame(rightB: true), true, true, Hit); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed && ClickMatches(native.Events, EVRMouseButton.Right),
            "A right-button up failure is surfaced");

        clicks.Update(Frame(), true, true, Hit);
        native.Events.Clear();
        native.ThrowOn = EVREventType.VREvent_MouseMove;
        failed = false;
        try { clicks.Update(Frame(leftA: true), true, true, Hit); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed && native.Events.Count == 1,
            "A mouse move failure never attempts a mouse down");
    }

    private static ControllerButtonFrame Frame(bool leftA = false, bool leftB = false,
        bool rightA = false, bool rightB = false) => new(
        Hand(ETrackedControllerRole.LeftHand, leftA, leftB), Hand(ETrackedControllerRole.RightHand, rightA, rightB));

    private static ControllerHandButtons Hand(ETrackedControllerRole role, bool a, bool b) => new(new(role,
        role == ETrackedControllerRole.LeftHand ? 1u : 2u, "knuckles", "knuckles_" + role, "serial_" + role,
        role == ETrackedControllerRole.LeftHand ? 11ul : 12ul, 1), Button(a), Button(b));

    private static ControllerButtonState Button(bool pressed, bool available = true) => new(available, pressed);

    private static bool ClickMatches(List<(ulong Handle, VREvent_t Event)> events, EVRMouseButton button) =>
        events.Count == 3 && events.Select(item => item.Event.eventType).SequenceEqual(new uint[]
        {
            (uint)EVREventType.VREvent_MouseMove, (uint)EVREventType.VREvent_MouseButtonDown,
            (uint)EVREventType.VREvent_MouseButtonUp
        }) && events.All(item => item.Handle == 41 && item.Event.data.mouse.x == 480 &&
            item.Event.data.mouse.y == 810 && item.Event.trackedDeviceIndex == OpenVR.k_unTrackedDeviceIndexInvalid) &&
        events[0].Event.data.mouse.button == 0 &&
        events.Skip(1).All(item => item.Event.data.mouse.button == (uint)button);

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class FakeInput : IDisposable
    {
        private readonly IVROverlay overlayTable;
        private readonly IVROverlayView viewTable;
        private readonly IntPtr overlayPointer;
        private readonly IntPtr viewPointer;
        internal readonly List<(ulong Handle, VREvent_t Event)> Events = new();
        internal HmdVector2_t Scale = new() { v0 = 1920, v1 = 1080 };
        internal EVROverlayError ScaleError;
        internal EVREventType? ThrowOn;
        internal CVROverlay Overlay { get; }
        internal CVROverlayView View { get; }

        internal FakeInput()
        {
            overlayTable = new IVROverlay { GetOverlayMouseScale = GetScale };
            viewTable = new IVROverlayView { PostOverlayEvent = Post };
            overlayPointer = Marshal.AllocHGlobal(Marshal.SizeOf<IVROverlay>());
            viewPointer = Marshal.AllocHGlobal(Marshal.SizeOf<IVROverlayView>());
            Marshal.StructureToPtr(overlayTable, overlayPointer, false);
            Marshal.StructureToPtr(viewTable, viewPointer, false);
            Overlay = new CVROverlay(overlayPointer);
            View = new CVROverlayView(viewPointer);
        }

        private EVROverlayError GetScale(ulong handle, ref HmdVector2_t scale)
        {
            scale = Scale;
            return ScaleError;
        }

        private void Post(ulong handle, ref VREvent_t value)
        {
            Events.Add((handle, value));
            if (ThrowOn == (EVREventType)value.eventType)
                throw new InvalidOperationException("Simulated event posting failure");
        }

        public void Dispose()
        {
            Marshal.DestroyStructure<IVROverlay>(overlayPointer);
            Marshal.DestroyStructure<IVROverlayView>(viewPointer);
            Marshal.FreeHGlobal(overlayPointer);
            Marshal.FreeHGlobal(viewPointer);
            GC.KeepAlive(overlayTable);
            GC.KeepAlive(viewTable);
        }
    }
}
