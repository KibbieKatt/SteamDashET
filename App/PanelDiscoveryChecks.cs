using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;

namespace SteamDashET;

internal static class PanelDiscoveryChecks
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SteamDashET-panels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string log = Path.Combine(directory, "systemui.txt");
        try
        {
            using var native = new FakeOverlay();
            native.Add("system.desktop.1", 1);
            File.WriteAllLines(log, Enumerable.Range(0, 1400)
                .Select(index => $"[Overlays] Created: com.check.history.{index:D4}"));
            var discovery = new DashboardPanelDiscovery(new[] { log });
            var panels = new DashboardPanels(native.Api, discovery);
            panels.Refresh(0, true, CancellationToken.None);
            Require(native.Lookups.Count <= 1024 &&
                native.Lookups.Take(DashboardPanelDiscovery.BaselineKeys.Count)
                    .SequenceEqual(DashboardPanelDiscovery.BaselineKeys) && CachedHandles(panels, native).Contains(1),
                "Historical log overflow preserves baseline panels and bounds native lookups");

            // Let the dashboard transition period expire, then test the normal refresh schedule.
            panels.Refresh(4, true, CancellationToken.None);
            native.Lookups.Clear();
            native.Add("com.check.new.panel", 2);
            File.AppendAllText(log, "[Overlays] Created: com.check.new.panel" + Environment.NewLine);
            panels.Refresh(4.99, true, CancellationToken.None);
            Require(native.Lookups.Count == 0, "Panel refresh respects its scheduled interval");
            panels.Refresh(5, true, CancellationToken.None);
            Require(native.Lookups.Count <= 1024 && native.Lookups.Contains("com.check.new.panel") &&
                CachedHandles(panels, native).Contains(2),
                "An eventless new panel is discovered within one second despite historical capacity pressure");

            native.LookupErrors["com.check.new.panel"] = EVROverlayError.UnknownOverlay;
            panels.Refresh(6, true, CancellationToken.None);
            Require(!CachedHandles(panels, native).Contains(2),
                "A failed lookup removes a cached panel handle");
            native.LookupErrors.Clear();
            panels.Refresh(7, true, CancellationToken.None);
            Require(CachedHandles(panels, native).Contains(2),
                "A panel can be rediscovered after a failed lookup");

            native.Add("com.check.event.panel", 3);
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayCreated, 3));
            panels.Refresh(8, true, CancellationToken.None);
            Require(CachedHandles(panels, native).Contains(3),
                "A live event key is admitted ahead of historical log keys");
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayDestroyed, 3));
            Require(!CachedHandles(panels, native).Contains(3),
                "A destroyed overlay is removed from the panel cache immediately");

            native.Add("com.check.pending.panel", 4);
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayCreated, 4));
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayDestroyed, 4));
            native.Lookups.Clear();
            panels.Refresh(9, true, CancellationToken.None);
            Require(!native.Lookups.Contains("com.check.pending.panel") &&
                !native.Lookups.Contains("com.check.event.panel"),
                "Destroyed event keys leave no pending or historical candidate reservation");

            File.Delete(log);
            panels.Refresh(10, true, CancellationToken.None);
            Require(panels.DiscoveryWarnings.Length > 0 && CachedHandles(panels, native).Contains(1) &&
                CachedHandles(panels, native).Contains(2),
                "An unreadable log reports degraded discovery while preserving known live panels");
            native.Add("com.check.recovered.panel", 5);
            File.WriteAllText(log, "[Overlays] Created: com.check.recovered.panel" + Environment.NewLine);
            panels.Refresh(11, true, CancellationToken.None);
            Require(panels.DiscoveryWarnings.Length == 0 && CachedHandles(panels, native).Contains(5),
                "Log recovery clears warnings and admits newly discovered panels");

            native.Lookups.Clear();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool canceled = false;
            try { panels.Refresh(12, true, cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled && native.Lookups.Count == 0,
                "Cancellation stops discovery before native panel lookups");
        }
        finally
        {
            File.Delete(log);
            Directory.Delete(directory);
        }
        EventRetryChecks();
    }

    private static void EventRetryChecks()
    {
        using (var native = new FakeOverlay())
        {
            const string key = "com.check.key.retry";
            native.Add(key, 10);
            native.KeyErrors[10] = EVROverlayError.RequestFailed;
            var panels = EventOnlyPanels(native);
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayCreated, 10));
            panels.Refresh(0, true, CancellationToken.None);
            Require(native.KeyQueries.Contains(10) && !native.Lookups.Contains(key) && CachedHandles(panels, native).Count == 0,
                "A failed event key lookup exposes no unresolved panel");
            native.KeyErrors.Clear();
            panels.Refresh(0.5, true, CancellationToken.None);
            Require(native.KeyQueries.Count(handle => handle == 10) == 2 && CachedHandles(panels, native).Contains(10),
                "An event-only handle retries key resolution and recovers without a log entry");
        }

        using (var native = new FakeOverlay())
        {
            const string key = "com.check.find.retry";
            native.Add(key, 20);
            native.LookupErrors[key] = EVROverlayError.UnknownOverlay;
            var panels = EventOnlyPanels(native);
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayCreated, 20));
            panels.Refresh(0, true, CancellationToken.None);
            Require(native.Lookups.Contains(key) && CachedHandles(panels, native).Count == 0,
                "An event-only panel stays hidden while its overlay lookup fails");
            native.LookupErrors.Clear();
            panels.Refresh(0.5, true, CancellationToken.None);
            Require(native.Lookups.Count(candidate => candidate == key) == 2 && CachedHandles(panels, native).Contains(20),
                "An event-only panel retries a failed overlay lookup without another event or log entry");

            native.LookupErrors[key] = EVROverlayError.RequestFailed;
            panels.Refresh(1, true, CancellationToken.None);
            Require(CachedHandles(panels, native).Count == 0,
                "A cached event-only panel is hidden after its lookup fails");
            native.LookupErrors.Clear();
            panels.Refresh(1.5, true, CancellationToken.None);
            Require(CachedHandles(panels, native).Contains(20),
                "A previously cached event-only panel retains enough identity to recover from a transient lookup failure");
        }

        using (var native = new FakeOverlay())
        {
            const string key = "com.check.expired.event";
            native.Add(key, 30);
            native.LookupErrors[key] = EVROverlayError.UnknownOverlay;
            var panels = EventOnlyPanels(native);
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayCreated, 30));
            for (int pass = 0; pass < 6; pass++) panels.Refresh(pass * 0.5, true, CancellationToken.None);
            Require(native.Lookups.Count(candidate => candidate == key) == 6 && CachedHandles(panels, native).Count == 0,
                "An unresolved event-only key receives exactly six scheduled lookup attempts");
            native.LookupErrors.Clear();
            native.Lookups.Clear();
            panels.Refresh(10, true, CancellationToken.None);
            Require(!native.Lookups.Contains(key) && CachedHandles(panels, native).Count == 0,
                "Expired event retries cannot reserve discovery capacity forever");
        }

        using (var native = new FakeOverlay())
        {
            native.Add("com.check.expired.handle", 31);
            native.KeyErrors[31] = EVROverlayError.RequestFailed;
            var panels = EventOnlyPanels(native);
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayCreated, 31));
            for (int pass = 0; pass < 6; pass++) panels.Refresh(pass * 0.5, true, CancellationToken.None);
            native.KeyErrors.Clear();
            panels.Refresh(10, true, CancellationToken.None);
            Require(native.KeyQueries.Count(handle => handle == 31) == 6 && CachedHandles(panels, native).Count == 0,
                "An unresolved event handle expires after six key-resolution attempts");
        }

        using (var native = new FakeOverlay())
        {
            const string key = "com.check.destroyed.retry";
            native.Add(key, 40);
            native.LookupErrors[key] = EVROverlayError.UnknownOverlay;
            var panels = EventOnlyPanels(native);
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayCreated, 40));
            panels.Refresh(0, true, CancellationToken.None);
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayDestroyed, 40));
            native.LookupErrors.Clear();
            native.Lookups.Clear();
            native.KeyQueries.Clear();
            panels.Refresh(5, true, CancellationToken.None);
            Require(!native.Lookups.Contains(key) && !native.KeyQueries.Contains(40) && CachedHandles(panels, native).Count == 0,
                "Destruction removes unresolved event retries before another native attempt");
        }

        using (var native = new FakeOverlay())
        {
            const string key = "com.check.replaced.handle";
            native.Add(key, 50);
            native.LookupErrors[key] = EVROverlayError.UnknownOverlay;
            var panels = EventOnlyPanels(native);
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayCreated, 50));
            panels.Refresh(0, true, CancellationToken.None);
            native.Add(key, 51);
            native.LookupErrors.Clear();
            panels.Refresh(0.5, true, CancellationToken.None);
            var handles = CachedHandles(panels, native);
            Require(handles.Contains(51) && !handles.Contains(50),
                "A resolved key uses its current live handle rather than the stale event handle");
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayDestroyed, 51));
            native.Lookups.Clear();
            panels.Refresh(1, true, CancellationToken.None);
            Require(!native.Lookups.Contains(key) && CachedHandles(panels, native).Count == 0,
                "Replacement resolution clears the stale event retry before the new handle is destroyed");
        }

        using (var native = new FakeOverlay())
        {
            native.Add("system.desktop.1", 1);
            var panels = EventOnlyPanels(native);
            for (int index = 0; index < 1100; index++)
            {
                string key = $"com.check.overflow.{index:D4}";
                ulong handle = (ulong)index + 100;
                native.Add(key, handle);
                native.LookupErrors[key] = EVROverlayError.UnknownOverlay;
                panels.ObserveEvent(Event(EVREventType.VREvent_OverlayCreated, handle));
            }
            panels.Refresh(0, true, CancellationToken.None);
            Require(native.KeyQueries.Count <= 1024 && !native.KeyQueries.Contains(100) && native.KeyQueries.Contains(1199) &&
                native.Lookups.Count <= 1024 && native.Lookups.Contains("com.check.overflow.1099") &&
                native.Lookups.Take(DashboardPanelDiscovery.BaselineKeys.Count).SequenceEqual(DashboardPanelDiscovery.BaselineKeys) &&
                CachedHandles(panels, native).SetEquals(new ulong[] { 1 }),
                "Pending event overflow keeps the newest handles, protects baseline panels, and bounds native work");
            for (int pass = 1; pass < 6; pass++) panels.Refresh(pass * 0.5, true, CancellationToken.None);
            native.LookupErrors.Clear();
            native.Lookups.Clear();
            panels.Refresh(10, true, CancellationToken.None);
            Require(!native.Lookups.Any(key => key.StartsWith("com.check.overflow.", StringComparison.Ordinal)) &&
                CachedHandles(panels, native).SetEquals(new ulong[] { 1 }),
                "Overflow retries expire after six passes even when they never fit the lookup budget");
        }

        using (var native = new FakeOverlay())
        {
            native.Add("com.check.dashboard.payload", 70);
            var panels = EventOnlyPanels(native);
            panels.ObserveEvent(Event(EVREventType.VREvent_DashboardOverlayCreated, 70));
            panels.Refresh(0, true, CancellationToken.None);
            Require(native.KeyQueries.Count == 0 && !native.Lookups.Contains("com.check.dashboard.payload") &&
                CachedHandles(panels, native).Count == 0,
                "Dashboard creation wakes discovery without treating its union payload as an overlay handle");
        }

        using (var native = new FakeOverlay())
        {
            native.Add("com.check.canceled.retry", 60);
            var panels = EventOnlyPanels(native);
            panels.ObserveEvent(Event(EVREventType.VREvent_OverlayCreated, 60));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool canceled = false;
            try { panels.Refresh(0, true, cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled && native.KeyQueries.Count == 0 && native.Lookups.Count == 0,
                "Cancellation stops pending event resolution before native calls");
            panels.Refresh(0.5, true, CancellationToken.None);
            Require(CachedHandles(panels, native).Contains(60),
                "A canceled refresh does not consume or discard a pending event retry");
        }
    }

    private static DashboardPanels EventOnlyPanels(FakeOverlay native) =>
        new(native.Api, new DashboardPanelDiscovery(Array.Empty<string>()));

    private static HashSet<ulong> CachedHandles(DashboardPanels panels, FakeOverlay native)
    {
        native.VisibilityQueries.Clear();
        panels.TryIntersect(Vector3.Zero, -Vector3.UnitZ, out _);
        return native.VisibilityQueries.ToHashSet();
    }

    private static VREvent_t Event(EVREventType type, ulong handle) => new()
    {
        eventType = (uint)type,
        data = new VREvent_Data_t { overlay = new VREvent_Overlay_t { overlayHandle = handle } }
    };

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    // Exercise the real CVROverlay wrappers without loading or initializing SteamVR.
    private sealed class FakeOverlay : IDisposable
    {
        private readonly Dictionary<string, ulong> handles = new(StringComparer.Ordinal);
        private readonly Dictionary<ulong, string> keys = new();
        private readonly IVROverlay table;
        private readonly IntPtr pointer;
        internal readonly List<string> Lookups = new();
        internal readonly List<ulong> KeyQueries = new();
        internal readonly List<ulong> VisibilityQueries = new();
        internal readonly Dictionary<string, EVROverlayError> LookupErrors = new(StringComparer.Ordinal);
        internal readonly Dictionary<ulong, EVROverlayError> KeyErrors = new();
        internal CVROverlay Api { get; }

        internal FakeOverlay()
        {
            table = new IVROverlay
            {
                FindOverlay = Find,
                GetOverlayKey = Key,
                GetOverlaySortOrder = Sort,
                GetOverlayInputMethod = Input,
                GetOverlayTransformType = Transform,
                IsOverlayVisible = Visible,
                GetOverlayAlpha = Alpha,
                ComputeOverlayIntersection = Intersect
            };
            pointer = Marshal.AllocHGlobal(Marshal.SizeOf<IVROverlay>());
            Marshal.StructureToPtr(table, pointer, false);
            Api = new CVROverlay(pointer);
        }

        internal void Add(string key, ulong handle)
        {
            handles[key] = handle;
            keys[handle] = key;
        }

        private EVROverlayError Find(IntPtr keyPointer, ref ulong handle)
        {
            string key = Marshal.PtrToStringUTF8(keyPointer);
            Lookups.Add(key);
            if (LookupErrors.TryGetValue(key, out var error)) return error;
            return handles.TryGetValue(key, out handle) ? EVROverlayError.None : EVROverlayError.UnknownOverlay;
        }

        private uint Key(ulong handle, StringBuilder value, uint size, ref EVROverlayError error)
        {
            KeyQueries.Add(handle);
            if (KeyErrors.TryGetValue(handle, out error)) return 0;
            if (!keys.TryGetValue(handle, out string key))
            {
                error = EVROverlayError.UnknownOverlay;
                return 0;
            }
            value.Append(key);
            error = EVROverlayError.None;
            return (uint)key.Length + 1;
        }

        private static EVROverlayError Sort(ulong handle, ref uint sort)
        {
            sort = 0;
            return EVROverlayError.None;
        }

        private static EVROverlayError Input(ulong handle, ref VROverlayInputMethod input)
        {
            input = VROverlayInputMethod.Mouse;
            return EVROverlayError.None;
        }

        private static EVROverlayError Transform(ulong handle, ref VROverlayTransformType transform)
        {
            transform = VROverlayTransformType.VROverlayTransform_Absolute;
            return EVROverlayError.None;
        }

        private bool Visible(ulong handle)
        {
            VisibilityQueries.Add(handle);
            return keys.ContainsKey(handle);
        }

        private static EVROverlayError Alpha(ulong handle, ref float alpha)
        {
            alpha = 1;
            return EVROverlayError.None;
        }

        private static bool Intersect(ulong handle, ref VROverlayIntersectionParams_t ray,
            ref VROverlayIntersectionResults_t result)
        {
            result = new VROverlayIntersectionResults_t
            {
                vPoint = new HmdVector3_t
                {
                    v0 = ray.vSource.v0 + ray.vDirection.v0,
                    v1 = ray.vSource.v1 + ray.vDirection.v1,
                    v2 = ray.vSource.v2 + ray.vDirection.v2
                },
                vNormal = new HmdVector3_t { v0 = -ray.vDirection.v0, v1 = -ray.vDirection.v1, v2 = -ray.vDirection.v2 },
                vUVs = new HmdVector2_t { v0 = 0.5f, v1 = 0.5f },
                fDistance = 1
            };
            return true;
        }

        public void Dispose()
        {
            Marshal.DestroyStructure<IVROverlay>(pointer);
            Marshal.FreeHGlobal(pointer);
            GC.KeepAlive(table);
        }
    }
}
