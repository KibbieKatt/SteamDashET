namespace SteamDashET;

internal sealed record VrStatus(bool IsRunning, string EyeTracking, string Dashboard, string Detail,
    bool EyeTrackingReady = false, bool DashboardOpen = false, bool DotVisible = false,
    string DiscoveryWarning = "")
{
    internal static VrStatus Stopped => new(false, "Stopped", "Disconnected", "Press Start to connect to SteamVR.");
}

internal interface IVrSession : IDisposable
{
    // The session paces updates and uses this token to interrupt fallback waits.
    VrStatus Poll(CancellationToken token);
}

// Only the worker owns the native session. The UI reads immutable snapshots and never makes native VR calls.
internal sealed class VrController
{
    private readonly Func<IVrSession> createSession;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource cancellation;
    private Task worker;
    private VrStatus status = VrStatus.Stopped;
    public VrStatus Status => Volatile.Read(ref status);
    internal CalibrationService Calibration { get; }

    public VrController(AppSettings settings)
    {
        Calibration = new CalibrationService(interpolationMethod: settings.InterpolationMethod);
        createSession = () => new SteamVrSession(settings, Calibration);
    }
    internal VrController(Func<IVrSession> createSession)
    {
        this.createSession = createSession;
        Calibration = new CalibrationService(CalibrationProfile.Empty(""), _ => { });
    }

    public async Task StartAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (worker != null) return;
            cancellation = new CancellationTokenSource();
            Volatile.Write(ref status, new(true, "Connecting…", "Connecting…", "Connecting to SteamVR."));
            CancellationToken token = cancellation.Token;
            worker = Task.Run(() => Run(token));
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (worker != null)
            {
                cancellation.Cancel();
                await worker.ConfigureAwait(false);
                worker = null;
                cancellation.Dispose();
                cancellation = null;
            }
            Volatile.Write(ref status, VrStatus.Stopped);
            Calibration.Finish();
        }
        finally { lifecycle.Release(); }
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using IVrSession session = createSession();
                while (!token.IsCancellationRequested)
                {
                    Volatile.Write(ref status, session.Poll(token));
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    Volatile.Write(ref status, new(true, "Unavailable", "Disconnected", ex.Message + " Retrying…"));
            }
            if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2))) break;
        }
    }
}
