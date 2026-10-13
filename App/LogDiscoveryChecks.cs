using System.Text.Json;

namespace SteamDashET;

internal static class LogDiscoveryChecks
{
    internal static void Run()
    {
        RegisteredLogChecks();
        RegistryFailureChecks();
        MissingLogRecoveryChecks();
        UnreadableLogChecks();
    }

    private static void RegisteredLogChecks()
    {
        using var files = new LogFiles();
        files.WriteRegistry(files.LogDirectory, files.HistoricalLogDirectory);
        const string older = "system.window.older";
        const string newest = "valve.steam.gamepadui.frame.menu.newest";
        const string previous = "valve.steam.gamepadui.barpopup.previous";
        const string historical = "system.window.unusedhistory";
        const string desktopGame = "valve.steam.desktopgame.2467050";
        const string desktopGamePointer = "valve.steam.desktopgame.pointer";
        File.WriteAllText(files.CurrentLog, Created(older) +
            $"{{\"overlay_key\":\"{desktopGame}\"}}\n" + Created(desktopGamePointer) + Popup(newest));
        File.WriteAllText(files.PreviousLog, Created(previous));
        File.WriteAllText(files.HistoricalLog, Created(historical));

        var discovery = new DashboardPanelDiscovery(files.Registry);
        var keys = discovery.ReadCandidates(CancellationToken.None).ToList();
        Require(HasBaseline(keys) && keys.Contains(older) && keys.Contains(newest) && keys.Contains(previous),
            "Registered custom log directories supply current and previous UI overlay keys");
        Require(keys.Contains(desktopGame) && !keys.Contains(desktopGamePointer),
            "UI logs discover desktop-game content without admitting its pointer overlay");
        Require(!keys.Contains(historical), "Only the first registered log path is the current source");
        Require(keys.IndexOf(newest) < keys.IndexOf(older) && keys.IndexOf(older) < keys.IndexOf(previous),
            "Fresh current-log keys precede older entries and previous-log keys");
        Require(discovery.Warnings.Length == 0, "Readable registered UI logs produce no warning");
    }

    private static void RegistryFailureChecks()
    {
        using var files = new LogFiles();
        var missing = new DashboardPanelDiscovery(files.Registry);
        Require(OnlyBaseline(missing.ReadCandidates(CancellationToken.None)) && missing.Warnings.Length > 0,
            "A missing path registry preserves baseline keys and reports limited discovery");

        const string recovered = "system.window.registryrecovered";
        File.WriteAllText(files.CurrentLog, Created(recovered));
        files.WriteRegistry(files.LogDirectory);
        Require(missing.ReadCandidates(CancellationToken.None).Contains(recovered) && missing.Warnings.Length == 0,
            "The same discovery instance recovers when a missing registry and current log appear");

        File.WriteAllText(files.Registry, "{ malformed registry");
        var malformed = new DashboardPanelDiscovery(files.Registry);
        Require(OnlyBaseline(malformed.ReadCandidates(CancellationToken.None)) && malformed.Warnings.Length > 0,
            "A malformed path registry preserves baseline keys and reports limited discovery");
        files.WriteRegistry(files.LogDirectory);
        Require(malformed.ReadCandidates(CancellationToken.None).Contains(recovered) && malformed.Warnings.Length == 0,
            "The same discovery instance recovers when a malformed registry becomes valid");

        foreach (var (name, json) in new[]
        {
            ("missing log", "{}"),
            ("empty log list", "{\"log\":[]}"),
            ("empty first log path", "{\"log\":[\"\"]}"),
            ("nonstring first log path", "{\"log\":[42]}"),
            ("null first log path", "{\"log\":[null]}")
        })
        {
            File.WriteAllText(files.Registry, json);
            var invalid = new DashboardPanelDiscovery(files.Registry);
            Require(OnlyBaseline(invalid.ReadCandidates(CancellationToken.None)) && invalid.Warnings.Length > 0,
                $"A valid registry JSON with {name} preserves baseline keys and warns without throwing");
        }
    }

    private static void MissingLogRecoveryChecks()
    {
        using var files = new LogFiles();
        files.WriteRegistry(files.LogDirectory);
        var discovery = new DashboardPanelDiscovery(files.Registry);
        Require(OnlyBaseline(discovery.ReadCandidates(CancellationToken.None)) &&
            WarnsAboutCurrentLog(discovery) && !WarnsAboutPreviousLog(discovery),
            "A missing current UI log warns while an absent previous log remains optional");

        const string first = "system.window.recovered";
        File.WriteAllText(files.CurrentLog, Created(first));
        var recovered = discovery.ReadCandidates(CancellationToken.None);
        Require(recovered.Contains(first) && discovery.Warnings.Length == 0,
            "Discovery recovers and clears its warning when the current UI log appears");

        const string popup = "valve.steam.gamepadui.frame.menu.freshpopup";
        File.AppendAllText(files.CurrentLog, Popup(popup));
        var refreshed = discovery.ReadCandidates(CancellationToken.None).ToList();
        Require(refreshed.Contains(first) && refreshed.Contains(popup) &&
            refreshed.IndexOf(popup) < refreshed.IndexOf(first) && discovery.Warnings.Length == 0,
            "An immediate second log read discovers a new popup without a stale cache");
    }

    private static void UnreadableLogChecks()
    {
        using var files = new LogFiles();
        files.WriteRegistry(files.LogDirectory);
        File.WriteAllText(files.CurrentLog, Created("system.window.locked"));
        var discovery = new DashboardPanelDiscovery(files.Registry);
        using (var locked = new FileStream(files.CurrentLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Require(OnlyBaseline(discovery.ReadCandidates(CancellationToken.None)) &&
                WarnsAboutCurrentLog(discovery) && !WarnsAboutPreviousLog(discovery),
                "An unreadable current UI log preserves baseline keys and reports its warning");
        }
        Require(discovery.ReadCandidates(CancellationToken.None).Contains("system.window.locked") &&
            discovery.Warnings.Length == 0,
            "Discovery recovers and clears its warning after the current UI log becomes readable");
    }

    private static bool HasBaseline(IEnumerable<string> keys)
    {
        var actual = keys.ToHashSet(StringComparer.Ordinal);
        return DashboardPanelDiscovery.BaselineKeys.All(actual.Contains);
    }

    private static bool OnlyBaseline(IEnumerable<string> keys) =>
        keys.ToHashSet(StringComparer.Ordinal).SetEquals(DashboardPanelDiscovery.BaselineKeys);

    private static bool WarnsAboutCurrentLog(DashboardPanelDiscovery discovery) =>
        discovery.Warnings.Any(warning => warning.Contains("vrwebhelper_systemui.txt", StringComparison.Ordinal));

    private static bool WarnsAboutPreviousLog(DashboardPanelDiscovery discovery) =>
        discovery.Warnings.Any(warning => warning.Contains("vrwebhelper_systemui.previous.txt", StringComparison.Ordinal));

    private static string Created(string key) => $"[Overlays] Created: {key}\n";
    private static string Popup(string key) => $"[PooledPopups] Showing: {{\"key\":\"{key}\"}}\n";

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private sealed class LogFiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "SteamDashET-log-check-" + Guid.NewGuid().ToString("N"));
        internal string Registry => Path.Combine(root, "openvrpaths.vrpath");
        internal string LogDirectory => Path.Combine(root, "registered-custom-logs");
        internal string HistoricalLogDirectory => Path.Combine(root, "historical-logs");
        internal string CurrentLog => Path.Combine(LogDirectory, "vrwebhelper_systemui.txt");
        internal string PreviousLog => Path.Combine(LogDirectory, "vrwebhelper_systemui.previous.txt");
        internal string HistoricalLog => Path.Combine(HistoricalLogDirectory, "vrwebhelper_systemui.txt");

        internal LogFiles()
        {
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(HistoricalLogDirectory);
        }

        internal void WriteRegistry(params string[] logs) => File.WriteAllText(Registry, JsonSerializer.Serialize(new
        {
            jsonid = "vrpathreg", version = 1,
            runtime = new[] { Path.Combine(root, "unrelated-runtime") },
            config = new[] { Path.Combine(root, "unrelated-config") },
            log = logs
        }));

        public void Dispose()
        {
            foreach (string file in new[] { Registry, CurrentLog, PreviousLog, HistoricalLog }) File.Delete(file);
            if (Directory.Exists(LogDirectory)) Directory.Delete(LogDirectory);
            if (Directory.Exists(HistoricalLogDirectory)) Directory.Delete(HistoricalLogDirectory);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }
}
