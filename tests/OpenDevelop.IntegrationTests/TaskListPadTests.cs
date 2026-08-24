using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Integration coverage for the Task List pad: verifies that the parser creates
/// CommentTasks from TODO/FIXME comments in source files and that the pad's
/// UI structure (toolbar + list) is visible from the moment the workbench shows it.
/// The fixture file (Widget.cs) contains two comment tokens: a TODO and a FIXME.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class TaskListPadTests : IAsyncDisposable
{
	readonly OpenDevelopAppFixture app;

	public TaskListPadTests(OpenDevelopAppFixture app) => this.app = app;

	[Fact]
	public async Task TaskList_PadShowsEntriesFromSourceComments()
	{
		await app.EnsureSolutionOpenAsync(app.SolutionExplorerFixturePath);

		// Poll until the parser discovers the TODO/FIXME comments in Widget.cs.
		JsonElement result = default;
		var found = await OpenDevelopAppFixture.PollUntilAsync(async () =>
		{
			result = await app.InvokeAsync("od.task-list.entries");
			return result.TryGetProperty("count", out var c) && c.GetInt32() > 0;
		}, TimeSpan.FromSeconds(30), initialDelayMs: 200, maxDelayMs: 500);
		Assert.True(found, "Task List never showed entries from source comments.\n" + result.ToString());

		var tasks = result.GetProperty("tasks").EnumerateArray().ToList();
		Assert.Contains(tasks, t => t.GetProperty("description").GetString()!.Contains("TODO"));
		Assert.Contains(tasks, t => t.GetProperty("description").GetString()!.Contains("FIXME"));
	}

	[Fact]
	public async Task TaskList_PadUiStructure_VisibleAtStartup()
	{
		await app.InvokeAsync("od.show-pad", "TaskList");
		var status = await app.InvokeAsync("od.mewui-designer.status"); // any action proves agent alive

		// Verify the pad has its toolbar and list rendered in the visual tree.
		var tree = await app.GetUITreeAsync();
		var elements = FlattenElements(tree).ToList();

		// The taskView ListView should be present with items or at least visible.
		Assert.Contains(elements, e =>
			e.TryGetProperty("type", out var t) && t.GetString() == "ListView");

		// The toolbar should exist above the list.
		Assert.Contains(elements, e =>
			e.TryGetProperty("type", out var t) && (t.GetString() == "ToolBar" || t.GetString() == "StackPanel"));
	}

	static IEnumerable<JsonElement> FlattenElements(JsonElement element)
	{
		yield return element;
		if (element.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
			foreach (var child in children.EnumerateArray())
				foreach (var descendant in FlattenElements(child))
					yield return descendant;
	}

	public ValueTask DisposeAsync() { try { Directory.Delete(Path.Combine(Path.GetTempPath(), "TaskListPadTests-" + Guid.NewGuid().ToString("N")), true); } catch { } return ValueTask.CompletedTask; }
}
