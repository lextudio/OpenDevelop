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
