#nullable enable

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamDashET;

internal sealed class DashboardPanelDiscovery
{
    private const int MaximumLogTailBytes = 4 * 1024 * 1024;
    private static readonly Regex CandidatePattern = new(
        @"\[Overlays\]\s+Created:\s*(?<key>[^\s""]{1,128})(?=\s|$)|""(?:overlayKey|overlay_key|popup_overlay_key|sOverlayKey)""\s*:\s*""(?<key>[^""\\\r\n]{1,128})""|\[PooledPopups\]\s+Showing:[^\r\n]{0,1024}?""key""\s*:\s*""(?<key>[^""\\\r\n]{1,128})""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline,
        TimeSpan.FromSeconds(1));
    private static readonly string[] SeedKeys =
    [
        "valve.steam.gamepadui.main", "valve.steam.gamepadui.bar", "system.systemui",
        "system.desktop", "system.desktop.1", "system.desktop.2", "system.desktop.3", "system.desktop.4",
        "system.desktop.5", "system.desktop.6", "system.desktop.7", "system.desktop.8",
        "system.keyboard", "valve.steam.gamepadui.keyboard", "system.settings",
        "system.vrwebhelper.controllerbinding", "system.messageoverlay", "system.dashboard",
        "system.dashboard.quicklaunch", "system.dashboard.nowplaying"
    ];
    internal static IReadOnlyList<string> BaselineKeys { get; } = Array.AsReadOnly(SeedKeys);

    private readonly string? pathRegistryFile;
    private readonly string? logDirectoryOverride;
    private readonly string[]? explicitLogPaths;
    private string[] warnings = Array.Empty<string>();

    internal DashboardPanelDiscovery(string? pathRegistryFile = null)
    {
        // Explicit registry files keep checks independent of this machine's environment overrides.
        if (pathRegistryFile is not null)
            this.pathRegistryFile = pathRegistryFile;
        else
        {
            string? registryOverride = Environment.GetEnvironmentVariable("VR_PATHREG_OVERRIDE");
            this.pathRegistryFile = !string.IsNullOrEmpty(registryOverride) ? registryOverride
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "openvr", "openvrpaths.vrpath");
            logDirectoryOverride = Environment.GetEnvironmentVariable("VR_LOG_PATH");
        }
    }

    internal DashboardPanelDiscovery(string[] logPaths) => explicitLogPaths = (string[])logPaths.Clone();

    public string[] Warnings => (string[])warnings.Clone();

    public IReadOnlyList<string> ReadCandidates(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var found = new List<string>(SeedKeys);
        var seen = new HashSet<string>(SeedKeys, StringComparer.Ordinal);
        var failures = new List<string>();
        string[] logPaths;
        try { logPaths = ResolveLogPaths(); }
        catch (Exception exception) when (IsReadFailure(exception) || exception is JsonException or InvalidDataException)
        {
            logPaths = Array.Empty<string>();
            failures.Add("Cannot locate SteamVR UI logs: " + exception.Message);
        }
        for (int index = 0; index < logPaths.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            string path = logPaths[index];
            try
            {
                MatchCollection matches = CandidatePattern.Matches(ReadBoundedTail(path, token));
                // Fresh keys take priority when the caller bounds its native overlay lookups.
                for (int matchIndex = matches.Count - 1; matchIndex >= 0; matchIndex--)
                {
                    token.ThrowIfCancellationRequested();
                    string key = matches[matchIndex].Groups["key"].Value;
                    if (DashboardPanels.IsPanelCandidate(key) && seen.Add(key)) found.Add(key);
                }
            }
            // SteamVR need not have rotated its current log yet.
            catch (Exception exception) when (index > 0 && exception is (FileNotFoundException or DirectoryNotFoundException)) { }
            catch (Exception exception) when (IsReadFailure(exception) || exception is RegexMatchTimeoutException)
            {
                failures.Add(Path.GetFileName(path) + ": " + exception.Message);
            }
        }
        warnings = failures.ToArray();
        return found;
    }

    private string[] ResolveLogPaths()
    {
        if (explicitLogPaths is not null) return explicitLogPaths;
        string? logDirectory = logDirectoryOverride;
        if (string.IsNullOrEmpty(logDirectory))
        {
            using var registry = JsonDocument.Parse(File.ReadAllText(pathRegistryFile!));
            JsonElement root = registry.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("log", out var logs) ||
                logs.ValueKind != JsonValueKind.Array || logs.GetArrayLength() == 0 ||
                logs[0].ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The OpenVR path registry has no current log directory.");
            // OpenVR stores the current path first and older registered paths after it.
            logDirectory = logs[0].GetString();
        }
        if (string.IsNullOrWhiteSpace(logDirectory))
            throw new InvalidDataException("The OpenVR log directory is empty.");
        logDirectory = Path.GetFullPath(logDirectory);
        return
        [
            Path.Combine(logDirectory, "vrwebhelper_systemui.txt"),
            Path.Combine(logDirectory, "vrwebhelper_systemui.previous.txt")
        ];
    }

    private static string ReadBoundedTail(string path, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        long snapshotLength = file.Length;
        int count = (int)Math.Min(snapshotLength, MaximumLogTailBytes);
        long start = snapshotLength - count;
        file.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[count];
        int bytesRead = 0;
        while (bytesRead < count)
        {
            token.ThrowIfCancellationRequested();
            int read = file.Read(buffer, bytesRead, Math.Min(count - bytesRead, 64 * 1024));
            if (read == 0) break;
            bytesRead += read;
        }
        string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        if (start > 0)
        {
            int firstNewline = text.IndexOf('\n');
            text = firstNewline >= 0 ? text[(firstNewline + 1)..] : string.Empty;
        }
        return text;
    }

    private static bool IsReadFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
