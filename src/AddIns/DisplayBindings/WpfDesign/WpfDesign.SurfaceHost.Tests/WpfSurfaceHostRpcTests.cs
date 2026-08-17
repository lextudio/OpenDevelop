using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace WpfDesign.SurfaceHost.Tests;

public sealed class WpfSurfaceHostRpcTests
{
	const string Xaml = """
		<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="300">
		  <TextBlock x:Name="greeting" Text="Hello" Width="200" Height="30" Background="White"/>
		  <Button x:Name="go" Content="Go" Width="80" Height="24"/>
		</Grid>
		""";

	static string HostDll() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
		"../../../../WpfDesign.SurfaceHost/bin/Debug/net10.0-windows/WpfDesign.SurfaceHost.dll"));

	static DesignerDocumentSnapshot Snapshot(long version, string xaml) => new() {
		Version = version,
		PrimaryFileName = "/project/Page.xaml",
		Files = { new DesignerSourceFileSnapshot { FileName = "/project/Page.xaml", Kind = "Source", Text = xaml } }
	};

	static DesignerElementNode? FindByName(DesignerElementNode node, string name)
	{
		if (node.Name == name)
			return node;
		foreach (var child in node.Children)
		{
			if (FindByName(child, name) is { } found)
				return found;
		}
		return null;
	}

	[Fact]
	public async Task ChildHost_HandshakesAndOpensASession()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		Assert.True(client.IsAlive);
		Assert.NotEqual(Environment.ProcessId, client.ProcessId);

		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.Equal(client.SessionId, opened.SessionId);
		Assert.Equal(client.DocumentId, opened.DocumentId);
		Assert.Equal("System.Windows.Controls.Grid", opened.RootType);
		Assert.NotNull(opened.Tree);
		Assert.NotNull(FindByName(opened.Tree!, "greeting"));
		Assert.NotNull(FindByName(opened.Tree!, "go"));
		// Render is best-effort on this platform: RenderTargetBitmap needs LibreWPF's native
		// wpfgfx compositor, which has no macOS build (see wpf-designer.md's Phase 0 progress
		// notes) - session/open must still succeed and populate the rest of the state without it.
		if (opened.Render != null)
			Assert.False(string.IsNullOrEmpty(opened.Render.Data));
	}

	[Fact]
	public async Task ChildHost_UpdatesAndFlushesTheEditedXaml()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);

		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var updatedXaml = Xaml.Replace("Text=\"Hello\"", "Text=\"Updated\"");
		var updated = await client.UpdateAsync(Snapshot(2, updatedXaml), timeout.Token);
		Assert.True(updated.Accepted, updated.Error);
		// Render is best-effort on this platform (see ChildHost_HandshakesAndOpensASession); only
		// compare sequence numbers when a native compositor actually produced frames.
		if (opened.Render != null && updated.Render != null)
			Assert.True(updated.Render.Sequence > opened.Render.Sequence);

		var flushed = await client.FlushAsync(2, timeout.Token);
		Assert.Equal(2, flushed.BaseVersion);
		var text = flushed.Files.Single().Text;
		Assert.Contains("Updated", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignHitTest_ResolvesAnElementInsideItsBounds()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		// Confirmed by a real run (see wpf-designer.md's Phase 0 progress notes):
		// VisualTreeHelper.HitTest never descends past the root visual under headless LibreWPF
		// on macOS, even though the TextBlock/Button children are real, arranged WPF objects with
		// correct bounds - every hit callback reports only the Grid itself. This is a confirmed
		// platform gap, not a designer bug, so this scenario can only assert what genuinely works
		// today: hit-testing resolves *some* valid element (the root), not a false green check
		// pretending child-level picking works.
		var hit = await client.HitTestAsync(1, 50, 15, timeout.Token);
		Assert.False(hit.PickPath is null);
		var hitNode = FindNodeByPath(opened.Tree!, hit.PickPath);
		Assert.NotNull(hitNode);
		Assert.Equal("Grid", hitNode!.Type);
	}

	static DesignerElementNode? FindNodeByPath(DesignerElementNode node, string path)
	{
		if (node.Path == path)
			return node;
		foreach (var child in node.Children)
		{
			if (FindNodeByPath(child, path) is { } found)
				return found;
		}
		return null;
	}

	[Fact]
	public async Task DesignSetProperty_RoundTripsIntoTheSavedXaml()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var greetingPath = FindByName(opened.Tree!, "greeting")!.Id;

		var edited = await client.SetPropertyAsync(1, greetingPath, "Text", "Edited", timeout.Token);
		Assert.True(edited.Accepted, edited.Error);

		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("Edited", flushed.Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignSetProperty_OnABadElementId_IsRejected()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var result = await client.SetPropertyAsync(1, "9,9,9", "Text", "Edited", timeout.Token);
		Assert.False(result.Accepted);
		Assert.False(string.IsNullOrEmpty(result.Error));
	}

	[Fact]
	public async Task DesignSetBounds_ChangesWidthAndHeight()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var goPath = FindByName(opened.Tree!, "go")!.Id;

		var resized = await client.SetBoundsAsync(1, goPath, 0, 0, 150, 40, timeout.Token);
		Assert.True(resized.Accepted, resized.Error);
		var flushed = await client.FlushAsync(1, timeout.Token);
		var text = flushed.Files.Single().Text;
		Assert.Contains("Width=\"150\"", text, StringComparison.Ordinal);
		Assert.Contains("Height=\"40\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignDeleteElements_RemovesTheElementFromTheSavedXaml()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var goPath = FindByName(opened.Tree!, "go")!.Id;

		var deleted = await client.DeleteElementsAsync(1, new[] { goPath }, timeout.Token);
		Assert.True(deleted.Accepted, deleted.Error);
		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.DoesNotContain("x:Name=\"go\"", flushed.Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignAddElement_InsertsANewStockControl()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var gridPath = opened.Tree!.Id;

		var toolboxItem = new DesignerToolboxItemInfo {
			TypeName = "CheckBox",
			XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
		};
		var added = await client.AddElementAsync(1, gridPath, toolboxItem, "added", 10, 10, timeout.Token);
		Assert.True(added.Accepted, added.Error);
		var addedNode = FindByName(added.Tree!, "added");
		Assert.True(addedNode != null, "the new CheckBox did not appear in the element tree");
		Assert.Equal("CheckBox", addedNode!.Type);

		var flushed = await client.FlushAsync(1, timeout.Token);
		var text = flushed.Files.Single().Text;
		Assert.Contains("CheckBox", text, StringComparison.Ordinal);
		Assert.Contains("x:Name=\"added\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignAddElement_OnABadParentId_IsRejected()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var toolboxItem = new DesignerToolboxItemInfo {
			TypeName = "CheckBox",
			XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
		};
		var result = await client.AddElementAsync(1, "9,9,9", toolboxItem, "shouldNotApply", 0, 0, timeout.Token);
		Assert.False(result.Accepted);
		Assert.False(string.IsNullOrEmpty(result.Error));
	}

	[Fact]
	public async Task DesignDeleteElements_OnABadElementId_IsRejectedWithoutPartiallyApplying()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var goPath = FindByName(opened.Tree!, "go")!.Id;

		// "go" is a valid id and comes first in the list, "9,9,9" is not - a naive
		// remove-as-you-go implementation would delete "go" before ever reaching the bad id.
		var result = await client.DeleteElementsAsync(1, new[] { goPath, "9,9,9" }, timeout.Token);
		Assert.False(result.Accepted);
		Assert.False(string.IsNullOrEmpty(result.Error));

		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("x:Name=\"go\"", flushed.Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignRename_OnABadElementId_IsRejected()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var result = await client.RenameAsync(1, "9,9,9", "shouldNotApply", timeout.Token);
		Assert.False(result.Accepted);
		Assert.False(string.IsNullOrEmpty(result.Error));
	}

	[Fact]
	public async Task DesignRename_ChangesTheElementName()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var greetingPath = FindByName(opened.Tree!, "greeting")!.Id;

		var renamed = await client.RenameAsync(1, greetingPath, "greetingRenamed", timeout.Token);
		Assert.True(renamed.Accepted, renamed.Error);
		Assert.NotNull(FindByName(renamed.Tree!, "greetingRenamed"));

		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("greetingRenamed", flushed.Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TwoIndependentClients_HaveDistinctSessionsAndLifetimes()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		var first = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var second = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		try
		{
			Assert.NotEqual(first.ProcessId, second.ProcessId);
			Assert.NotEqual(first.SessionId, second.SessionId);
			Assert.True((await first.OpenAsync(Snapshot(1, Xaml), timeout.Token)).Accepted);
			Assert.True((await second.OpenAsync(Snapshot(1, Xaml), timeout.Token)).Accepted);
			first.Dispose();
			Assert.True(second.IsAlive);
			Assert.True((await second.OpenAsync(Snapshot(2, Xaml), timeout.Token)).Accepted);
		}
		finally
		{
			first.Dispose();
			second.Dispose();
		}
	}

	static string CustomControlFixtureDll() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
		"../../../../WpfDesign.SurfaceHost.Tests/Fixtures/CustomControlFixture/bin/Debug/net10.0-windows/CustomControlFixture.dll"));

	[Fact]
	public async Task CustomControlType_IsResolvedOnlyInTheChild()
	{
		var fixtureDll = CustomControlFixtureDll();
		Assert.True(File.Exists(fixtureDll), "CustomControlFixture must be built before this test runs: " + fixtureDll);

		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:c="clr-namespace:CustomControlFixture;assembly=CustomControlFixture" Width="400" Height="300">
			  <c:GreetingBadge x:Name="badge" Content="Hi"/>
			</Grid>
			""";
		var snapshot = Snapshot(1, xaml);
		snapshot.ProjectAssemblyPath = fixtureDll;

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.NotNull(opened.Tree);
		var badge = FindByName(opened.Tree!, "badge");
		Assert.True(badge != null, "GreetingBadge did not resolve in the child - the custom control type was not found.");
		Assert.Equal("GreetingBadge", badge!.Type);

		// The Phase 1 gate this test exists to prove (wpf-designer.md): type resolution for a
		// project-defined control happens only in the spawned child, never in this test process.
		AssertFixtureNotLoadedHere();
	}

	[Fact]
	public async Task ReferencedAssemblyControlType_IsResolvedOnlyInTheChild()
	{
		var fixtureDll = CustomControlFixtureDll();
		Assert.True(File.Exists(fixtureDll), "CustomControlFixture must be built before this test runs: " + fixtureDll);

		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:c="clr-namespace:CustomControlFixture;assembly=CustomControlFixture" Width="400" Height="300">
			  <c:GreetingBadge x:Name="badge" Content="Hi"/>
			</Grid>
			""";
		// The referenced-library case: the control lives in a *reference*, not the project's own
		// output, so ProjectAssemblyPath is deliberately left empty. Testing only the
		// ProjectAssemblyPath path (as the first version of this wire-in did) silently ignored
		// ReferencedAssemblyPaths entirely and never built a SurfaceTypeFinder at all.
		var snapshot = Snapshot(1, xaml);
		snapshot.ReferencedAssemblyPaths.Add(fixtureDll);

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.NotNull(opened.Tree);
		var badge = FindByName(opened.Tree!, "badge");
		Assert.True(badge != null, "GreetingBadge did not resolve in the child - the custom control type was not found.");
		Assert.Equal("GreetingBadge", badge!.Type);

		AssertFixtureNotLoadedHere();
	}

	[Fact]
	public async Task AppXamlResources_AreMergedAndAffectTheDocumentLayout()
	{
		// An *implicit* style (TargetType, no x:Key) that sets Width is deliberately chosen as the
		// probe: layout actually consumes the value, so the resulting ActualWidth in the element
		// tree the child already reports is decisive proof the app-level dictionary was merged.
		// Reading the property back instead would be ambiguous - WpfDesign.XamlDom represents a
		// markup-extension value as a design-time wrapper rather than eagerly resolving it, so
		// DesignItemProperty.ValueOnInstance reports null even for a resource that did resolve.
		const string appXaml = """
			<Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			  <Application.Resources>
			    <Style TargetType="TextBlock">
			      <Setter Property="Width" Value="250"/>
			    </Style>
			  </Application.Resources>
			</Application>
			""";
		const string pageXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="300">
			  <TextBlock x:Name="styled" Text="Hi"/>
			</Grid>
			""";
		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Page.xaml",
			Files = {
				new DesignerSourceFileSnapshot { FileName = "/project/Page.xaml", Kind = "Source", Text = pageXaml },
				new DesignerSourceFileSnapshot { FileName = "/project/App.xaml", Kind = "AppXaml", Text = appXaml }
			}
		};

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.NotNull(opened.Tree);
		var styled = FindByName(opened.Tree!, "styled");
		Assert.True(styled != null, "the TextBlock did not appear in the element tree");
		// 250 can only come from layout consuming the merged implicit style; without the merge the
		// TextBlock stretches to the Grid's full 400 (verified by a real run before the fix).
		Assert.Equal(250d, styled!.Width);
	}

	[Fact]
	public async Task StaleMutations_AreRejectedAndCannotOverwriteNewerSource()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		Assert.True((await client.OpenAsync(Snapshot(1, Xaml), timeout.Token)).Accepted);

		// The host accepts newer source (a source-side edit), moving the document to version 2.
		var updated = await client.UpdateAsync(Snapshot(2, Xaml.Replace("Text=\"Hello\"", "Text=\"Newer\"")), timeout.Token);
		Assert.True(updated.Accepted, updated.Error);
		var greetingPath = FindByName(updated.Tree!, "greeting")!.Id;

		// Every mutating operation still carrying the now-stale version 1 must be rejected - the
		// DDP's mandatory rule 5 / "Can a stale request overwrite newer XAML?" checklist item.
		foreach (var (name, stale) in new (string, DesignerSessionState)[] {
			("set-property", await client.SetPropertyAsync(1, greetingPath, "Text", "Clobbered", timeout.Token)),
			("set-bounds", await client.SetBoundsAsync(1, greetingPath, 0, 0, 11, 22, timeout.Token)),
			("delete-elements", await client.DeleteElementsAsync(1, new[] { greetingPath }, timeout.Token)),
			("rename", await client.RenameAsync(1, greetingPath, "clobbered", timeout.Token)),
		})
		{
			Assert.False(stale.Accepted, $"design/{name} accepted a stale base version");
			Assert.Contains("Stale base version", stale.Error, StringComparison.Ordinal);
		}

		// ...and the newer source must be intact: no rejected mutation partially applied.
		var flushed = await client.FlushAsync(2, timeout.Token);
		var text = flushed.Files.Single().Text;
		Assert.Contains("Newer", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Clobbered", text, StringComparison.Ordinal);
		Assert.DoesNotContain("clobbered", text, StringComparison.Ordinal);
		Assert.Contains("x:Name=\"greeting\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildCrash_IsDetectedAndTheSurfaceIsRestartable()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		var client = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var crashedProcessId = client.ProcessId;
		Assert.True((await client.OpenAsync(Snapshot(1, Xaml), timeout.Token)).Accepted);

		var exited = new TaskCompletionSource();
		client.HostExited += (sender, args) => exited.TrySetResult();

		// Hard-kill the surface, standing in for the failure modes Phase 1 has to survive:
		// faulting project code, a hung child, or an unrecoverable RPC timeout (which
		// DesignerHostProcessClient.InvokeCoreAsync handles by calling TerminateHost itself).
		client.TerminateHost();
		await exited.Task.WaitAsync(TimeSpan.FromSeconds(30), timeout.Token);
		Assert.False(client.IsAlive);

		// A dead host must fail fast with the documented "not running" error rather than hang or
		// surface some incidental failure - asserting the specific type keeps this from passing
		// for the wrong reason.
		await Assert.ThrowsAsync<IOException>(() => client.OpenAsync(Snapshot(2, Xaml), timeout.Token));
		client.Dispose();

		// The Phase 0/1 recovery gate: a replacement surface rebuilds purely from host-owned
		// snapshot state, with no residue from the dead child.
		using var restarted = await global::WpfDesign.SurfaceHost.Tests.WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		Assert.NotEqual(crashedProcessId, restarted.ProcessId);
		var reopened = await restarted.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(reopened.Accepted, reopened.Error);
		Assert.NotNull(FindByName(reopened.Tree!, "greeting"));
	}

	static void AssertFixtureNotLoadedHere()
		=> Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly =>
			string.Equals(assembly.GetName().Name, "CustomControlFixture", StringComparison.OrdinalIgnoreCase));
}
