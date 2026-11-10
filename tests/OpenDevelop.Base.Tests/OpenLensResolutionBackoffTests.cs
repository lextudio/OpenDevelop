using System;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// <see cref="OpenLensResolutionBackoff"/> is what stops the editor from re-resolving an item its
/// provider keeps returning unresolved (a document whose language service never becomes ready).
/// Before it, every unresolved answer redrew the view and the redraw started the next resolution at
/// once, ~300 times a second on the UI thread, which hung OpenDevelop 5.7.3.
/// </summary>
public class OpenLensResolutionBackoffTests
{
	static readonly DateTime Start = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

	[Fact]
	public void UnknownItem_CanResolveAtOnce()
	{
		var backoff = new OpenLensResolutionBackoff();

		Assert.True(backoff.CanResolve("a", "references", Start));
	}

	[Fact]
	public void UnresolvedItem_WaitsForItsDelay_ThenCanResolve()
	{
		var backoff = new OpenLensResolutionBackoff();

		var delay = backoff.RecordUnresolved("a", "references", Start);

		Assert.Equal(OpenLensResolutionBackoff.InitialDelay, delay);
		// The immediate retry that used to spin is refused...
		Assert.False(backoff.CanResolve("a", "references", Start));
		Assert.False(backoff.CanResolve("a", "references", Start + delay - TimeSpan.FromMilliseconds(1)));
		// ...and the item is tried again once its delay has passed, so it still resolves eventually.
		Assert.True(backoff.CanResolve("a", "references", Start + delay));
	}

	[Fact]
	public void ConsecutiveMisses_DoubleTheDelay_UpToTheMaximum()
	{
		var backoff = new OpenLensResolutionBackoff();
		var now = Start;
		var delays = new TimeSpan[10];
		for (int i = 0; i < delays.Length; i++) {
			delays[i] = backoff.RecordUnresolved("a", "references", now);
			now += delays[i];
		}

		Assert.Equal(TimeSpan.FromMilliseconds(250), delays[0]);
		Assert.Equal(TimeSpan.FromMilliseconds(500), delays[1]);
		Assert.Equal(TimeSpan.FromMilliseconds(1000), delays[2]);
		Assert.Equal(OpenLensResolutionBackoff.MaximumDelay, delays[^1]);
		for (int i = 1; i < delays.Length; i++)
			Assert.True(delays[i] >= delays[i - 1]);
	}

	[Fact]
	public void LongUnresolvedItem_StaysAtTheMaximumWithoutOverflow()
	{
		Assert.Equal(OpenLensResolutionBackoff.MaximumDelay, OpenLensResolutionBackoff.GetDelay(int.MaxValue));
	}

	[Fact]
	public void Resolved_ForgetsTheMisses()
	{
		var backoff = new OpenLensResolutionBackoff();
		backoff.RecordUnresolved("a", "references", Start);
		backoff.RecordUnresolved("a", "references", Start);

		backoff.RecordResolved("a", "references");

		Assert.True(backoff.CanResolve("a", "references", Start));
		Assert.Equal(OpenLensResolutionBackoff.InitialDelay, backoff.RecordUnresolved("a", "references", Start));
	}

	[Fact]
	public void Reset_LetsEveryWaitingItemResolveAtOnce()
	{
		var backoff = new OpenLensResolutionBackoff();
		backoff.RecordUnresolved("a", "references", Start);
		backoff.RecordUnresolved("b", "implementations", Start);

		backoff.Reset();

		Assert.True(backoff.CanResolve("a", "references", Start));
		Assert.True(backoff.CanResolve("b", "implementations", Start));
	}

	[Fact]
	public void NextRetry_NoWaitingItem_NothingToArm()
	{
		var backoff = new OpenLensResolutionBackoff();

		Assert.False(backoff.TryGetNextRetry(Start, out _));
	}

	[Fact]
	public void NextRetry_MixedDelays_StillReachesTheLongerOneAfterTheSoonerFired()
	{
		// The renderer arms one timer for the soonest item. When it fires and that item resolves,
		// the item with the longer delay must still be retried without any redraw or edit.
		var backoff = new OpenLensResolutionBackoff();
		for (int i = 0; i < 5; i++)
			backoff.RecordUnresolved("slow", "references", Start);
		var slowDue = Start + OpenLensResolutionBackoff.GetDelay(5);
		backoff.RecordUnresolved("fast", "references", Start);
		var fastDue = Start + OpenLensResolutionBackoff.InitialDelay;

		Assert.True(backoff.TryGetNextRetry(Start, out var first));
		Assert.Equal(fastDue, first);

		// The timer fires at fastDue: "fast" is resolvable, "slow" is not yet...
		Assert.True(backoff.CanResolve("fast", "references", fastDue));
		Assert.False(backoff.CanResolve("slow", "references", fastDue));
		backoff.RecordResolved("fast", "references");

		// ...and the re-arm after that pass targets "slow".
		Assert.True(backoff.TryGetNextRetry(fastDue, out var next));
		Assert.Equal(slowDue, next);
		Assert.True(backoff.CanResolve("slow", "references", next));
	}

	[Fact]
	public void NextRetry_IgnoresItemsWhoseDelayAlreadyPassed()
	{
		// An item that became resolvable but was not resolved is off screen. Re-arming for a time
		// that is already past would fire at once, forever: the spin this type exists to stop.
		var backoff = new OpenLensResolutionBackoff();
		backoff.RecordUnresolved("offscreen", "references", Start);
		var afterDelay = Start + OpenLensResolutionBackoff.InitialDelay;

		Assert.False(backoff.TryGetNextRetry(afterDelay, out _));
		Assert.False(backoff.TryGetNextRetry(afterDelay + TimeSpan.FromMinutes(1), out _));
	}

	[Fact]
	public void Items_AreKeyedByAnchorAndLens()
	{
		var backoff = new OpenLensResolutionBackoff();

		backoff.RecordUnresolved("a", "references", Start);

		Assert.True(backoff.CanResolve("a", "implementations", Start));
		Assert.True(backoff.CanResolve("b", "references", Start));
	}
}
