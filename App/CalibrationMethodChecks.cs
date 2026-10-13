using System.Numerics;

namespace SteamDashET;

internal static class CalibrationMethodChecks
{
    internal static void Run()
    {
        var points = new[] {
            new CalibrationPoint(-.1f, -.08f, -.14f, -.05f),
            new CalibrationPoint(.12f, -.05f, .145f, -.09f),
            new CalibrationPoint(.02f, .14f, .05f, .16f) };
        var profile = CalibrationProfile.Empty("headset");
        for (int index = 0; index < points.Length; index++) profile = profile.WithPoint(index, points[index]);
        var saves = new List<CalibrationProfile>();
        var service = new CalibrationService(profile, saves.Add, CalibrationInterpolationMethod.SmoothRbf);
        Vector3 input = CalibrationGrid.FromAngles(new Vector2(points[0].Yaw, points[0].Pitch));
        Vector3 expected = CalibrationGrid.FromAngles(new Vector2(points[0].TargetYaw, points[0].TargetPitch));
        Vector3 rbf = new RbfCalibrationField(profile).Correct(input);
        Require(service.Correct(input) == rbf && Vector3.Distance(rbf, expected) < .000001f,
            "An explicit RBF selection uses the exact RBF interpolation behavior");
        var defaultService = new CalibrationService(profile, _ => { });
        Vector3 defaultProbe = CalibrationGrid.FromAngles(new Vector2(.04f, .03f));
        Require(defaultService.Correct(defaultProbe) == new LinearTriangleCalibrationField(profile).Correct(defaultProbe),
            "The calibration service defaults to linear triangle interpolation");
        var before = service.Snapshot;
        service.SetInterpolationMethod(CalibrationInterpolationMethod.LinearTriangles);
        var switched = service.Snapshot;
        Require(Vector3.Distance(service.Correct(input), expected) < .000001f &&
            switched.Revision > before.Revision && saves.Count == 0 &&
            switched.HasSavedPoints.SequenceEqual(before.HasSavedPoints),
            "Switching to triangles applies saved averages exactly and updates the revision without rewriting calibration");
        service.SetInterpolationMethod(CalibrationInterpolationMethod.LinearTriangles);
        Require(service.Snapshot.Revision == switched.Revision,
            "Polling an unchanged interpolation setting does not refit or reset smoothing");

        service.Begin();
        var active = service.Snapshot;
        service.SetLive(active.RunId, 0, true, 7);
        service.SetInterpolationMethod(CalibrationInterpolationMethod.SmoothRbf);
        Require(service.Correct(input) == rbf && service.Snapshot.Active && service.Snapshot.Collecting &&
            service.Snapshot.RunId == active.RunId && service.Snapshot.PendingSamples == 7 && saves.Count == 0,
            "Changing interpolation preserves an active capture and its locked target while restoring the RBF field");
        service.SetInterpolationMethod(CalibrationInterpolationMethod.LinearTriangles);
        var capture = new[] { new CalibrationSample(0, points[0].Yaw, points[0].Pitch, -.13f, -.04f) };
        bool saved = service.SaveCapture(active.RunId, 0, capture);
        expected = CalibrationGrid.FromAngles(new Vector2(-.13f, -.04f));
        Require(saved && saves.Count == 1 && Vector3.Distance(service.Correct(input), expected) < .000001f,
            "A capture refits the selected interpolation field and still replaces only its saved point");
        service.ClearTarget(active.RunId, 0);
        Require(!service.Snapshot.HasSavedPoints[0] && service.Snapshot.HasSavedPoints[1] && saves.Count == 2,
            "Clearing a point rebuilds the selected field and preserves the other calibration averages");
        service.Finish();
        service.Clear();
        Require(service.Correct(input) == input && service.Snapshot.PopulatedTargets == 0,
            "Clearing all calibration leaves gaze unchanged with triangle mode selected");

        var fromStartup = new CalibrationService(profile, _ => { }, CalibrationInterpolationMethod.LinearTriangles);
        Require(Vector3.Distance(fromStartup.Correct(input),
            CalibrationGrid.FromAngles(new Vector2(points[0].TargetYaw, points[0].TargetPitch))) < .000001f,
            "A persisted triangle selection applies to loaded calibration before the first frame");
        long revision = fromStartup.Snapshot.Revision;
        bool rejected = false;
        try { fromStartup.SetInterpolationMethod((CalibrationInterpolationMethod)100); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Require(rejected && fromStartup.Snapshot.Revision == revision,
            "An unsupported field selection leaves the current model and revision intact");
        FitWarningChecks();
    }

    private static void FitWarningChecks()
    {
        var singular = CalibrationProfile.Empty("headset")
            .WithPoint(0, new CalibrationPoint(0, 0, .01f, 0))
            .WithPoint(1, new CalibrationPoint(1e-12f, 0, -.01f, 0));
        var saves = new List<CalibrationProfile>();
        var service = new CalibrationService(singular, saves.Add, CalibrationInterpolationMethod.SmoothRbf);
        Vector3 ray = CalibrationGrid.FromAngles(new Vector2(.04f, .02f));
        Require(service.Snapshot.FitError.Length > 0 && service.Snapshot.Warning == service.Snapshot.FitError &&
            service.Snapshot.Status == "Fit warning" && service.Snapshot.PopulatedTargets == 2 &&
            service.Correct(ray) == ray && saves.Count == 0,
            "An unresolvable loaded RBF fit warns and leaves gaze uncorrected while retaining every saved point");
        service.Begin();
        var active = service.Snapshot;
        Require(active.FitError.Length > 0 && active.Active,
            "Starting recapture retains the fit warning until calibration is repaired");
        bool saved = service.SaveCapture(active.RunId, 1,
            [new CalibrationSample(0, .1f, 0, -.01f, 0)]);
        Vector3 repairedRay = CalibrationGrid.FromAngles(new Vector2(.1f, 0));
        Vector3 target = CalibrationGrid.FromAngles(new Vector2(-.01f, 0));
        Require(saved && saves.Count == 1 && service.Snapshot.Warning.Length == 0 &&
            service.Snapshot.Error.Length == 0 && service.Snapshot.PopulatedTargets == 2 &&
            Vector3.Distance(service.Correct(repairedRay), target) < .000001f,
            "Recapturing an unstable point repairs the RBF fit and clears the warning");
        Vector3 previous = service.Correct(ray);
        long revision = service.Snapshot.Revision;
        bool failedCapture = service.SaveCapture(active.RunId, 1,
            [new CalibrationSample(0, 1e-12f, 0, -.01f, 0)]);
        Require(!failedCapture && saves.Count == 1 && service.Snapshot.Error.Length > 0 &&
            service.Snapshot.Warning.Length == 0 && service.Snapshot.Revision == revision &&
            service.Correct(ray) == previous,
            "An unstable new capture reports the error while preserving the previously valid fit and saved calibration");

        var switched = new CalibrationService(singular, _ => throw new InvalidOperationException("Unexpected save"),
            CalibrationInterpolationMethod.LinearTriangles);
        switched.SetInterpolationMethod(CalibrationInterpolationMethod.SmoothRbf);
        Require(switched.Snapshot.FitError.Length > 0 && switched.Correct(ray) == ray &&
            switched.Snapshot.PopulatedTargets == 2,
            "Selecting an unresolvable RBF fit warns without using triangle corrections or rewriting saved points");
        switched.SetInterpolationMethod(CalibrationInterpolationMethod.LinearTriangles);
        Require(switched.Snapshot.Warning.Length == 0 && switched.Snapshot.PopulatedTargets == 2 &&
            switched.Correct(ray) != ray,
            "Explicitly selecting triangles clears the RBF warning and applies the saved calibration");
        var cleared = new CalibrationService(singular, saves.Add, CalibrationInterpolationMethod.SmoothRbf);
        cleared.Clear();
        Require(cleared.Snapshot.Warning.Length == 0 && cleared.Snapshot.PopulatedTargets == 0 &&
            cleared.Correct(ray) == ray,
            "Clearing an unresolvable loaded calibration restores normal uncorrected gaze without a warning");
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
