using System.Numerics;
using Valve.VR;

namespace SteamDashET;

internal static class CalibrationRoutineChecks
{
    private static readonly Vector3 Eye = new(.012f, .008f, -.025f);
    private static readonly Vector2 Offset = new(.02f, -.01f);

    internal static void Run()
    {
        SamplingChecks();
        ClearingAndExitChecks();
        RecoveryChecks();
        SelectionChecks();
        SpaceChecks();
    }

    private static void SamplingChecks()
    {
        var saves = new List<CalibrationProfile>();
        var service = new CalibrationService(CalibrationProfile.Empty("head"), saves.Add);
        var routine = new CalibrationRoutine(service);
        service.Begin();
        Tick(routine, 0, Frame(a: true));
        Require(!service.Snapshot.Collecting, "Calibration ignores A already held when the grid opens");
        Tick(routine, .01);
        Tick(routine, .02, Frame(a: true));
        Tick(routine, .04, Frame(a: true), gazeTarget: 0);
        Require(service.Snapshot.Collecting && service.Snapshot.PendingSamples == 2 &&
            service.Snapshot.Selected == CalibrationGrid.CenterIndex && service.Snapshot.PopulatedTargets == 0,
            "Holding A collects raw samples and locks the target without applying unfinished data");
        Tick(routine, .06);
        var state = service.Snapshot;
        Vector3 raw = RawDirection(CalibrationGrid.CenterIndex);
        Vector3 expected = Vector3.Normalize(CalibrationGridImage.TargetPoint(CalibrationGrid.CenterIndex) - Eye);
        Require(state.PopulatedTargets == 1 && !state.Collecting && saves.Count == 1 &&
            Vector3.Distance(service.Correct(raw), expected) < .00001f,
            "Releasing A saves one average and immediately corrects the preview from the tracked eye origin");
        CalibrationGrid.TryAngles(raw, out var firstAngles);
        Require(saves[0].Targets.Count(point => point.HasValue) == 1 &&
            saves[0].Targets[CalibrationGrid.CenterIndex].Value.Yaw == firstAngles.X,
            "The saved profile retains the capture mean rather than its individual readings or timestamps");
        long revision = state.Revision;
        Vector3 replacementRaw = CalibrationGrid.FromAngles(firstAngles + new Vector2(.01f, 0));
        Tick(routine, .07, Frame(rightA: true), rawDirection: replacementRaw);
        Tick(routine, .09, rawDirection: replacementRaw);
        Require(service.Snapshot.PopulatedTargets == 1 && service.Snapshot.Revision > revision && saves.Count == 2 &&
            saves[1].Targets[CalibrationGrid.CenterIndex] != saves[0].Targets[CalibrationGrid.CenterIndex] &&
            Math.Abs(saves[1].Targets[CalibrationGrid.CenterIndex].Value.Yaw - firstAngles.X - .01f) < .000001f &&
            Vector3.Distance(service.Correct(replacementRaw), expected) < .00001f,
            "A repeated capture on either hand replaces the old average and correction");

        var failing = new CalibrationService(CalibrationProfile.Empty("head"), _ => throw new IOException("test save failure"));
        var failedRoutine = new CalibrationRoutine(failing);
        failing.Begin();
        Tick(failedRoutine, 0);
        Tick(failedRoutine, .01, Frame(a: true));
        Tick(failedRoutine, .03);
        Require(failing.Snapshot.PopulatedTargets == 0 && failing.Snapshot.Error.Contains("test save failure") &&
            failing.Correct(raw) == raw,
            "A failed save retains the previous correction and reports the error instead of applying unsaved data");

        service.Begin();
        Tick(routine, 1);
        Tick(routine, 1.01, Frame(a: true), valid: false);
        Tick(routine, 1.03, valid: false);
        Require(service.Snapshot.PopulatedTargets == 1 && saves.Count == 2 &&
            Vector3.Distance(service.Correct(replacementRaw), expected) < .00001f &&
            service.Snapshot.Hint.Contains("No valid gaze"),
            "An A hold with no valid eye tracking cannot create calibration data");

        saves.Clear();
        service = new CalibrationService(CalibrationProfile.Empty("head"), saves.Add);
        routine = new CalibrationRoutine(service);
        service.Begin();
        Tick(routine, 0);
        Tick(routine, .01, Frame(a: true));
        Tick(routine, .03, Frame(a: true));
        Tick(routine, .04, Frame(a: true), valid: false);
        Vector3 afterGap = CalibrationGrid.FromAngles(firstAngles + new Vector2(.001f, 0));
        Tick(routine, .07, Frame(a: true), rawDirection: afterGap);
        Require(service.Snapshot.Hint.Contains("Hold your gaze"),
            "The sampling hint recovers when valid gaze returns");
        Tick(routine, .09, Frame(a: true, b: true), rawDirection: afterGap);
        Tick(routine, .1, Frame(a: true), valid: false);
        Tick(routine, .11, valid: false);
        var average = saves.Single().Targets[CalibrationGrid.CenterIndex].Value;
        Require(Math.Abs(average.Yaw - firstAngles.X - .0005f) < .000001f && service.Snapshot.PendingSamples == 0,
            "The saved average excludes tracking gaps and invalid release time while B does not pause held-A sampling");
    }

    private static void ClearingAndExitChecks()
    {
        var saves = new List<CalibrationProfile>();
        var service = new CalibrationService(Saved(), saves.Add);
        var routine = new CalibrationRoutine(service);
        service.Begin();
        Tick(routine, 0);
        Tick(routine, .1, Frame(b: true));
        Require(service.Snapshot.PopulatedTargets == 1 && saves.Count == 0,
            "B press defers clearing until its release so a long hold cannot erase data first");
        Tick(routine, .3);
        Require(service.Snapshot.Active && service.Snapshot.PopulatedTargets == 0 && saves.Count == 1,
            "A short B release clears the selected target and keeps calibration open");

        foreach (bool releaseAfterThreshold in new[] { false, true })
        {
            saves.Clear();
            service = new CalibrationService(Saved(), saves.Add);
            routine = new CalibrationRoutine(service);
            service.Begin();
            Tick(routine, 0);
            Tick(routine, .1, Frame(rightB: true));
            Tick(routine, 1.09, Frame(rightB: true));
            Tick(routine, 1.11, releaseAfterThreshold ? Frame() : Frame(rightB: true));
            Require(!service.Snapshot.Active && service.Snapshot.PopulatedTargets == 1 && saves.Count == 0,
                releaseAfterThreshold ? "Releasing B just beyond one second exits without clearing despite no pressed poll crossing the threshold"
                    : "Holding B for one second exits without clearing or resaving existing points");
        }
        service = new CalibrationService(Saved(), saves.Add);
        routine = new CalibrationRoutine(service);
        service.Begin();
        Tick(routine, 0);
        Tick(routine, .1, Frame(a: true));
        Tick(routine, .2, Frame(a: true, b: true));
        Tick(routine, .4, Frame(a: true));
        Require(service.Snapshot.PopulatedTargets == 1, "Tapping B during an A capture cannot erase the saved target point");
    }

    private static void RecoveryChecks()
    {
        var saves = new List<CalibrationProfile>();
        var service = new CalibrationService(CalibrationProfile.Empty("head"), saves.Add);
        var routine = new CalibrationRoutine(service);
        service.Begin();
        Tick(routine, 0);
        Tick(routine, .01, Frame(a: true));
        Tick(routine, .02, Frame(aAvailable: false));
        Require(!service.Snapshot.Collecting && saves.Count == 0,
            "Unavailable capture-owner input discards unfinished samples instead of treating loss as A release");
        Tick(routine, .03, Frame(a: true));
        Require(!service.Snapshot.Collecting, "A recovering while held waits for a real release before collecting again");
        Tick(routine, .04);
        Tick(routine, .05, Frame(a: true));
        Tick(routine, .06, Frame(a: true, generation: 2));
        Require(!service.Snapshot.Collecting && saves.Count == 0,
            "A controller identity change discards the hold and requires release on the new identity");
        Tick(routine, .07, Frame(generation: 2));
        Tick(routine, .08, Frame(a: true, generation: 2));
        service.Finish();
        Tick(routine, .09, Frame(a: true, generation: 2));
        Require(!service.Snapshot.Active && service.Snapshot.PendingSamples == 0 && saves.Count == 0,
            "Finishing from the control window discards an unfinished hold while preserving saved data");
        service.Begin();
        Tick(routine, .1, Frame(a: true, generation: 2));
        Require(!service.Snapshot.Collecting, "Reopening calibration with held A cannot resume a discarded capture");
        service.Clear();
        Require(service.Snapshot.Active, "Global clear does not mutate a calibration being collected");

        var saved = new CalibrationService(Saved(), saves.Add);
        saved.Clear();
        Require(saved.Snapshot.PopulatedTargets == 0 && saved.Correct(RawDirection(CalibrationGrid.CenterIndex)) == RawDirection(CalibrationGrid.CenterIndex),
            "Clear calibration removes saved correction while disconnected too");
    }

    private static void SelectionChecks()
    {
        var saves = new List<CalibrationProfile>();
        var service = new CalibrationService(CalibrationProfile.Empty("head"), saves.Add);
        var routine = new CalibrationRoutine(service);
        service.Begin();
        bool allTargets = true;
        for (int index = 0; index < CalibrationGrid.Count; index++)
        {
            Vector3 expected = Vector3.Normalize(CalibrationGridImage.TargetPoint(index) - Eye);
            CalibrationGridImage.TryProject(Eye, expected, out var projected);
            Tick(routine, index * .01, point: projected);
            allTargets &= service.Snapshot.Selected == index;
        }
        Require(allTargets, "Gaze selects every target using the preview projected from the tracked eye origin");
        int last = service.Snapshot.Selected;
        Tick(routine, 1, valid: false, gazeTarget: 0);
        Tick(routine, 1.01, hasProjection: false, gazeTarget: 0);
        Tick(routine, 1.02, point: new Vector3(float.NaN, 0, -1));
        Require(service.Snapshot.Selected == last,
            "Invalid tracking or an unavailable preview projection keeps the previous selection");
        bool corners = true;
        foreach (var (point, index) in new[] {
            (new Vector3(-3, 3, -1), 0), (new Vector3(3, 3, -1), CalibrationGrid.Columns - 1),
            (new Vector3(-3, -3, -1), CalibrationGrid.Count - CalibrationGrid.Columns),
            (new Vector3(3, -3, -1), CalibrationGrid.Count - 1) })
        {
            Tick(routine, 1.1, point: point);
            corners &= service.Snapshot.Selected == index;
        }
        Require(corners, "Off-grid gaze selects the nearest edge target");
        int center = CalibrationGrid.CenterIndex, next = center + 1;
        Tick(routine, 1.2, gazeTarget: next);
        Vector3 halfway = Vector3.Lerp(CalibrationGridImage.TargetPoint(center), CalibrationGridImage.TargetPoint(next), .5f);
        Tick(routine, 1.21, point: halfway);
        Require(service.Snapshot.Selected == next, "An exact distance tie retains the current highlighted target");

        Tick(routine, 1.3, Frame(a: true), gazeTarget: 0);
        Tick(routine, 1.32, Frame(a: true), gazeTarget: CalibrationGrid.Count - 1);
        Tick(routine, 1.33, Frame(a: true), valid: false);
        Tick(routine, 1.34, Frame(a: true), gazeTarget: 0);
        Require(service.Snapshot.Selected == next && service.Snapshot.Collecting,
            "A press and hold freeze the selected target across gaze movements and tracking gaps");
        Tick(routine, 1.36, gazeTarget: 0);
        CalibrationGrid.TryAngles(RawDirection(center), out var raw);
        var captured = saves.Single().Targets[next].Value;
        Require(service.Snapshot.Selected == next && service.Snapshot.PopulatedTargets == 1 &&
            Math.Abs(captured.Yaw - raw.X) < .000001f && Math.Abs(captured.Pitch - raw.Y) < .000001f,
            "A release saves the raw gaze mean to the locked target even when the preview points somewhere else");
        Tick(routine, 1.37, gazeTarget: 0);
        Require(service.Snapshot.Selected == 0, "Automatic selection resumes on the frame after A release");
        Tick(routine, 1.4, Frame(rightA: true), gazeTarget: next);
        Tick(routine, 1.41, gazeTarget: next);
        Require(service.Snapshot.Selected == 0 && service.Snapshot.HasSavedPoints[0] && service.Snapshot.HasSavedPoints[next],
            "A one-poll hold on the other hand keeps its original target through release");

        service.Begin();
        Tick(routine, 2, Frame(a: true), gazeTarget: 0);
        Tick(routine, 2.01, Frame(a: true), gazeTarget: next);
        Require(service.Snapshot.Selected == center && !service.Snapshot.Collecting,
            "A already held on calibration entry freezes selection without starting a capture");
        Tick(routine, 2.02, gazeTarget: next);
        Require(service.Snapshot.Selected == next, "Releasing an unprimed A restores automatic selection");
        Tick(routine, 2.1, Frame(b: true), gazeTarget: 0);
        Tick(routine, 2.2, Frame(b: true), gazeTarget: 0);
        Tick(routine, 2.3, gazeTarget: 0);
        Require(service.Snapshot.Selected == next && !service.Snapshot.HasSavedPoints[next] && service.Snapshot.HasSavedPoints[0],
            "B clearing keeps the highlighted target fixed through press and release despite gaze movement");
        Tick(routine, 2.31, gazeTarget: 0);
        Require(service.Snapshot.Selected == 0, "Automatic selection resumes after clearing the selected target");
    }

    private static void SpaceChecks()
    {
        var pose = new HmdMatrix34_t { m2 = 1, m5 = 1, m8 = -1, m3 = 1, m7 = 2, m11 = 3 };
        Vector3 direction = CalibrationGrid.FromAngles(new Vector2(.2f, -.1f));
        Vector3 rotated = CalibrationSpace.ToTrackingDirection(direction, pose);
        Require(Vector3.Distance(CalibrationSpace.ToHeadDirection(rotated, pose), direction) < .000001f,
            "Head-relative calibration rotates back into Standing space without reversing correction axes");
        var absoluteEye = new Vector3(1 + Eye.Z, 2 + Eye.Y, 3 - Eye.X);
        Require(Vector3.Distance(CalibrationSpace.ToHeadOrigin(absoluteEye, pose), Eye) < .000001f,
            "Calibration preserves the tracked eye origin while moving between coordinate spaces");
        Vector3 point = CalibrationGridImage.TargetPoint(0);
        Require(CalibrationSpace.TryIndicatorFrame(Eye, point, out var frame) &&
            Vector3.Distance(Vector3.Normalize(new Vector3(frame.m3, frame.m7, frame.m11) - Eye),
                Vector3.Normalize(point - Eye)) < .000001f && frame.m11 > point.Z,
            "Calibration dot's surface offset keeps its angular alignment with the visible target");
    }

    private static CalibrationProfile Saved()
    {
        CalibrationGrid.TryAngles(Vector3.Normalize(CalibrationGridImage.TargetPoint(CalibrationGrid.CenterIndex) - Eye), out var expected);
        var raw = expected + Offset;
        return CalibrationProfile.Empty("head").WithCapture(CalibrationGrid.CenterIndex,
            [new CalibrationSample(0, raw.X, raw.Y, expected.X, expected.Y),
             new CalibrationSample(.1, raw.X, raw.Y, expected.X, expected.Y)]);
    }

    private static Vector3 RawDirection(int target)
    {
        CalibrationGrid.TryAngles(CalibrationGridImage.TargetPoint(target) - Eye, out var expected);
        return CalibrationGrid.FromAngles(expected + Offset);
    }

    private static void Tick(CalibrationRoutine routine, double now, ControllerButtonFrame buttons = null,
        bool valid = true, int gazeTarget = CalibrationGrid.CenterIndex, Vector3? point = null, bool hasProjection = true,
        Vector3? rawDirection = null) =>
        routine.Update(buttons ?? Frame(), valid, Eye, rawDirection ?? RawDirection(CalibrationGrid.CenterIndex),
            valid && hasProjection ? point ?? CalibrationGridImage.TargetPoint(gazeTarget) : null, now);

    private static ControllerButtonFrame Frame(bool a = false, bool b = false, bool rightA = false,
        bool rightB = false, bool aAvailable = true, long generation = 1) => new(
        new(Identity(ETrackedControllerRole.LeftHand, generation), new(aAvailable, a), new(true, b)),
        new(Identity(ETrackedControllerRole.RightHand, generation), new(true, rightA), new(true, rightB)));

    private static ControllerHandIdentity Identity(ETrackedControllerRole role, long generation) =>
        new(role, role == ETrackedControllerRole.LeftHand ? 1u : 2u, "knuckles", "model", "serial", (ulong)role, generation);

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
