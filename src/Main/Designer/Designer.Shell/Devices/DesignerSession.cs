namespace ICSharpCode.SharpDevelop.Designer.Devices;

/// <summary>Where a designer's connection to its preview host is, from "nothing started" to
/// "frame on screen". Shared so every designer (MAUI, Uno, ...) reports the same vocabulary.</summary>
public enum DesignerSessionPhase
{
    Idle,
    Preflight,
    ProvisioningDevice,
    BootingDevice,
    InstallingHost,
    LaunchingHost,
    Connecting,
    Rendering,
    Ready,
    /// <summary>The host or its device went away after being ready; a reconnect is pending or possible.</summary>
    Stale,
    Failed,
}

/// <summary>An immutable snapshot of a session: the phase, the stage that failed (if any), a
/// plain-language reason and how long the current phase has been running.</summary>
public sealed record DesignerSessionStatus(
    DesignerSessionPhase Phase,
    string? Reason,
    DesignerSessionPhase? FailedStage,
    DateTimeOffset Since)
{
    public static DesignerSessionStatus Idle { get; } = new(DesignerSessionPhase.Idle, null, null, DateTimeOffset.UtcNow);

    /// <summary>True while the session is moving toward Ready (shows progress, can be cancelled).</summary>
    public bool InProgress => Phase is >= DesignerSessionPhase.Preflight and <= DesignerSessionPhase.Rendering;

    public TimeSpan Elapsed(DateTimeOffset now) => now - Since;

    /// <summary>The one-line message the canvas shows for a phase, e.g. "Starting simulator iPhone 16 Pro…".</summary>
    public static string Describe(DesignerSessionPhase phase, string deviceName) => phase switch
    {
        DesignerSessionPhase.Idle => "",
        DesignerSessionPhase.Preflight => "Checking the environment…",
        DesignerSessionPhase.ProvisioningDevice => $"Preparing {deviceName}…",
        DesignerSessionPhase.BootingDevice => $"Starting {deviceName}…",
        DesignerSessionPhase.InstallingHost => $"Installing the design host on {deviceName}…",
        DesignerSessionPhase.LaunchingHost => $"Launching the design host on {deviceName}…",
        DesignerSessionPhase.Connecting => $"Connecting to {deviceName}…",
        DesignerSessionPhase.Rendering => "Rendering the first frame…",
        DesignerSessionPhase.Ready => "Ready",
        DesignerSessionPhase.Stale => $"{deviceName} stopped; reconnecting…",
        DesignerSessionPhase.Failed => "Could not start the preview",
        _ => phase.ToString(),
    };
}

/// <summary>
/// Tracks one designer session. Thread-safe (phases are reported from background tasks);
/// <see cref="Changed"/> is raised on the reporting thread after each real change, and a phase that
/// repeats is ignored so a progress loop cannot spam the UI.
/// </summary>
public sealed class DesignerSession
{
    readonly object gate = new();
    readonly Func<DateTimeOffset> clock;
    DesignerSessionStatus status;

    public DesignerSession(Func<DateTimeOffset>? clock = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        status = new DesignerSessionStatus(DesignerSessionPhase.Idle, null, null, this.clock());
    }

    public event EventHandler<DesignerSessionStatus>? Changed;

    public DesignerSessionStatus Status
    {
        get { lock (gate) return status; }
    }

    /// <summary>Moves to <paramref name="phase"/>. Failed must go through <see cref="Fail"/>.</summary>
    public void Enter(DesignerSessionPhase phase)
    {
        if (phase == DesignerSessionPhase.Failed)
            throw new ArgumentException("Use Fail to record a failure and its reason.", nameof(phase));
        Publish(current => current.Phase == phase ? null : new DesignerSessionStatus(phase, null, null, clock()));
    }

    /// <summary>Records a failure at the stage that was running (or <see cref="DesignerSessionPhase.Idle"/>
    /// when nothing was).</summary>
    public void Fail(string reason) =>
        Publish(current => new DesignerSessionStatus(
            DesignerSessionPhase.Failed,
            reason,
            current.Phase == DesignerSessionPhase.Failed ? current.FailedStage : current.Phase,
            clock()));

    /// <summary>Back to Idle (a target switch discards the old session's outcome).</summary>
    public void Reset() => Enter(DesignerSessionPhase.Idle);

    void Publish(Func<DesignerSessionStatus, DesignerSessionStatus?> next)
    {
        DesignerSessionStatus? changed;
        lock (gate)
        {
            changed = next(status);
            if (changed == null)
                return;
            status = changed;
        }

        Changed?.Invoke(this, changed);
    }
}
