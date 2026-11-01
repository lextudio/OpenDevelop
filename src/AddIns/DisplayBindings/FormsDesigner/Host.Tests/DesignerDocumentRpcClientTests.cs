using System.Text.Json;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.FormsDesigner.Host.Tests;

/// <summary>The wire shape of the verbs every source-model designer shares. Hosts match these names and
/// parameter names exactly, so a rename or a stray extra argument here breaks WPF, GTK and MewUI at once.</summary>
public sealed class DesignerDocumentRpcClientTests
{
	sealed class RecordingConnection : DesignerHostProcessClient
	{
		public readonly List<(string Method, string Json)> Calls = new();
		protected override string GetChildDllPath() => "";
		public override Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken, TimeSpan? timeout = null, bool terminateHostOnTimeout = true)
		{
			Calls.Add((method, JsonSerializer.Serialize(arguments)));
			return Task.FromResult(default(T)!);
		}
	}

	static (DesignerDocumentRpcClient Client, RecordingConnection Connection) Create()
	{
		var connection = new RecordingConnection();
		return (new DesignerDocumentRpcClient(connection, connection.SessionId, "doc-1"), connection);
	}

	static string[] Names(string json) => JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToArray();

	[Fact]
	public async Task EveryVerb_SendsTheSessionDocumentAndBaseVersionEnvelope_AndExactlyItsOwnArguments()
	{
		var (client, connection) = Create();
		await client.UndoAsync(7);
		await client.RedoAsync(7);
		await client.ReorderAsync(7, "a", -1);
		await client.SetPropertyManyAsync(7, new[] { "a", "b" }, "Width", "10");
		await client.SetItemsAsync(7, "list", new[] { new DesignerCollectionItemEdit { Text = "x" } });
		await client.WrapElementsAsync(7, new[] { "a" }, "Grid");
		await client.UnwrapElementAsync(7, "grid");
		await client.CopyElementsAsync(7, new[] { "a" });
		await client.PasteElementsAsync(7, "target", new[] { "<Button/>" });

		var expected = new Dictionary<string, string[]> {
			["design/undo"] = new string[0],
			["design/redo"] = new string[0],
			["design/reorder"] = new[] { "elementId", "delta" },
			["design/set-property-many"] = new[] { "elementIds", "propertyName", "value" },
			["design/set-items"] = new[] { "elementId", "items" },
			["design/wrap-elements"] = new[] { "elementIds", "containerType" },
			["design/unwrap-element"] = new[] { "elementId" },
			["design/copy-elements"] = new[] { "elementIds" },
			["design/paste-elements"] = new[] { "targetId", "snippets" },
		};
		Assert.Equal(expected.Keys, connection.Calls.Select(c => c.Method));
		foreach (var (method, json) in connection.Calls) {
			var names = Names(json);
			Assert.Equal(new[] { "sessionId", "documentId", "baseVersion" }.Concat(expected[method]), names);
			var root = JsonDocument.Parse(json).RootElement;
			Assert.Equal("doc-1", root.GetProperty("documentId").GetString());
			Assert.Equal(7, root.GetProperty("baseVersion").GetInt64());
		}
	}

	[Fact]
	public async Task Paste_SendsAfterIdOnlyWhenGiven_SoAHostWithoutItStillAcceptsTheCall()
	{
		var (client, connection) = Create();
		await client.PasteElementsAsync(1, "t", new[] { "<A/>" });
		await client.PasteElementsAsync(1, "t", new[] { "<A/>" }, "sibling");
		Assert.DoesNotContain("afterId", Names(connection.Calls[0].Json));
		var withAfter = JsonDocument.Parse(connection.Calls[1].Json).RootElement;
		Assert.Equal("sibling", withAfter.GetProperty("afterId").GetString());
	}
}
