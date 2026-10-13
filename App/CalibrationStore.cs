using System.Text.Json;

namespace SteamDashET;

internal static class CalibrationStore
{
    internal const long MaximumFileBytes = 1024 * 1024;

    // A null headset id reads the saved status before the headset connects. A real id
    // prevents a profile from being applied to a different headset. Loading never edits disk.
    internal static CalibrationProfile Load(string path, string headsetId) => Load(path, headsetId, out _);

    internal static CalibrationProfile Load(string path, string headsetId, out string error)
    {
        error = "";
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaximumFileBytes)
                throw new InvalidDataException("The calibration file exceeds the supported size limit.");
            var stored = JsonSerializer.Deserialize<StoredProfile>(stream);
            if (stored == null)
                throw new InvalidDataException("The calibration file does not contain a profile.");
            if (stored.Rows != CalibrationGrid.Rows || stored.Columns != CalibrationGrid.Columns)
                throw new InvalidDataException("The calibration file has an unsupported grid layout.");
            if (stored.HeadsetId == null)
                throw new InvalidDataException("The calibration file is missing its headset identifier.");
            if (stored.Targets == null || stored.Targets.Length != CalibrationGrid.Count)
                throw new InvalidDataException("The calibration file has an incomplete target list.");
            if (headsetId != null && !string.Equals(stored.HeadsetId, headsetId, StringComparison.Ordinal))
                return CalibrationProfile.Empty(headsetId);
            var profile = CalibrationProfile.Empty(stored.HeadsetId);
            for (int i = 0; i < stored.Targets.Length; i++)
            {
                if (stored.Targets[i] is { } point)
                    profile = profile.WithPoint(i, point);
            }
            return profile;
        }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
            return CalibrationProfile.Empty(headsetId);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or ArgumentException or NotSupportedException)
        {
            error = "Unable to load calibration: " + failure.Message;
            return CalibrationProfile.Empty(headsetId);
        }
    }

    internal static void Save(string path, CalibrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new StoredProfile
                {
                    Rows = CalibrationGrid.Rows,
                    Columns = CalibrationGrid.Columns,
                    HeadsetId = profile.HeadsetId,
                    Targets = profile.Targets.ToArray()
                });
                stream.Flush(flushToDisk: true);
            }
            // Same-directory rename publishes the complete new file, keeping the existing
            // file intact if writing fails. CalibrationService serializes these saves.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class StoredProfile
    {
        public int Rows { get; set; }
        public int Columns { get; set; }
        public string HeadsetId { get; set; }
        public CalibrationPoint?[] Targets { get; set; }
    }
}
