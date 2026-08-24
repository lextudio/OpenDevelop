using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WinUIXamlDesigner.UnoDesignHost;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost.Tests;

public sealed class UnoDesignHostRpcTests
{
	const string Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	const string XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

	static string Fixture(string text) =>
		$"""
		<Grid xmlns="{Ns}" xmlns:x="{XNs}" x:Name="root">
		    <TextBlock x:Name="greeting" Text="{text}"/>
		</Grid>
		""";

	static string HostDll() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
		"../../../../WinUIXamlDesigner.UnoHost/bin/Debug/net10.0-desktop/WinUIXamlDesigner.UnoHost.dll"));

	static Task<UnoDesignClient> StartAsync(CancellationToken cancellationToken)
		=> UnoDesignClient.StartAsync("", "", cancellationToken, HostDll());

	/// <summary>Wraps fixture XAML as a single-file document snapshot (the DDP document shape);
	/// the surface size/DPI is presentation state and goes through SetViewport instead.</summary>
	static DesignerDocumentSnapshot Document(UnoDesignClient client, string xaml, long version = 1)
	{
		client.SetViewport(320, 240, 1.0);
		return new DesignerDocumentSnapshot {
			SessionId = client.SessionId,
			DocumentId = client.DocumentId,
			Version = version,
			PrimaryFileName = "MainPage.xaml",
			Language = "",
			Files = { new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = xaml } }
		};
	}

	[Fact]
	public async Task ChildHost_HandshakesAndOpensUpdatesFlushesASession()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await StartAsync(timeout.Token);
		Assert.True(client.IsAlive);
		Assert.NotEqual(Environment.ProcessId, client.ProcessId);

		var opened = await client.OpenAsync(Document(client, Fixture("Hello")), timeout.Token);
		Assert.True(opened.Accepted);
		Assert.NotNull(opened.Tree);
		Assert.NotNull(opened.Render);
		Assert.False(string.IsNullOrEmpty(opened.Render!.Data));

		var updated = await client.UpdateAsync(Document(client, Fixture("Updated"), 2), timeout.Token);
		Assert.True(updated.Accepted);
		Assert.NotNull(updated.Tree);
		Assert.NotNull(updated.Render);
		// The rendered frame carries no reliable monotonic sequence on this backend (Sequence
		// is not populated by the Uno child) - a changed document produces a different
		// compressed frame, which is the useful proxy that a fresh render actually happened.
		Assert.NotEqual(opened.Render.Data, updated.Render!.Data);

		var flushed = await client.FlushAsync(2, timeout.Token);
		var document = Assert.Single(flushed.Files);
		Assert.Contains("Updated", document.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_SupportsElementMutationRpcs()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await StartAsync(timeout.Token);
		var opened = await client.OpenAsync(Document(client, Fixture("Hello")), timeout.Token);
		Assert.True(opened.Accepted);

		var edited = await client.SetPropertyAsync(1, "greeting", "Text", "Edited", timeout.Token);
		Assert.True(edited.Accepted);
		Assert.NotNull(edited.Render);
		Assert.NotEqual(opened.Render!.Data, edited.Render!.Data);

		var badProperty = await client.SetPropertyAsync(1, "doesNotExist", "Text", "x", timeout.Token);
		Assert.False(badProperty.Accepted);
		Assert.False(string.IsNullOrEmpty(badProperty.Error));

		var resized = await client.SetBoundsAsync(1, "greeting", 0, 0, 150, 40, timeout.Token);
		Assert.True(resized.Accepted);

		var eventBound = await client.SetEventAsync(1, "greeting", "PointerPressed", "greeting_PointerPressed", timeout.Token);
		Assert.True(eventBound.Accepted);

		var badEvent = await client.SetEventAsync(1, "greeting", "NotARealEvent", "handler", timeout.Token);
		Assert.False(badEvent.Accepted);

		var itemXaml = $"""<Button xmlns="{Ns}" xmlns:x="{XNs}" x:Name="tempButton" Content="Temp"/>""";
		var added = await client.AddElementAsync(1, "root", new DesignerToolboxItemInfo { Template = itemXaml }, "tempButton", 10, 10, timeout.Token);
		Assert.True(added.Accepted);

		var deleted = await client.DeleteElementsAsync(1, new[] { "tempButton" }, timeout.Token);
		Assert.True(deleted.Accepted);

		var deleteMissing = await client.DeleteElementsAsync(1, new[] { "tempButton" }, timeout.Token);
		Assert.False(deleteMissing.Accepted);
		Assert.False(string.IsNullOrEmpty(deleteMissing.Error));

		var renamed = await client.RenameAsync(1, "greeting", "greeting2", timeout.Token);
		Assert.True(renamed.Accepted);

		var staleNameLookup = await client.SetPropertyAsync(1, "greeting", "Text", "y", timeout.Token);
		Assert.False(staleNameLookup.Accepted);

		var newNameLookup = await client.SetPropertyAsync(1, "greeting2", "Text", "y", timeout.Token);
		Assert.True(newNameLookup.Accepted);
	}

	[Fact]
	public async Task ChildHost_SupportsIndependentLifetimes()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		var first = await StartAsync(timeout.Token);
		var second = await StartAsync(timeout.Token);
		try {
			Assert.NotEqual(first.ProcessId, second.ProcessId);
			Assert.NotEqual(first.SessionId, second.SessionId);

			Assert.True((await first.OpenAsync(Document(first, Fixture("First")), timeout.Token)).Accepted);
			Assert.True((await second.OpenAsync(Document(second, Fixture("Second")), timeout.Token)).Accepted);

			var firstPid = first.ProcessId;
			first.Dispose();
			await WaitForExitAsync(firstPid, timeout.Token);

			Assert.True(second.IsAlive);
			var flushed = await second.FlushAsync(1, timeout.Token);
			Assert.Contains(flushed.Files, item => item.Text.Contains("Second", StringComparison.Ordinal));
		} finally {
			first.Dispose();
			second.Dispose();
		}
	}

	[Fact]
	public async Task SharedHost_UsesOneUnoApplicationAndKeepsDocumentsIsolated()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		var first = await UnoDesignClient.AcquireSharedAsync("", "", timeout.Token, HostDll());
		var second = await UnoDesignClient.AcquireSharedAsync("", "", timeout.Token, HostDll());
		try {
			Assert.Equal(first.ProcessId, second.ProcessId);
			Assert.Equal(first.SessionId, second.SessionId);
			Assert.NotEqual(first.DocumentId, second.DocumentId);
			var firstOpened = await first.OpenAsync(Document(first, Fixture("First")), timeout.Token);
			Assert.True(firstOpened.Accepted);
			Assert.True((await second.OpenAsync(Document(second, Fixture("Second")), timeout.Token)).Accepted);
			var edited = await first.SetPropertyAsync(1, "greeting", "Text", "First edited", timeout.Token);
			Assert.True(edited.Accepted);
			Assert.NotEqual(firstOpened.Render!.Data, edited.Render!.Data);
			Assert.Contains("First", (await first.FlushAsync(1, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
			var sibling = (await second.FlushAsync(1, timeout.Token)).Files.Single().Text;
			Assert.Contains("Second", sibling, StringComparison.Ordinal);
			Assert.DoesNotContain("First edited", sibling, StringComparison.Ordinal);
			first.Dispose();
			Assert.True(second.IsAlive);
			Assert.Contains("Second", (await second.FlushAsync(1, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		} finally {
			first.Dispose();
			second.Dispose();
		}
	}

	static async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
	{
		while (true) {
			try {
				if (System.Diagnostics.Process.GetProcessById(processId).HasExited) return;
			} catch (ArgumentException) { return; }
			await Task.Delay(25, cancellationToken);
		}
	}
}
