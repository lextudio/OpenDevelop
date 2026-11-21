using ICSharpCode.SharpDevelop;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The Android SDK and device windows use this to keep one busy indicator honest while a slow load can be started again
/// before the previous one finished: an older run must not clear the indicator, and only the newest run publishes.
/// </summary>
public sealed class LoadTrackerTests
{
	[Fact]
	public void SingleRun_IsCurrent_AndEndingItLeavesNothingInFlight()
	{
		var tracker = new LoadTracker();
		int run = tracker.Begin();

		Assert.True(tracker.IsCurrent(run));
		Assert.True(tracker.IsBusy);
		Assert.True(tracker.End(run));
		Assert.False(tracker.IsBusy);
	}

	[Fact]
	public void OverlappingRuns_OnlyTheNewestIsCurrent_AndBusyEndsWithTheLast()
	{
		var tracker = new LoadTracker();
		int first = tracker.Begin();
		int second = tracker.Begin();

		Assert.False(tracker.IsCurrent(first));
		Assert.True(tracker.IsCurrent(second));

		// The older run finishing must not clear the busy state of the newer one.
		Assert.False(tracker.End(first));
		Assert.True(tracker.IsBusy);

		Assert.True(tracker.End(second));
		Assert.False(tracker.IsBusy);
	}

	[Fact]
	public void EndingAnAlreadyEndedRun_IsIgnored()
	{
		var tracker = new LoadTracker();
		int run = tracker.Begin();
		Assert.True(tracker.End(run));
		Assert.False(tracker.End(run));
		Assert.False(tracker.IsBusy);
	}

	[Fact]
	public void StaleTokenAfterANewRunStarted_CannotClearTheNewRunsBusyState()
	{
		// The case End must be token-aware for: a stale finally runs after a newer run has begun.
		var tracker = new LoadTracker();
		int first = tracker.Begin();
		Assert.True(tracker.End(first));

		int second = tracker.Begin();
		Assert.False(tracker.End(first));   // stale token: must not count as the second run ending
		Assert.True(tracker.IsBusy);

		Assert.True(tracker.End(second));
		Assert.False(tracker.IsBusy);
	}
}
