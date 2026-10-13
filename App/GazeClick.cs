#nullable enable
using Valve.VR;

namespace SteamDashET;

// Each controller button produces a complete click; no mouse button remains held between frames.
internal sealed class GazeClick(CVROverlay overlay, CVROverlayView view)
{
    private readonly HandEdges left = new();
    private readonly HandEdges right = new();

    internal void Reset()
    {
        left.Reset();
        right.Reset();
    }

    internal void Update(ControllerButtonFrame state, bool dashboardOpen,
        bool hasTarget, PanelHit hit)
    {
        if (!dashboardOpen)
        {
            Reset();
            return;
        }
        ProcessHand(state.Left, left);
        ProcessHand(state.Right, right);

        void ProcessHand(ControllerHandButtons hand, HandEdges edges)
        {
            var identity = hand.Identity;
            if (edges.Identity != identity)
            {
                edges.Reset();
                edges.Identity = identity;
            }
            ProcessButton(hand.A, edges.A, EVRMouseButton.Left);
            ProcessButton(hand.B, edges.B, EVRMouseButton.Right);
        }

        void ProcessButton(ControllerButtonState buttonState, ButtonEdge edge, EVRMouseButton mouseButton)
        {
            if (!buttonState.Available)
            {
                edge.Reset();
                return;
            }
            // Startup, recovery, and controller changes must observe a release before accepting a press.
            if (!edge.Primed)
            {
                edge.Primed = !buttonState.Pressed;
                return;
            }
            bool pressed = buttonState.Pressed && !edge.WasDown;
            edge.WasDown = buttonState.Pressed;
            if (!pressed) return;
            // Consume a press on a miss so moving onto a panel while held cannot cause a delayed click.
            if (!hasTarget || hit.Handle == 0) return;
            if (!float.IsFinite(hit.UV.X) || !float.IsFinite(hit.UV.Y) ||
                hit.UV.X < 0 || hit.UV.X > 1 || hit.UV.Y < 0 || hit.UV.Y > 1)
                return;
            HmdVector2_t scale = default;
            var error = overlay.GetOverlayMouseScale(hit.Handle, ref scale);
            if (error != EVROverlayError.None || !float.IsFinite(scale.v0) || !float.IsFinite(scale.v1) ||
                scale.v0 <= 0 || scale.v1 <= 0)
                return;
            // Native panel UVs and OpenVR mouse events both use bottom-left coordinates.
            float x = hit.UV.X * scale.v0;
            float y = hit.UV.Y * scale.v1;
            Post(EVREventType.VREvent_MouseMove, 0);
            try { Post(EVREventType.VREvent_MouseButtonDown, (uint)mouseButton); }
            finally { Post(EVREventType.VREvent_MouseButtonUp, (uint)mouseButton); }

            void Post(EVREventType type, uint nativeButton)
            {
                var value = new VREvent_t
                {
                    eventType = (uint)type,
                    trackedDeviceIndex = OpenVR.k_unTrackedDeviceIndexInvalid,
                    data = new VREvent_Data_t
                    {
                        mouse = new VREvent_Mouse_t { x = x, y = y, button = nativeButton }
                    }
                };
                view.PostOverlayEvent(hit.Handle, ref value);
            }
        }
    }

    private sealed class HandEdges
    {
        internal ControllerHandIdentity? Identity;
        internal readonly ButtonEdge A = new();
        internal readonly ButtonEdge B = new();
        internal void Reset()
        {
            Identity = null;
            A.Reset();
            B.Reset();
        }
    }

    private sealed class ButtonEdge
    {
        internal bool Primed;
        internal bool WasDown;
        internal void Reset() => (Primed, WasDown) = (false, false);
    }
}
