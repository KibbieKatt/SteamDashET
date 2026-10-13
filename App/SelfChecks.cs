using System.Numerics;
using System.Text.Json;
using Valve.VR;

namespace SteamDashET;

internal static class SelfChecks
{
    internal static void Run()
    {
        GazeInput.CheckAbi();
        var gaze = new VREyeTrackingData_t
        {
            bActive = true, bValid = true, bTracked = true,
            vGazeOrigin = new HmdVector3_t { v0 = 1, v1 = 2, v2 = 3 },
            vGazeTarget = new HmdVector3_t { v0 = 1, v1 = 2, v2 = 1 }
        };
        Require(GazeInput.TryDirection(gaze, out var direction) && direction == -Vector3.UnitZ,
            "Combined gaze normalizes target minus origin");
        gaze.bTracked = false;
        Require(!GazeInput.TryDirection(gaze, out _), "Untracked gaze is rejected");
        gaze.bTracked = true;
        gaze.vGazeTarget = gaze.vGazeOrigin;
        Require(!GazeInput.TryDirection(gaze, out _), "Zero ray is rejected");
        gaze.vGazeTarget.v0 = float.NaN;
        Require(!GazeInput.TryDirection(gaze, out _), "Non-finite ray is rejected");
        string temporary = Path.Combine(Path.GetTempPath(), "SteamDashET-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(GazeInput.WriteManifest(temporary, "lighthouse_hmd")));
            using var binding = JsonDocument.Parse(File.ReadAllText(Path.Combine(temporary, "binding-1.json")));
            Require(manifest.RootElement.GetProperty("actions")[0].GetProperty("type").GetString() == "eyetracking" &&
                binding.RootElement.GetProperty("bindings").GetProperty(GazeInput.ActionSet).GetProperty("eyetracking")[0]
                    .GetProperty("path").GetString() == "/user/head/eyetracking", "Generated action and HMD binding agree");
        }
        finally
        {
            foreach (string file in new[] { "actions.json", "binding-0.json", "binding-1.json" })
                File.Delete(Path.Combine(temporary, file));
            if (Directory.Exists(temporary)) Directory.Delete(temporary);
        }
        ConnectionChecks();
        SmoothingChecks.Run();
        PanelChecks.Run();
        PanelDiscoveryChecks.Run();
        LogDiscoveryChecks.Run();
        SettingsChecks();
        LifecycleChecks().GetAwaiter().GetResult();
    }

    private static void SettingsChecks()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "SteamDashET-settings-check-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(temporary, "settings.json");
        try
        {
            var settings = AppSettings.Load(path);
            Require(settings.SmoothingSeconds == AppSettings.DefaultSmoothingSeconds, "Smoothing defaults to 0.07 seconds");
            settings.SmoothingSeconds = 0.37;
            settings.Save();
            Require(AppSettings.Load(path).SmoothingSeconds == 0.37, "Smoothing period survives saving and loading");
            File.WriteAllText(path, "{ invalid JSON");
            Require(AppSettings.Load(path).SmoothingSeconds == AppSettings.DefaultSmoothingSeconds,
                "Unreadable settings use the default without preventing startup");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
            if (Directory.Exists(temporary)) Directory.Delete(temporary);
        }
    }

    private static void ConnectionChecks()
    {
        foreach (var error in new[] { EVRInputError.NoSteam, EVRInputError.IPCError, EVRInputError.InvalidHandle })
            Require(ThrowsInvalidOperation(() => SteamVrSession.CheckInputConnection(error, "Test gaze input")),
                $"Gaze input {error} requires a fresh session");
        SteamVrSession.CheckInputConnection(EVRInputError.None, "Test gaze input");
        SteamVrSession.CheckInputConnection(EVRInputError.NoData, "Test gaze input");
        Require(true, "Successful input and missing gaze data preserve the session");
        Require(!SteamVrSession.NeedsFrameSyncFallback(EVROverlayError.None) &&
            SteamVrSession.NeedsFrameSyncFallback(EVROverlayError.TimedOut),
            "Frame sync needs fallback pacing only when it times out");
        Require(Enum.GetValues<EVROverlayError>()
            .Where(error => error != EVROverlayError.None && error != EVROverlayError.TimedOut)
            .All(error => ThrowsInvalidOperation(() => SteamVrSession.NeedsFrameSyncFallback(error))),
            "Other frame sync failures require a fresh session");
    }

    private static async Task LifecycleChecks()
    {
        var counters = new Counters();
        var controller = new VrController(() => new FakeSession(counters));
        try
        {
            await controller.StartAsync();
            await WaitUntil(() => controller.Status.EyeTrackingReady);
            await Task.WhenAll(controller.StartAsync(), controller.StartAsync());
            Require(counters.Created == 1, "Repeated Start shares one native session");
            await controller.StopAsync();
            Require(counters.Active == 0 && counters.Disposed == 1 && !controller.Status.IsRunning && !controller.Status.DotVisible,
                "Stop disposes the session before reporting stopped");
            await controller.StartAsync();
            await WaitUntil(() => controller.Status.EyeTrackingReady);
            Require(counters.Created == 2, "Start after Stop creates a fresh session");
            await Task.WhenAll(controller.StopAsync(), controller.StartAsync(), controller.StopAsync());
            Require(counters.MaxActive == 1 && counters.Active == 0 && !controller.Status.IsRunning,
                "Overlapping start/stop requests serialize session ownership");
        }
        finally { await controller.StopAsync(); }

        var unavailable = new VrController(() => throw new InvalidOperationException("Simulated runtime missing."));
        try
        {
            await unavailable.StartAsync();
            await WaitUntil(() => unavailable.Status.Detail.Contains("Simulated runtime"));
            await unavailable.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Require(!unavailable.Status.IsRunning, "Stop cancels the reconnect wait");
        }
        finally { await unavailable.StopAsync(); }

        var recoveryCounters = new Counters();
        int attempts = 0;
        bool disposedBeforeReconnect = false;
        var recovering = new VrController(() =>
        {
            if (++attempts == 1) return new FakeSession(recoveryCounters, EVRInputError.NoSteam);
            disposedBeforeReconnect = recoveryCounters.Disposed == 1 && recoveryCounters.Active == 0;
            return new FakeSession(recoveryCounters);
        });
        try
        {
            await recovering.StartAsync();
            await WaitUntil(() => recovering.Status.Dashboard == "Disconnected" &&
                recovering.Status.Detail.Contains("NoSteam"));
            await WaitUntil(() => recovering.Status.EyeTrackingReady);
            Require(attempts == 2 && disposedBeforeReconnect && recoveryCounters.MaxActive == 1,
                "A polling connection failure disposes its session before reconnecting");
        }
        finally { await recovering.StopAsync(); }

        using var release = new ManualResetEventSlim();
        var fallbackSession = new FallbackSession(release);
        var fallback = new VrController(() => fallbackSession);
        try
        {
            await fallback.StartAsync();
            await fallbackSession.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fallback.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Require(fallbackSession.CancellationObserved && fallbackSession.Disposed && !fallback.Status.IsRunning,
                "Stop cancels a session's fallback wait before disposing it");
        }
        finally
        {
            // Release the fake even if an assertion fails, so cleanup cannot remain blocked.
            release.Set();
            await fallback.StopAsync();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static bool ThrowsInvalidOperation(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
    }
    private static void Require(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
    private sealed class Counters { internal int Created, Disposed, Active, MaxActive; }
    private sealed class FakeSession : IVrSession
    {
        private readonly Counters counters;
        private readonly EVRInputError inputError;
        internal FakeSession(Counters counters, EVRInputError inputError = EVRInputError.None)
        {
            this.counters = counters;
            this.inputError = inputError;
            Interlocked.Increment(ref counters.Created);
            int active = Interlocked.Increment(ref counters.Active);
            counters.MaxActive = Math.Max(counters.MaxActive, active);
        }
        public VrStatus Poll(CancellationToken token)
        {
            SteamVrSession.CheckInputConnection(inputError, "Test gaze input");
            // Simulate the native frame wait instead of spinning when the controller adds no delay.
            token.WaitHandle.WaitOne(10);
            return new(true, "Receiving gaze", "Connected · Open", "Test dot", true, true, true);
        }
        public void Dispose()
        {
            Interlocked.Increment(ref counters.Disposed);
            Interlocked.Decrement(ref counters.Active);
        }
    }
    private sealed class FallbackSession : IVrSession
    {
        private readonly ManualResetEventSlim release;
        internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CancellationObserved;
        internal bool Disposed;
        internal FallbackSession(ManualResetEventSlim release) => this.release = release;
        public VrStatus Poll(CancellationToken token)
        {
            Waiting.TrySetResult();
            CancellationObserved = WaitHandle.WaitAny(new[] { token.WaitHandle, release.WaitHandle }) == 0;
            return new(true, "Receiving gaze", "Connected · Open", "Test dot", true, true, true);
        }
        public void Dispose() => Disposed = true;
    }
}
