#nullable enable

using System.Numerics;
using System.Text;
using Valve.VR;

namespace SteamDashET;

internal readonly record struct PanelHit(string Key, ulong Handle, Vector3 Point, Vector3 Normal,
    float Distance);

// All instance methods run on the OpenVR worker. Keys are hints until FindOverlay resolves them.
internal sealed class DashboardPanels
{
    private const double NormalRefreshSeconds = 1;
    private const double TransitionRefreshSeconds = 0.5;
    private const double TransitionWindowSeconds = 3;
    private const int MaximumCandidates = 1024;
    private const int EventRetryAttempts = 6;
    private const float DistanceTieMeters = 0.001f;
    private const double GeometryToleranceMeters = 0.02;
    private readonly CVROverlay api;
    private readonly DashboardPanelDiscovery discovery;
    private readonly LinkedList<EventCandidate> eventCandidates = new();
    private readonly Dictionary<ulong, LinkedListNode<EventCandidate>> eventCandidateNodes = new();
    private readonly Dictionary<string, PanelState> panels = new(StringComparer.Ordinal);
    private bool sampledDashboard;
    private bool dashboardOpen;
    private bool refreshRequested;
    private double nextRefresh;
    private double transitionUntil;

    public DashboardPanels(CVROverlay api) : this(api, new DashboardPanelDiscovery()) { }

    internal DashboardPanels(CVROverlay api, DashboardPanelDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(discovery);
        this.api = api;
        this.discovery = discovery;
    }

    public string[] DiscoveryWarnings => discovery.Warnings;

    public void ObserveEvent(VREvent_t value)
    {
        var type = (EVREventType)value.eventType;
        if (type is EVREventType.VREvent_OverlayCreated or
            EVREventType.VREvent_OverlayNameChanged or EVREventType.VREvent_OverlayDestroyed)
        {
            ulong handle = value.data.overlay.overlayHandle;
            if (type == EVREventType.VREvent_OverlayDestroyed)
            {
                foreach (string key in panels.Where(pair => pair.Value.Handle == handle)
                    .Select(pair => pair.Key).ToArray())
                    panels.Remove(key);
                RemoveEventCandidate(handle);
            }
            else if (handle != 0)
                RememberEventCandidate(handle);
            refreshRequested = true;
        }
        else if (type is EVREventType.VREvent_DashboardOverlayCreated or
            EVREventType.VREvent_DashboardActivated or EVREventType.VREvent_DashboardDeactivated or
            EVREventType.VREvent_StartDashboard or EVREventType.VREvent_ResetDashboard or
            EVREventType.VREvent_OverlayShown or EVREventType.VREvent_OverlayHidden or
            EVREventType.VREvent_OverlayClosed or EVREventType.VREvent_OverlayFocusChanged or
            EVREventType.VREvent_OverlayGamepadFocusGained or EVREventType.VREvent_OverlayGamepadFocusLost or
            EVREventType.VREvent_DesktopViewUpdating or EVREventType.VREvent_DesktopViewReady or
            EVREventType.VREvent_DesktopMightBeVisible or EVREventType.VREvent_DesktopMightBeHidden or
            EVREventType.VREvent_SceneApplicationChanged)
        {
            // These events wake discovery; their union payload is not an overlay identity contract.
            refreshRequested = true;
        }
    }

    public void Refresh(double now, bool dashboardOpen, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        bool edge = !sampledDashboard || this.dashboardOpen != dashboardOpen;
        this.dashboardOpen = dashboardOpen;
        sampledDashboard = true;
        if (edge || refreshRequested)
        {
            transitionUntil = now + TransitionWindowSeconds;
            if (edge) nextRefresh = now;
            else nextRefresh = Math.Min(nextRefresh, now + TransitionRefreshSeconds);
        }
        if (now < nextRefresh) return;

        refreshRequested = false;
        IReadOnlyList<string> discovered = discovery.ReadCandidates(token);
        ResolveEventKeys(token);
        // Rebuild the bounded work list each time. Historical names never reserve capacity forever.
        string[] candidates = DashboardPanelDiscovery.BaselineKeys
            .Concat(eventCandidates.Where(candidate => candidate.Key is not null).Select(candidate => candidate.Key!))
            .Concat(panels.Keys.OrderBy(key => key, StringComparer.Ordinal))
            .Concat(discovered)
            .Where(IsPanelCandidate)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumCandidates)
            .ToArray();
        var refreshed = new Dictionary<string, PanelState>(StringComparer.Ordinal);
        foreach (string key in candidates)
        {
            token.ThrowIfCancellationRequested();
            ulong handle = 0;
            var lookup = api.FindOverlay(key, ref handle);
            if (lookup != EVROverlayError.None || handle == 0)
            {
                // A failed lookup cannot keep a stale handle in the next snapshot.
                if (panels.TryGetValue(key, out var failedPanel) && !eventCandidateNodes.ContainsKey(failedPanel.Handle))
                    RememberEventCandidate(failedPanel.Handle, key);
                continue;
            }

            // Optional getters can fail on a live handle. Keep its identity and fail eligibility conservatively.
            uint sort = 0;
            if (api.GetOverlaySortOrder(handle, ref sort) != EVROverlayError.None) sort = 0;
            var inputMethod = VROverlayInputMethod.None;
            if (api.GetOverlayInputMethod(handle, ref inputMethod) != EVROverlayError.None)
                inputMethod = VROverlayInputMethod.None;
            var transform = VROverlayTransformType.VROverlayTransform_Absolute;
            var transformError = api.GetOverlayTransformType(handle, ref transform);
            bool cursor = transformError == EVROverlayError.None
                ? transform == VROverlayTransformType.VROverlayTransform_Cursor
                : panels.TryGetValue(key, out var previous) && previous.Handle == handle && previous.Cursor;
            if (transformError != EVROverlayError.None && !IsKnownContentKey(key))
                inputMethod = VROverlayInputMethod.None;
            refreshed[key] = new PanelState(handle, sort, inputMethod, cursor);
        }
        panels.Clear();
        foreach (var pair in refreshed) panels.Add(pair.Key, pair.Value);
        foreach (var candidate in eventCandidates.ToArray())
        {
            // A resolved key may now belong to a replacement handle. Only FindOverlay's live handle is cached.
            if ((candidate.Key is not null && refreshed.ContainsKey(candidate.Key)) || --candidate.AttemptsRemaining == 0)
                RemoveEventCandidate(candidate.Handle);
        }
        nextRefresh = now + (now < transitionUntil || eventCandidates.Count > 0
            ? TransitionRefreshSeconds : NormalRefreshSeconds);
    }

    public bool TryIntersect(Vector3 origin, Vector3 unitDirection, out PanelHit hit)
    {
        hit = default;
        if (!dashboardOpen || !IsValidRay(origin, unitDirection)) return false;
        var parameters = new VROverlayIntersectionParams_t
        {
            eOrigin = ETrackingUniverseOrigin.TrackingUniverseStanding,
            vSource = NativeVector(origin),
            vDirection = NativeVector(unitDirection)
        };
        var hits = new List<(PanelHit Hit, uint Sort)>();
        float nearestDistance = float.PositiveInfinity;
        foreach (var (key, panel) in panels)
        {
            if (panel.Cursor) continue;
            // Visibility and transparency may change between cached identity refreshes.
            bool visible = api.IsOverlayVisible(panel.Handle);
            float alpha = float.NaN;
            if (!visible || api.GetOverlayAlpha(panel.Handle, ref alpha) != EVROverlayError.None ||
                !IsEligiblePanel(key, visible, alpha, panel.InputMethod))
                continue;

            VROverlayIntersectionResults_t result = default;
            if (!api.ComputeOverlayIntersection(panel.Handle, ref parameters, ref result) ||
                !IsValidHit(origin, unitDirection, result))
                continue;
            var candidate = new PanelHit(key, panel.Handle, Vector(result.vPoint),
                Vector3.Normalize(Vector(result.vNormal)), result.fDistance);
            hits.Add((candidate, panel.Sort));
            nearestDistance = MathF.Min(nearestDistance, candidate.Distance);
        }

        bool found = false;
        uint selectedSort = 0;
        // Anchor the tie group to the minimum distance, so a chain of close hits cannot drift farther away.
        foreach (var candidate in hits)
        {
            if ((double)candidate.Hit.Distance - nearestDistance > DistanceTieMeters) continue;
            if (!found || IsPreferredHit(candidate.Hit, candidate.Sort, hit, selectedSort))
            {
                hit = candidate.Hit;
                selectedSort = candidate.Sort;
                found = true;
            }
        }
        // Sort order breaks near-distance ties; this does not establish agreement with rendered stacking.
        return found;
    }

    public static bool IsPanelCandidate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length >= OpenVR.k_unVROverlayMaxKeyLength ||
            key.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            key.StartsWith("steamdash.et.", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ETCalCursor", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ETCalTarget", StringComparison.OrdinalIgnoreCase))
            return false;
        if (HasHelperComponent(key)) return false;
        if (key.StartsWith("system.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("valve.steam.", StringComparison.OrdinalIgnoreCase))
            return IsKnownContentKey(key);
        // A namespaced foreign key plus Mouse input gives evidence of an ordinary app UI.
        return key.IndexOf('.') > 0 && !key.EndsWith('.');
    }

    internal static bool IsEligiblePanel(string? key, bool visible, float alpha, VROverlayInputMethod inputMethod) =>
        IsPanelCandidate(key) && visible && float.IsFinite(alpha) && alpha > 0 &&
        (IsKnownContentKey(key!) || inputMethod == VROverlayInputMethod.Mouse);

    internal static bool IsValidHit(Vector3 origin, Vector3 unitDirection, VROverlayIntersectionResults_t result)
    {
        var point = Vector(result.vPoint);
        var normal = Vector(result.vNormal);
        if (!IsValidRay(origin, unitDirection) || !Finite(point) || !Finite(normal) ||
            !float.IsFinite(result.vUVs.v0) || !float.IsFinite(result.vUVs.v1) ||
            !float.IsFinite(result.fDistance) || result.fDistance <= 0 || Math.Abs(Length(normal) - 1) > 0.05)
            return false;
        double x = (double)point.X - origin.X;
        double y = (double)point.Y - origin.Y;
        double z = (double)point.Z - origin.Z;
        double distanceResidual = Math.Abs(Math.Sqrt(x * x + y * y + z * z) - result.fDistance);
        x -= (double)unitDirection.X * result.fDistance;
        y -= (double)unitDirection.Y * result.fDistance;
        z -= (double)unitDirection.Z * result.fDistance;
        return distanceResidual <= GeometryToleranceMeters &&
            Math.Sqrt(x * x + y * y + z * z) <= GeometryToleranceMeters;
    }

    internal static bool IsPreferredHit(PanelHit candidate, uint candidateSort, PanelHit current, uint currentSort)
    {
        double difference = (double)candidate.Distance - current.Distance;
        if (difference < -DistanceTieMeters) return true;
        if (difference > DistanceTieMeters) return false;
        if (candidateSort != currentSort) return candidateSort > currentSort;
        int keyOrder = string.CompareOrdinal(candidate.Key, current.Key);
        return keyOrder < 0 || keyOrder == 0 && candidate.Handle < current.Handle;
    }

    private void RememberEventCandidate(ulong handle, string? key = null)
    {
        RemoveEventCandidate(handle);
        eventCandidateNodes[handle] = eventCandidates.AddFirst(new EventCandidate(handle, key));
        if (eventCandidates.Count > MaximumCandidates)
        {
            var oldest = eventCandidates.Last!;
            RemoveEventCandidate(oldest.Value.Handle);
        }
    }

    private void ResolveEventKeys(CancellationToken token)
    {
        foreach (var candidate in eventCandidates.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Key is not null) continue;
            var error = EVROverlayError.None;
            var key = new StringBuilder((int)OpenVR.k_unVROverlayMaxKeyLength);
            api.GetOverlayKey(candidate.Handle, key, (uint)key.Capacity, ref error);
            if (error != EVROverlayError.None) continue;
            candidate.Key = key.ToString();
            if (!IsPanelCandidate(candidate.Key)) RemoveEventCandidate(candidate.Handle);
        }
    }

    private void RemoveEventCandidate(ulong handle)
    {
        if (eventCandidateNodes.Remove(handle, out var node)) eventCandidates.Remove(node);
    }

    private static bool IsKnownContentKey(string key) =>
        key.Equals("system.systemui", StringComparison.Ordinal) ||
        key.Equals("system.desktop", StringComparison.Ordinal) ||
        key.StartsWith("system.desktop.", StringComparison.Ordinal) &&
            uint.TryParse(key["system.desktop.".Length..], out uint desktop) && desktop > 0 ||
        HasSingleSuffix(key, "system.window.") ||
        key.Equals("system.keyboard", StringComparison.Ordinal) ||
        key.Equals("system.settings", StringComparison.Ordinal) ||
        key.Equals("system.vrwebhelper.controllerbinding", StringComparison.Ordinal) ||
        key.Equals("system.messageoverlay", StringComparison.Ordinal) ||
        key.Equals("system.dashboard", StringComparison.Ordinal) ||
        key.Equals("system.dashboard.quicklaunch", StringComparison.Ordinal) ||
        key.Equals("system.dashboard.nowplaying", StringComparison.Ordinal) ||
        HasSingleSuffix(key, "valve.steam.desktopgame.") &&
            key["valve.steam.desktopgame.".Length..].All(char.IsAsciiDigit) ||
        key.Equals("valve.steam.gamepadui.main", StringComparison.Ordinal) ||
        key.Equals("valve.steam.gamepadui.bar", StringComparison.Ordinal) ||
        key.Equals("valve.steam.gamepadui.keyboard", StringComparison.Ordinal) ||
        HasTwoSuffixes(key, "valve.steam.gamepadui.appoverlay.") ||
        HasSingleSuffix(key, "valve.steam.gamepadui.barpopup.") ||
        HasSingleSuffix(key, "valve.steam.gamepadui.frame.menu.") ||
        key.StartsWith("valve.steam.browserview_", StringComparison.Ordinal) &&
            key.Length > "valve.steam.browserview_".Length;

    private static bool HasSingleSuffix(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length &&
        key.IndexOf('.', prefix.Length) < 0;

    private static bool HasTwoSuffixes(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int separator = key.IndexOf('.', prefix.Length);
        return separator > prefix.Length && separator < key.Length - 1 &&
            key.IndexOf('.', separator + 1) < 0;
    }

    private static bool HasHelperComponent(string key)
    {
        foreach (string component in key.Split(new[] { '.', '_', '-', '/' }, StringSplitOptions.RemoveEmptyEntries))
            if (component.Equals("cursor", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("pointer", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("input", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("helper", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("thumbnail", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("tooltip", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("toast", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("headsetview", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("notifications", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("volumelevel", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsValidRay(Vector3 origin, Vector3 direction) =>
        Finite(origin) && Finite(direction) && Math.Abs(Length(direction) - 1) <= 0.01;
    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static double Length(Vector3 value) => Math.Sqrt(
        (double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);
    private static Vector3 Vector(HmdVector3_t value) => new(value.v0, value.v1, value.v2);
    private static HmdVector3_t NativeVector(Vector3 value) => new() { v0 = value.X, v1 = value.Y, v2 = value.Z };
    private readonly record struct PanelState(ulong Handle, uint Sort, VROverlayInputMethod InputMethod, bool Cursor);
    private sealed class EventCandidate(ulong handle, string? key)
    {
        internal readonly ulong Handle = handle;
        internal string? Key = key;
        internal int AttemptsRemaining = EventRetryAttempts;
    }
}
