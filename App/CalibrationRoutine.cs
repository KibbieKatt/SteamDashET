using System.Numerics;

namespace SteamDashET;

// Button levels have the same focus-independent source as dashboard clicking.
internal sealed class CalibrationRoutine(CalibrationService service)
{
    private readonly Hand left = new(), right = new();
    private readonly List<CalibrationSample> pending = new();
    private Hand capture;
    private long runId = -1;
    private int target;
    private double sampleOrigin;
    private bool sampling;

    internal void Update(ControllerButtonFrame buttons, bool gazeValid, Vector3 headOrigin,
        Vector3 rawHeadDirection, Vector3? gazePoint, double now)
    {
        var state = service.Snapshot;
        if (!state.Active) { Reset(); return; }
        if (runId != state.RunId)
        {
            Reset();
            runId = state.RunId;
            target = state.Selected;
        }
        bool wasCapturing = capture != null;
        bool leftChanged = left.Update(buttons.Left, now, target, capture != null);
        bool rightChanged = right.Update(buttons.Right, now, target, capture != null);
        if (capture != null && ((capture == left && leftChanged) || (capture == right && rightChanged) ||
            !capture.A.Available))
        {
            capture = null;
            pending.Clear();
            sampling = false;
            service.SetLive(runId, target, false, 0, "Controller input lost; unfinished samples discarded.");
        }
        // Defer clearing until release so a long B hold exits without clearing a target first.
        foreach (Hand hand in new[] { left, right })
        {
            if (hand.BLongReleased || (hand.B.Available && hand.B.Pressed && hand.BStarted.HasValue && now - hand.BStarted.Value >= 1))
            {
                service.Finish();
                Reset();
                return;
            }
            if (hand.BReleased && !hand.SuppressClear && capture == null)
                service.ClearTarget(runId, hand.BTarget);
        }

        if (capture != null && capture.AReleased)
        {
            if (pending.Count > 0)
            {
                // Close the last known sample's time interval; a tracking gap carries no dwell weight.
                double end = now - sampleOrigin;
                if (sampling && gazeValid && end > pending[^1].TimeSeconds && end - pending[^1].TimeSeconds < .2)
                    pending.Add(pending[^1] with { TimeSeconds = end });
                service.SaveCapture(runId, target, pending);
            }
            else service.SetLive(runId, target, false, 0, "No valid gaze samples received while A was held.");
            capture = null;
            pending.Clear();
            sampling = false;
        }
        bool bHeld = left.B.Pressed || right.B.Pressed;
        if (capture == null && !bHeld)
        {
            if (left.AStarted) capture = left;
            else if (right.AStarted) capture = right;
            if (capture != null)
            {
                pending.Clear();
                sampleOrigin = double.NaN;
                sampling = false;
                service.SetLive(runId, target, true, 0, "Hold your gaze on the selected target; release A to save.");
            }
        }
        // Keep the highlighted target fixed through button press/release frames as well as
        // the hold. B uses the same lock so clearing applies to the visibly selected target.
        if (!wasCapturing && capture == null && !left.A.Pressed && !right.A.Pressed && !bHeld &&
            !left.AReleased && !right.AReleased && !left.BReleased && !right.BReleased &&
            gazeValid && gazePoint.HasValue && GazeInput.Finite(gazePoint.Value))
        {
            float nearest = Vector3.DistanceSquared(gazePoint.Value, CalibrationGridImage.TargetPoint(target));
            for (int index = 0; index < CalibrationGrid.Count; index++)
            {
                float distance = Vector3.DistanceSquared(gazePoint.Value, CalibrationGridImage.TargetPoint(index));
                if (distance < nearest) { nearest = distance; target = index; }
            }
        }
        if (capture != null && capture.A.Pressed && gazeValid &&
            CalibrationGrid.TryAngles(rawHeadDirection, out var measured) &&
            CalibrationGrid.TryAngles(CalibrationGridImage.TargetPoint(target) - headOrigin, out var expected))
        {
            // Restart time after a tracking gap so it never contributes to the saved dwell average.
            if (!sampling) sampleOrigin = now;
            pending.Add(new CalibrationSample(now - sampleOrigin, measured.X, measured.Y, expected.X, expected.Y));
            sampling = true;
            if (pending.Count > CalibrationAveraging.MaximumSamples)
                pending.RemoveAt(0);
        }
        else sampling = false;
        string hint = !left.A.Available && !right.A.Available
            ? "Waiting for a supported controller. You can finish from the control window." :
            capture != null ? sampling ? "Hold your gaze on the selected target; release A to save." :
                "Waiting for valid gaze; sampling is paused." :
            service.Snapshot.Hint.StartsWith("Waiting for", StringComparison.Ordinal) ? "Look at a target to select it." : null;
        service.SetLive(runId, target, capture != null, pending.Count, hint);
    }

    internal void Reset()
    {
        left.Reset(); right.Reset();
        capture = null;
        pending.Clear();
        sampling = false;
        runId = -1;
    }

    private sealed class Hand
    {
        private ControllerHandIdentity? identity;
        private bool aPrimed, bPrimed, aDown, bDown;
        internal ControllerButtonState A, B;
        internal bool AStarted, AReleased, BReleased, BLongReleased, SuppressClear;
        internal double? BStarted;
        internal int BTarget;

        internal bool Update(ControllerHandButtons hand, double now, int selected, bool capturing)
        {
            bool changed = identity != hand.Identity;
            if (changed) { Reset(); identity = hand.Identity; }
            A = hand.A; B = hand.B;
            AStarted = AReleased = BReleased = BLongReleased = false;
            if (!A.Available) { aPrimed = aDown = false; }
            else if (!aPrimed) aPrimed = !A.Pressed;
            else
            {
                AStarted = A.Pressed && !aDown;
                AReleased = !A.Pressed && aDown;
                aDown = A.Pressed;
            }
            if (!B.Available) { bPrimed = bDown = false; BStarted = null; }
            else if (!bPrimed) bPrimed = !B.Pressed;
            else
            {
                if (B.Pressed && !bDown)
                {
                    BStarted = now;
                    BTarget = selected;
                    SuppressClear = capturing || A.Pressed;
                }
                BReleased = !B.Pressed && bDown;
                if (BReleased)
                {
                    BLongReleased = BStarted.HasValue && now - BStarted.Value >= 1;
                    BStarted = null;
                }
                bDown = B.Pressed;
            }
            return changed;
        }

        internal void Reset()
        {
            identity = null;
            aPrimed = bPrimed = aDown = bDown = false;
            A = B = default;
            AStarted = AReleased = BReleased = BLongReleased = SuppressClear = false;
            BStarted = null;
        }
    }
}
