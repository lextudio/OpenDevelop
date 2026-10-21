using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Common real-pointer resize-handle journey for the visual designers.
/// Backends retain ownership of loading, selection, scrolling, and persistence assertions; this
/// base owns the part that must not drift: fresh handle geometry per retry, delivered native
/// press, stepped held-button movement, release, and the post-drag growth observation.
/// </summary>
public abstract class ResizeDragTestBase(OpenDevelopAppFixture app)
{
    protected readonly OpenDevelopAppFixture App = app;

    protected sealed record ResizeDragResult(
        JsonElement Before,
        JsonElement After,
        int Attempts,
        IReadOnlyList<string> PointerDeliveries);

    /// <summary>
    /// Reads the width and height from one named geometry member.  The three designer actions
    /// expose different members (<c>designSize</c>, <c>selection</c>, or <c>element</c>), but
    /// their resize contract is the same.
    /// </summary>
    protected static (double Width, double Height) GeometrySize(JsonElement geometry, string member)
    {
        var size = geometry.GetProperty(member);
        return (size.GetProperty("width").GetDouble(), size.GetProperty("height").GetDouble());
    }

    protected static void AssertGeometryGrew(
        JsonElement before,
        JsonElement after,
        string member,
        double minimumWidthGrowth,
        double minimumHeightGrowth,
        string subject)
    {
        var beforeSize = GeometrySize(before, member);
        var afterSize = GeometrySize(after, member);
        Assert.True(
            afterSize.Width > beforeSize.Width + minimumWidthGrowth
            && afterSize.Height > beforeSize.Height + minimumHeightGrowth,
            $"{subject} should have materially grown, but went from {beforeSize.Width}×{beforeSize.Height} to {afterSize.Width}×{afterSize.Height}.");
    }

    /// <summary>
    /// Retries a complete native resize gesture. <paramref name="prepareAttemptAsync"/> must
    /// reactivate/reselect/reposition its backend and return freshly sampled screen geometry.
    /// Geometry is deliberately sampled before every press: a previous failed gesture can move
    /// the viewport or clear selection, making a cached handle point unsafe.
    /// </summary>
    protected async Task<ResizeDragResult> RetryResizeHandleDragAsync(
        Func<int, Task<JsonElement>> prepareAttemptAsync,
        Func<Task<JsonElement>> readGeometryAsync,
        string growthMember,
        double minimumWidthGrowth,
        double minimumHeightGrowth,
        double dx,
        double dy,
        TimeSpan growthTimeout,
        int maxAttempts = 4,
        Func<int, Task>? afterGestureAsync = null)
    {
        JsonElement before = default;
        JsonElement after = default;
        var pointerDeliveries = new List<string>();
        var deliveredPress = false;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            before = await prepareAttemptAsync(attempt);
            Assert.True(before.GetProperty("available").GetBoolean(), before.ToString());
            Assert.True(before.GetProperty("handleVisible").GetBoolean(),
                "The resize handle is outside the visible canvas: " + before);
            OpenDevelopAppFixture.AssertSurfaceGeometryConsistent(before, $"resize attempt {attempt}");

            var handle = before.GetProperty("handle");
            var x = handle.GetProperty("x").GetDouble();
            var y = handle.GetProperty("y").GetDouble();

            // The DevFlow endpoint acknowledges an injected request even when the native window
            // did not receive it. Reset and inspect its recorded mouse-down around the real press
            // so a delivery failure cannot be misreported as a flaky resize implementation.
            await App.InvokeAsync("od.pointer-events", true);
            JsonElement delivery = default;
            if (OperatingSystem.IsMacOS())
            {
                // macOS routes a press/move/release sequence issued as separate HTTP requests to
                // different native windows surprisingly often. The DevFlow one-shot drag is the
                // established CliclickHeldDrag path: it keeps the real button held while emitting
                // its configured intermediate moves, so it is still a granular native gesture.
                var dragged = await App.DragPointerAsync(x, y, x + dx, y + dy, steps: 8);
                Assert.True(dragged.GetProperty("ok").GetBoolean(), dragged.ToString());
                Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
                    delivery = await App.InvokeAsync("od.pointer-events", false);
                    return delivery.GetProperty("count").GetInt32() > 0;
                }, TimeSpan.FromSeconds(3), initialDelayMs: 50, maxDelayMs: 150),
                    "The native drag did not deliver a mouse-down to the main window: " + delivery);
            }
            else
            {
                var pressed = await App.PressPointerAsync(x, y);
                Assert.True(pressed.GetProperty("ok").GetBoolean(), pressed.ToString());
                delivery = await App.InvokeAsync("od.pointer-events", false);
            }
            pointerDeliveries.Add($"attempt {attempt}: press ({x},{y}) observed={delivery}");
            if (delivery.GetProperty("count").GetInt32() == 0)
            {
                // Do not attribute a missing native mouse-down to the designer. Release the
                // potentially held OS button before re-activating and sampling fresh geometry
                // for the next delivery attempt; a terminal error below remains explicitly a
                // foreground/native-input failure rather than a resize failure.
                var released = await App.ReleasePointerAsync(x, y);
                Assert.True(released.GetProperty("ok").GetBoolean(), released.ToString());
                continue;
            }
            deliveredPress = true;

            if (!OperatingSystem.IsMacOS())
                await App.DragPointerInStepsAsync(x, y, x + dx, y + dy);
            if (afterGestureAsync is not null)
                await afterGestureAsync(attempt);

            var resized = await OpenDevelopAppFixture.PollUntilAsync(async () => {
                after = await readGeometryAsync();
                return after.GetProperty("available").GetBoolean()
                    && GeometrySize(after, growthMember).Width > GeometrySize(before, growthMember).Width + minimumWidthGrowth
                    && GeometrySize(after, growthMember).Height > GeometrySize(before, growthMember).Height + minimumHeightGrowth;
            }, growthTimeout, initialDelayMs: 100, maxDelayMs: 400);
            if (resized) {
                OpenDevelopAppFixture.AssertSurfaceGeometryConsistent(after, "after");
                return new ResizeDragResult(before, after, attempt, pointerDeliveries);
            }
        }

        throw new Xunit.Sdk.XunitException(
            (deliveredPress
                ? "The delivered resize drag did not grow the selected element after " + maxAttempts + " attempts."
                : "No native resize press reached the main window after " + maxAttempts + " attempts; this is a foreground/input-delivery failure, not a designer resize failure.")
            + " before=" + before + "\nPointer deliveries:\n"
            + string.Join(Environment.NewLine, pointerDeliveries) + "\nafter=" + after);
    }
}
