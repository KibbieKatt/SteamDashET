using System.Text.Json;

namespace SteamDashET;

internal sealed class AppSettings
{
    internal const double DefaultSmoothingSeconds = 0.07;
    internal const CalibrationInterpolationMethod DefaultInterpolationMethod = CalibrationInterpolationMethod.LinearTriangles;
    private readonly string path;
    private int smoothingHundredths = (int)(DefaultSmoothingSeconds * 100);
    private int interpolationMethod = (int)DefaultInterpolationMethod;

    internal AppSettings(string path = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamDashET", "settings.json");
    }

    // The UI writes this setting; the VR worker reads it without accessing UI controls.
    public double SmoothingSeconds
    {
        get => Volatile.Read(ref smoothingHundredths) / 100.0;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException(nameof(value), "Smoothing must be between 0 and 1 seconds.");
            Volatile.Write(ref smoothingHundredths, (int)Math.Round(value * 100, MidpointRounding.ToEven));
        }
    }

    public CalibrationInterpolationMethod InterpolationMethod
    {
        get => (CalibrationInterpolationMethod)Volatile.Read(ref interpolationMethod);
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), "Choose a supported calibration interpolation method.");
            Volatile.Write(ref interpolationMethod, (int)value);
        }
    }

    internal static AppSettings Load(string path = null)
    {
        var settings = new AppSettings(path);
        try
        {
            if (File.Exists(settings.path))
            {
                using var file = JsonDocument.Parse(File.ReadAllText(settings.path));
                if (file.RootElement.TryGetProperty(nameof(SmoothingSeconds), out var smoothing) &&
                    smoothing.ValueKind == JsonValueKind.Number &&
                    smoothing.TryGetDouble(out double seconds) && double.IsFinite(seconds))
                    settings.SmoothingSeconds = Math.Clamp(seconds, 0, 1);
                if (file.RootElement.TryGetProperty(nameof(InterpolationMethod), out var method) &&
                    method.ValueKind == JsonValueKind.String)
                    settings.InterpolationMethod = method.GetString() switch
                    {
                        nameof(CalibrationInterpolationMethod.LinearTriangles) => CalibrationInterpolationMethod.LinearTriangles,
                        nameof(CalibrationInterpolationMethod.SmoothRbf) => CalibrationInterpolationMethod.SmoothRbf,
                        _ => DefaultInterpolationMethod
                    };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine("SteamDashET settings: " + error.Message);
        }
        return settings;
    }

    public void Save()
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            SmoothingSeconds,
            InterpolationMethod = InterpolationMethod.ToString()
        },
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
