using System.Numerics;

namespace SteamDashET;

// The UI requests calibration; the VR worker collects it. Published models and profiles are immutable.
internal sealed class CalibrationService
{
    private readonly object gate = new();
    private readonly Action<CalibrationProfile> save;
    private readonly Func<string, CalibrationProfile> load;
    private CalibrationProfile profile;
    private ICalibrationCorrectionField model;
    private CalibrationInterpolationMethod interpolationMethod;
    private long revision, runId;
    private bool active, collecting;
    private int selected = CalibrationGrid.CenterIndex, pending;
    private string hint = "", error = "", loadError = "", fitError = "";

    internal CalibrationService(string path = null, CalibrationInterpolationMethod interpolationMethod = AppSettings.DefaultInterpolationMethod)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamDashET", "calibration.json");
        load = id => CalibrationStore.Load(path, id, out loadError);
        save = value => CalibrationStore.Save(path, value);
        profile = load(null);
        model = CreateLoadedModel(profile, interpolationMethod);
        this.interpolationMethod = interpolationMethod;
    }

    internal CalibrationService(CalibrationProfile profile, Action<CalibrationProfile> save,
        CalibrationInterpolationMethod interpolationMethod = AppSettings.DefaultInterpolationMethod)
    {
        this.profile = profile;
        this.save = save;
        load = id => this.profile.HeadsetId == id ? this.profile : CalibrationProfile.Empty(id);
        model = CreateLoadedModel(profile, interpolationMethod);
        this.interpolationMethod = interpolationMethod;
    }

    internal CalibrationSnapshot Snapshot
    {
        get
        {
            lock (gate)
                return new(active, selected, collecting, pending, profile.PopulatedTargetCount,
                    profile.Targets.Select(point => point.HasValue).ToArray(), revision, runId, hint, error, loadError, fitError);
        }
    }

    internal void BindHeadset(string identity)
    {
        lock (gate)
        {
            if (profile.HeadsetId == identity) return;
            profile = load(identity);
            model = CreateLoadedModel(profile, interpolationMethod);
            revision++;
            Finish();
        }
    }

    internal Vector3 Correct(Vector3 direction)
    {
        ICalibrationCorrectionField current;
        lock (gate) current = model;
        return current.Correct(direction);
    }

    internal void SetInterpolationMethod(CalibrationInterpolationMethod method)
    {
        lock (gate)
        {
            if (method == interpolationMethod) return;
            var replacement = CreateLoadedModel(profile, method);
            model = replacement;
            interpolationMethod = method;
            revision++;
        }
    }

    private ICalibrationCorrectionField CreateLoadedModel(CalibrationProfile value, CalibrationInterpolationMethod method)
    {
        try
        {
            var fitted = CalibrationCorrectionFields.Create(value, method);
            fitError = "";
            return fitted;
        }
        catch (InvalidOperationException failure) when (method == CalibrationInterpolationMethod.SmoothRbf)
        {
            // Retain the saved points for recapture/clear, with visibly uncorrected
            // gaze instead of applying an invalid fit or changing interpolation modes.
            fitError = "Unable to fit calibration: " + failure.Message +
                " Select Linear triangles to repair the saved points, or clear calibration.";
            return CalibrationCorrectionFields.Create(CalibrationProfile.Empty(value.HeadsetId), method);
        }
    }

    internal void Begin()
    {
        lock (gate)
        {
            runId++;
            active = true;
            selected = CalibrationGrid.CenterIndex;
            collecting = false;
            pending = 0;
            hint = "Look at a target to select it.";
            error = "";
        }
    }

    internal void Finish()
    {
        lock (gate)
        {
            active = collecting = false;
            pending = 0;
            hint = "";
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            if (active) return;
            Replace(CalibrationProfile.Empty(profile.HeadsetId));
        }
    }

    internal bool SaveCapture(long expectedRun, int target, IEnumerable<CalibrationSample> samples)
    {
        lock (gate)
        {
            if (!active || runId != expectedRun) return false;
            try
            {
                Replace(profile.WithCapture(target, samples));
                hint = "Capture average saved; this target's previous data is replaced.";
                return true;
            }
            catch (Exception exception)
            {
                error = "Could not save calibration: " + exception.Message;
                return false;
            }
        }
    }

    internal void ClearTarget(long expectedRun, int target)
    {
        lock (gate)
        {
            if (!active || runId != expectedRun) return;
            try
            {
                Replace(profile.WithoutTarget(target));
                hint = "Target cleared; updated correction is applied.";
            }
            catch (Exception exception) { error = "Could not clear target: " + exception.Message; }
        }
    }

    private void Replace(CalibrationProfile replacement)
    {
        var replacementModel = CalibrationCorrectionFields.Create(replacement, interpolationMethod);
        save(replacement);
        profile = replacement;
        model = replacementModel;
        revision++;
        error = "";
        loadError = "";
        fitError = "";
    }

    internal void SetLive(long expectedRun, int target, bool isCollecting, int sampleCount, string message = null)
    {
        lock (gate)
        {
            if (!active || runId != expectedRun) return;
            selected = target;
            collecting = isCollecting;
            pending = sampleCount;
            if (message != null) hint = message;
        }
    }
}

internal sealed record CalibrationSnapshot(bool Active, int Selected, bool Collecting, int PendingSamples,
    int PopulatedTargets, bool[] HasSavedPoints, long Revision, long RunId, string Hint, string Error, string LoadError,
    string FitError = "")
{
    internal string Warning => LoadError.Length > 0 ? LoadError : FitError;
    internal string Status => (Active ? "Calibrating · " : "") + (LoadError.Length > 0 ? "Load warning" : FitError.Length > 0
        ? "Fit warning" : PopulatedTargets == 0
        ? "No saved points" : $"{PopulatedTargets}/{CalibrationGrid.Count} points");
}
