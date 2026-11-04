namespace ICSharpCode.SharpDevelop.Designer.Devices;

public enum SessionStepState { Pending, Active, Done, Failed }

public sealed record SessionStep(string Label, SessionStepState State);

/// <summary>Turns a session status into the four rows of the canvas progress card:
/// device, host, connection, first frame. Pure, so every designer shows the same card.</summary>
public static class DesignerSessionSteps
{
    static readonly string[] Labels = ["Device ready", "Design host started", "Connected", "First frame rendered"];

    static int Group(DesignerSessionPhase phase) => phase switch
    {
        DesignerSessionPhase.Preflight or DesignerSessionPhase.ProvisioningDevice or DesignerSessionPhase.BootingDevice => 0,
        DesignerSessionPhase.InstallingHost or DesignerSessionPhase.LaunchingHost => 1,
        DesignerSessionPhase.Connecting => 2,
        DesignerSessionPhase.Rendering => 3,
        _ => -1,
    };

    public static IReadOnlyList<SessionStep> For(DesignerSessionStatus status)
    {
        // Ready: everything is done. Idle/Stale: nothing is in flight, so nothing is highlighted.
        var done = status.Phase == DesignerSessionPhase.Ready ? Labels.Length : 0;
        var active = -1;
        var failed = -1;
        switch (status.Phase)
        {
            case DesignerSessionPhase.Failed:
                // A failure with no starting stage (nothing was running, or the stage was Idle,
                // Ready or Stale) is shown on the first row: the preview never got going.
                failed = status.FailedStage is { } stage ? Math.Max(Group(stage), 0) : 0;
                done = failed;
                break;
            case var phase when status.InProgress:
                active = Group(phase);
                done = active;
                break;
        }

        var steps = new SessionStep[Labels.Length];
        for (var i = 0; i < steps.Length; i++)
        {
            var state = i < done ? SessionStepState.Done
                : i == failed ? SessionStepState.Failed
                : i == active ? SessionStepState.Active
                : SessionStepState.Pending;
            steps[i] = new SessionStep(Labels[i], state);
        }

        return steps;
    }
}
