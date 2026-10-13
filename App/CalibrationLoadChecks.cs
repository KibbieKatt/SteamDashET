using System.Numerics;
using System.Text.Json;

namespace SteamDashET;

internal static class CalibrationLoadChecks
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SteamDashET-load-check-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "calibration.json");
        try
        {
            var missing = CalibrationStore.Load(path, "headset", out string missingWarning);
            Require(missing.PopulatedTargetCount == 0 && missingWarning.Length == 0 && !Directory.Exists(directory),
                "A missing calibration file starts empty without a warning or creating files");

            var point = new CalibrationPoint(.03f, -.01f, 0, 0);
            var profile = CalibrationProfile.Empty("headset").WithPoint(CalibrationGrid.CenterIndex, point);
            CalibrationStore.Save(path, profile);
            string valid = File.ReadAllText(path);
            var otherHeadset = CalibrationStore.Load(path, "another-headset", out string otherWarning);
            Require(otherHeadset.PopulatedTargetCount == 0 && otherWarning.Length == 0 && File.ReadAllText(path) == valid,
                "Calibration for a different headset is ignored without reporting a load failure or changing the file");

            foreach (var invalid in new[]
            {
                (Content: "{ invalid JSON", Label: "Malformed JSON"),
                (Content: valid.Replace("\"TargetYaw\":", "\"MissingTargetYaw\":", StringComparison.Ordinal),
                    Label: "A saved average with missing required angles"),
                (Content: valid.Replace("\"Rows\":12", "\"Rows\":13", StringComparison.Ordinal),
                    Label: "An unsupported calibration grid"),
                (Content: JsonSerializer.Serialize(new
                {
                    Rows = 11, Columns = 9, HeadsetId = "headset",
                    Targets = profile.Targets.Take(99).ToArray()
                }), Label: "A calibration file with eleven rows"),
                (Content: JsonSerializer.Serialize(new
                {
                    Rows = 12, Columns = 5, HeadsetId = "headset",
                    Targets = profile.Targets.Take(60).ToArray()
                }), Label: "A calibration file with five columns"),
                (Content: JsonSerializer.Serialize(new
                {
                    Rows = CalibrationGrid.Rows, Columns = CalibrationGrid.Columns, HeadsetId = "headset",
                    Targets = Enumerable.Range(0, CalibrationGrid.Count)
                        .Select(index => index == CalibrationGrid.CenterIndex ? new[] { point } : null).ToArray()
                }), Label: "A raw sample array instead of a saved average"),
                (Content: JsonSerializer.Serialize(new
                {
                    Rows = CalibrationGrid.Rows, Columns = CalibrationGrid.Columns, HeadsetId = "headset",
                    Targets = profile.Targets.Take(CalibrationGrid.Count - 1).ToArray()
                }), Label: "A calibration file with an incomplete target list"),
                (Content: valid + new string(' ', 1024 * 1024),
                    Label: "A calibration file larger than one MiB")
            })
            {
                File.WriteAllText(path, invalid.Content);
                var rejected = CalibrationStore.Load(path, "headset", out string warning);
                Require(rejected.PopulatedTargetCount == 0 && warning.StartsWith("Unable to load calibration: ", StringComparison.Ordinal) &&
                    warning.Length > "Unable to load calibration: ".Length && File.ReadAllText(path) == invalid.Content,
                    invalid.Label + " produces an explanatory warning and loading leaves the original file intact");
            }

            const string damaged = "{ invalid JSON";
            File.WriteAllText(path, damaged);
            var service = new CalibrationService(path);
            Require(service.Snapshot.LoadError.Length > 0 && service.Snapshot.Error.Length == 0 &&
                service.Snapshot.Status == "Load warning" && service.Snapshot.PopulatedTargets == 0,
                "An unreadable calibration is visible as a load warning in the initial service status");
            service.Begin();
            service.BindHeadset("headset");
            service.Begin();
            var before = service.Snapshot;
            Vector3 ray = CalibrationGrid.FromAngles(new Vector2(.03f, -.01f));
            var samples = new[]
            {
                new CalibrationSample(0, .03f, -.01f, 0, 0),
                new CalibrationSample(.1, .03f, -.01f, 0, 0)
            };
            Require(before.Active && before.LoadError.Length > 0 && before.Status.Contains("Load warning", StringComparison.Ordinal),
                "Entering calibration and binding the headset retain an unresolved load warning");

            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var inaccessible = CalibrationStore.Load(path, "headset", out string lockedWarning);
                Require(inaccessible.PopulatedTargetCount == 0 && lockedWarning.Length > 0,
                    "A calibration file locked by another process is reported as a load failure");
                bool saved = service.SaveCapture(before.RunId, CalibrationGrid.CenterIndex, samples);
                var failed = service.Snapshot;
                Require(!saved && failed.LoadError == before.LoadError && failed.Error.Length > 0 &&
                    failed.Revision == before.Revision && failed.PopulatedTargets == before.PopulatedTargets &&
                    failed.HasSavedPoints.SequenceEqual(before.HasSavedPoints) && service.Correct(ray) == ray,
                    "A failed overwrite retains the load warning, previous profile and correction instead of publishing unsaved data");
            }
            Require(File.ReadAllText(path) == damaged && Directory.GetFiles(directory).Length == 1,
                "A failed overwrite leaves the damaged original intact and cleans up its temporary file");

            bool replaced = service.SaveCapture(before.RunId, CalibrationGrid.CenterIndex, samples);
            var completed = service.Snapshot;
            var reloaded = CalibrationStore.Load(path, "headset", out string reloadWarning);
            using (var stored = JsonDocument.Parse(File.ReadAllText(path)))
            {
                JsonElement root = stored.RootElement;
                JsonElement average = root.GetProperty("Targets")[CalibrationGrid.CenterIndex];
                Require(replaced && completed.LoadError.Length == 0 && completed.Error.Length == 0 &&
                    completed.PopulatedTargets == 1 && completed.Revision > before.Revision &&
                    reloadWarning.Length == 0 && reloaded.Targets[CalibrationGrid.CenterIndex] == point &&
                    root.EnumerateObject().Count() == 4 &&
                    root.GetProperty("Rows").GetInt32() == CalibrationGrid.Rows &&
                    root.GetProperty("Columns").GetInt32() == CalibrationGrid.Columns &&
                    root.GetProperty("HeadsetId").GetString() == "headset" && average.ValueKind == JsonValueKind.Object &&
                    average.EnumerateObject().Count() == 4 && !average.TryGetProperty("TimeSeconds", out _) &&
                    Directory.GetFiles(directory).Length == 1,
                    "A successful capture overwrites the unreadable file with one persisted average and clears its warning without creating a backup");
            }
            var relaunched = new CalibrationService(path);
            Require(relaunched.Snapshot.LoadError.Length == 0 && relaunched.Snapshot.PopulatedTargets == 1 &&
                Vector3.Distance(relaunched.Correct(ray), -Vector3.UnitZ) < .000001f,
                "The replacement calibration loads and applies after relaunch");

            File.WriteAllText(path, damaged);
            var disconnected = new CalibrationService(path);
            disconnected.Clear();
            var cleared = CalibrationStore.Load(path, null, out string clearWarning);
            Require(disconnected.Snapshot.LoadError.Length == 0 && disconnected.Snapshot.Error.Length == 0 &&
                disconnected.Snapshot.PopulatedTargets == 0 && clearWarning.Length == 0 && cleared.PopulatedTargetCount == 0 &&
                Directory.GetFiles(directory).Length == 1,
                "Clearing unreadable calibration while disconnected replaces the file with a valid empty profile and clears the warning");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
