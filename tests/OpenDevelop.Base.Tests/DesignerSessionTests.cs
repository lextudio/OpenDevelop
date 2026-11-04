using ICSharpCode.SharpDevelop.Designer.Devices;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerSessionTests
{
    [Fact]
    public void StartsIdleAndTracksPhases()
    {
        var session = new DesignerSession();
        Assert.Equal(DesignerSessionPhase.Idle, session.Status.Phase);
        Assert.False(session.Status.InProgress);

        session.Enter(DesignerSessionPhase.BootingDevice);
        Assert.True(session.Status.InProgress);

        session.Enter(DesignerSessionPhase.Ready);
        Assert.Equal(DesignerSessionPhase.Ready, session.Status.Phase);
        Assert.False(session.Status.InProgress);
    }

    [Fact]
    public void ARepeatedPhaseKeepsItsStartTimeAndRaisesNothing()
    {
        var now = DateTimeOffset.Parse("2026-11-03T10:00:00Z");
        var session = new DesignerSession(() => now);
        var raised = 0;
        session.Changed += (_, _) => raised++;

        session.Enter(DesignerSessionPhase.BootingDevice);
        var since = session.Status.Since;
        now = now.AddSeconds(30);
        session.Enter(DesignerSessionPhase.BootingDevice);

        Assert.Equal(1, raised);
        Assert.Equal(since, session.Status.Since);
        Assert.Equal(TimeSpan.FromSeconds(30), session.Status.Elapsed(now));
    }

    [Fact]
    public void FailureRemembersTheStageThatWasRunning()
    {
        var session = new DesignerSession();
        session.Enter(DesignerSessionPhase.InstallingHost);
        session.Fail("simctl install failed");

        Assert.Equal(DesignerSessionPhase.Failed, session.Status.Phase);
        Assert.Equal(DesignerSessionPhase.InstallingHost, session.Status.FailedStage);
        Assert.Equal("simctl install failed", session.Status.Reason);

        // A second failure keeps the original stage rather than reporting "failed during Failed".
        session.Fail("again");
        Assert.Equal(DesignerSessionPhase.InstallingHost, session.Status.FailedStage);
        Assert.Equal("again", session.Status.Reason);
    }

    [Fact]
    public void FailedCannotBeEnteredWithoutAReason()
    {
        Assert.Throws<ArgumentException>(() => new DesignerSession().Enter(DesignerSessionPhase.Failed));
    }

    [Fact]
    public void ResetClearsAFailure()
    {
        var session = new DesignerSession();
        session.Fail("x");
        session.Reset();
        Assert.Equal(DesignerSessionPhase.Idle, session.Status.Phase);
        Assert.Null(session.Status.Reason);
        Assert.Null(session.Status.FailedStage);
    }

    [Fact]
    public void DescribeNamesTheDevice()
    {
        Assert.Equal("Starting iPhone 17…", DesignerSessionStatus.Describe(DesignerSessionPhase.BootingDevice, "iPhone 17"));
        Assert.Equal("", DesignerSessionStatus.Describe(DesignerSessionPhase.Idle, "iPhone 17"));
    }
}

public sealed class DesignerSessionStepsTests
{
    static string Render(DesignerSession session) =>
        string.Join(",", DesignerSessionSteps.For(session.Status).Select(s => s.State.ToString()[0]));

    [Fact]
    public void ProgressMarksEarlierStepsDoneAndTheCurrentOneActive()
    {
        var session = new DesignerSession();
        Assert.Equal("P,P,P,P", Render(session));
        session.Enter(DesignerSessionPhase.BootingDevice);
        Assert.Equal("A,P,P,P", Render(session));
        session.Enter(DesignerSessionPhase.LaunchingHost);
        Assert.Equal("D,A,P,P", Render(session));
        session.Enter(DesignerSessionPhase.Rendering);
        Assert.Equal("D,D,D,A", Render(session));
        session.Enter(DesignerSessionPhase.Ready);
        Assert.Equal("D,D,D,D", Render(session));
    }

    [Fact]
    public void FailureMarksTheFailedStepAndKeepsEarlierOnesDone()
    {
        var session = new DesignerSession();
        session.Enter(DesignerSessionPhase.InstallingHost);
        session.Fail("install failed");
        Assert.Equal("D,F,P,P", Render(session));

        var early = new DesignerSession();
        early.Fail("nothing was running");
        Assert.Equal("F,P,P,P", Render(early));
    }

    [Fact]
    public void StaleShowsNothingInFlight()
    {
        var session = new DesignerSession();
        session.Enter(DesignerSessionPhase.Stale);
        Assert.Equal("P,P,P,P", Render(session));
    }
}
