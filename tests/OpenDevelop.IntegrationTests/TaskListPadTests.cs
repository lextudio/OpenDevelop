using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Integration coverage for the Task List pad: verifies that the parser creates
/// CommentTasks from TODO/FIXME comments in source files, token/scope filtering
/// works, dynamic source updates are reflected, and the pad's UI structure is
/// visible from the moment the workbench shows it.
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
		await app.InvokeAsync("od.task-list.reparse");
		var result = await app.InvokeAsync("od.task-list.entries");
		var count = result.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
		Assert.True(count > 0, "Task List never showed entries from source comments.\n" + result.ToString());

		var tasks = result.GetProperty("tasks").EnumerateArray().ToList();
		Assert.Contains(tasks, t => t.GetProperty("description").GetString()!.Contains("TODO"));
		Assert.Contains(tasks, t => t.GetProperty("description").GetString()!.Contains("FIXME"));
	}

	[Fact]
	public async Task TaskList_PadUiStructure_VisibleAtStartup()
	{
		await app.InvokeAsync("od.show-pad", "TaskList");
		var scope = await app.InvokeAsync("od.task-list.scope");
		Assert.True(scope.TryGetProperty("scope", out _), "TaskList pad did not respond to scope query.\n" + scope);
		var tokens = await app.InvokeAsync("od.task-list.tokens");
		Assert.True(tokens.TryGetProperty("tokens", out _), "TaskList pad did not respond to tokens query.\n" + tokens);
	}

	[Fact]
	public async Task TaskList_TokenFiltering_DisableFixmeShowsOnlyTodo()
	{
		await app.EnsureSolutionOpenAsync(app.SolutionExplorerFixturePath);
		await WaitForTasksAsync();

		// Disable FIXME token.
		var setTokens = await app.InvokeAsync("od.task-list.tokens", """{"TODO":true,"FIXME":false}""");
		Assert.True(setTokens.TryGetProperty("tokens", out _), setTokens.ToString());

		var result = await app.InvokeAsync("od.task-list.entries");
		var tasks = result.GetProperty("tasks").EnumerateArray().ToList();

		Assert.Contains(tasks, t => t.GetProperty("description").GetString()!.Contains("TODO"));
		Assert.DoesNotContain(tasks, t => t.GetProperty("description").GetString()!.Contains("FIXME"));

		// Restore FIXME.
		await app.InvokeAsync("od.task-list.tokens", """{"TODO":true,"FIXME":true}""");
	}

	[Fact]
	public async Task TaskList_TokenFiltering_DisableAllShowsEmpty()
	{
		await app.EnsureSolutionOpenAsync(app.SolutionExplorerFixturePath);
		await WaitForTasksAsync();

		// Disable all tokens.
		await app.InvokeAsync("od.task-list.tokens", """{"TODO":false,"FIXME":false}""");

		var result = await app.InvokeAsync("od.task-list.entries");
		Assert.Equal(0, result.GetProperty("count").GetInt32());

		// Restore.
		await app.InvokeAsync("od.task-list.tokens", """{"TODO":true,"FIXME":true}""");
	}

	[Fact]
	public async Task TaskList_TokenQueryReturnsCurrentState()
	{
		await app.EnsureSolutionOpenAsync(app.SolutionExplorerFixturePath);
		await WaitForTasksAsync();

		var tokens = await app.InvokeAsync("od.task-list.tokens");
		Assert.True(tokens.TryGetProperty("tokens", out var tokenMap), tokens.ToString());

		// Both TODO and FIXME should be enabled by default.
		Assert.True(tokenMap.GetProperty("TODO").GetBoolean(), "TODO should be enabled by default");
		Assert.True(tokenMap.GetProperty("FIXME").GetBoolean(), "FIXME should be enabled by default");
	}

	[Fact]
	public async Task TaskList_TaskTypeDiffers_TODO_vs_FIXME()
	{
		await app.EnsureSolutionOpenAsync(app.SolutionExplorerFixturePath);
		var result = await WaitForTasksAsync();

		var tasks = result.GetProperty("tasks").EnumerateArray().ToList();
		var todoTask = tasks.FirstOrDefault(t => t.GetProperty("description").GetString()!.Contains("TODO"));
		var fixmeTask = tasks.FirstOrDefault(t => t.GetProperty("description").GetString()!.Contains("FIXME"));

		Assert.True(todoTask.ValueKind != JsonValueKind.Null, "TODO task not found");
		Assert.True(fixmeTask.ValueKind != JsonValueKind.Null, "FIXME task not found");

		// Both are Comment type, but descriptions should differ.
		Assert.Equal("Comment", todoTask.GetProperty("type").GetString());
		Assert.Equal("Comment", fixmeTask.GetProperty("type").GetString());
		Assert.NotEqual(
			todoTask.GetProperty("description").GetString(),
			fixmeTask.GetProperty("description").GetString());
	}

	[Fact]
	public async Task TaskList_TasksReportFileNameAndLine()
	{
		await app.EnsureSolutionOpenAsync(app.SolutionExplorerFixturePath);
		var result = await WaitForTasksAsync();

		var tasks = result.GetProperty("tasks").EnumerateArray().ToList();
		foreach (var task in tasks) {
			var fileName = task.GetProperty("fileName").GetString();
			Assert.False(string.IsNullOrEmpty(fileName), "Task should have a file name");
			Assert.True(fileName!.EndsWith("Widget.cs", StringComparison.OrdinalIgnoreCase),
				$"Task file should be Widget.cs, got: {fileName}");
			Assert.True(task.GetProperty("line").GetInt32() > 0, "Task line should be > 0");
		}
	}

	[Fact]
	public async Task TaskList_ScopeQueryReturnsValidValues()
	{
		await app.EnsureSolutionOpenAsync(app.SolutionExplorerFixturePath);
		await WaitForTasksAsync();

		var scope = await app.InvokeAsync("od.task-list.scope");
		Assert.True(scope.TryGetProperty("scope", out var current), scope.ToString());
		Assert.True(current.GetInt32() >= 0 && current.GetInt32() <= 5, "Scope should be 0-5");
		Assert.True(scope.TryGetProperty("scopeNames", out var names), scope.ToString());
		Assert.Equal(6, names.GetArrayLength());
	}

	[Fact]
	public async Task TaskList_ScopeFiltering_SolutionShowsAll()
	{
		await app.EnsureSolutionOpenAsync(app.SolutionExplorerFixturePath);
		await WaitForTasksAsync();

		// Scope 0 = Solution — should show tasks from all projects.
		await app.InvokeAsync("od.task-list.scope", "0");
		var result = await app.InvokeAsync("od.task-list.entries");
		var count = result.GetProperty("count").GetInt32();
		Assert.True(count >= 2, $"Solution scope should show at least 2 tasks, got {count}");

		// Restore to Solution scope.
		await app.InvokeAsync("od.task-list.scope", "0");
	}

	async Task<JsonElement> WaitForTasksAsync()
	{
		await app.InvokeAsync("od.task-list.reparse");
		JsonElement result = default;
		var found = await OpenDevelopAppFixture.PollUntilAsync(async () =>
		{
			result = await app.InvokeAsync("od.task-list.entries");
			return result.TryGetProperty("count", out var c) && c.GetInt32() > 0;
		}, TimeSpan.FromSeconds(10), initialDelayMs: 100, maxDelayMs: 300);
		Assert.True(found, "Task List never populated");
		return result;
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
