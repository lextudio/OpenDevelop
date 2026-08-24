using System.IO.Compression;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WpfDesign.SurfaceHost;
using Xunit;
using WpfThemeFixture;

namespace WpfDesign.SurfaceHost.Tests;

public sealed class WpfSurfaceHostRpcTests
{
	const string Xaml = """
		<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="300">
		  <TextBlock x:Name="greeting" Text="Hello" Width="200" Height="30" Background="White"/>
		  <Button x:Name="go" Content="Go" Width="80" Height="24"/>
		</Grid>
		""";

	// Uses FixtureThemeBackground via DynamicResource so a real render pixel actually differs
	// between the fixture's "Bright"/"Midnight" dictionaries (see DesignTheme_* tests below) -
	// StaticResource would only resolve once at parse time and never pick up a later swap.
	const string ThemedXaml = """
		<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="300" Background="{DynamicResource FixtureThemeBackground}">
		  <TextBlock x:Name="greeting" Text="Hello" Width="200" Height="30"/>
		</Grid>
		""";

	static string HostDll() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
		"../../../../WpfDesign.SurfaceHost/bin/Debug/net10.0-windows/WpfDesign.SurfaceHost.dll"));

	static DesignerDocumentSnapshot Snapshot(long version, string xaml, string projectAssemblyPath = "") => new() {
		Version = version,
		PrimaryFileName = "/project/Page.xaml",
		ProjectAssemblyPath = projectAssemblyPath,
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
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		// RenderTargetBitmap doesn't work headlessly on macOS, but the ProGPU composition path
		// does (see wpf-designer.md's Phase 1 progress notes) - render is no longer best-effort.
		Assert.NotNull(opened.Render);
		Assert.False(string.IsNullOrEmpty(opened.Render!.Data));
	}

	[Fact]
	public async Task ChildHost_UpdatesAndFlushesTheEditedXaml()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);

		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var updatedXaml = Xaml.Replace("Text=\"Hello\"", "Text=\"Updated\"");
		var updated = await client.UpdateAsync(Snapshot(2, updatedXaml), timeout.Token);
		Assert.True(updated.Accepted, updated.Error);
		Assert.NotNull(opened.Render);
		Assert.NotNull(updated.Render);
		Assert.True(updated.Render!.Sequence > opened.Render!.Sequence);

		var flushed = await client.FlushAsync(2, timeout.Token);
		Assert.Equal(2, flushed.BaseVersion);
		var text = flushed.Files.Single().Text;
		Assert.Contains("Updated", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignHitTest_ResolvesAnElementInsideItsBounds()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		// Plain VisualTreeHelper.HitTest never descends past the root visual under headless
		// LibreWPF on macOS (confirmed by direct run; see wpf-designer.md). The fix is
		// ProGpuWpfCompositionTarget.TryHitTestOwner, which answers per-element hit-testing
		// straight from the GPU-side hit-test data ReplayVisualSubtree already builds - no
		// PresentationSource needed. This stays deliberately position-agnostic even though the
		// old tree-bounds-vs-rendered-pixels mismatch is now fixed (see
		// RenderedContent_LandsExactlyAtTheBoundsTheElementTreeReports, which asserts that
		// alignment directly): what this scenario is about is that hit-testing *distinguishes
		// children at all*, so it scans for two points resolving to two different, non-background
		// pick paths rather than hardcoding where those points are.
		var background = await client.HitTestAsync(1, 0, 0, timeout.Token);
		Assert.True(string.IsNullOrEmpty(background.PickPath));

		string? firstPath = null, secondPath = null;
		for (var y = 0; y < 300 && secondPath == null; y += 5)
		{
			for (var x = 0; x < 400 && secondPath == null; x += 5)
			{
				var hit = await client.HitTestAsync(1, x, y, timeout.Token);
				if (string.IsNullOrEmpty(hit.PickPath))
					continue;
				if (firstPath == null)
					firstPath = hit.PickPath;
				else if (hit.PickPath != firstPath)
					secondPath = hit.PickPath;
			}
		}
		Assert.False(firstPath is null, "No point in the frame resolved to any element - hit-testing found nothing at all.");
		Assert.False(secondPath is null, "Only one distinct element was ever resolved across the whole frame - hit-testing is not distinguishing children.");
	}

	[Fact]
	public async Task DesignSetProperty_RoundTripsIntoTheSavedXaml()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var greetingPath = FindByName(opened.Tree!, "greeting")!.Id;

		var edited = await client.SetPropertyAsync(1, greetingPath, "Text", "Edited", timeout.Token);
		Assert.True(edited.Accepted, edited.Error);

		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("Edited", flushed.Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Tree_CarriesRealPropertyValuesForTheAddInsPropertiesPad()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var greeting = FindByName(opened.Tree!, "greeting")!;

		var text = greeting.Properties.SingleOrDefault(p => p.Name == "Text");
		Assert.NotNull(text);
		Assert.Equal("Hello", text!.Value);
		Assert.Equal("String", text.Kind);
		Assert.False(text.IsReadOnly);

		var width = greeting.Properties.SingleOrDefault(p => p.Name == "Width");
		Assert.NotNull(width);
		Assert.Equal("200", width!.Value);
		Assert.Equal("Number", width.Kind);

		// Editing through design/set-property is reflected back in the next tree, proving the
		// property list isn't a one-shot snapshot frozen at session/open.
		var edited = await client.SetPropertyAsync(1, greeting.Id, "Text", "Edited", timeout.Token);
		Assert.True(edited.Accepted, edited.Error);
		var editedGreeting = FindByName(edited.Tree!, "greeting")!;
		Assert.Equal("Edited", editedGreeting.Properties.Single(p => p.Name == "Text").Value);
	}

	[Fact]
	public async Task DesignSetProperty_OnABadElementId_IsRejected()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var goPath = FindByName(opened.Tree!, "go")!.Id;

		var resized = await client.SetBoundsAsync(1, goPath, 0, 0, 150, 40, timeout.Token);
		Assert.True(resized.Accepted, resized.Error);

		// Assert the resulting GEOMETRY, not the specific attribute used to express it. Since
		// design/set-bounds routes through the designer's own PlacementOperation, the container
		// decides how to encode the new bounds - a Grid child with the default Stretch alignment
		// gets a Margin (and GridPlacementSupport deliberately Resets Width/Height), while a
		// Canvas child would get Canvas.Left/Top. Asserting Width="150" in the XAML, as this test
		// originally did, was really asserting the old Width/Height-only implementation that
		// silently ignored x/y and could never move anything.
		var after = FindByName(resized.Tree!, "go")!;
		Assert.Equal(0, after.X, 1);
		Assert.Equal(0, after.Y, 1);
		Assert.Equal(150, after.Width, 1);
		Assert.Equal(40, after.Height, 1);

		// ...and it still round-trips into the saved document.
		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("Margin=", flushed.Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignDeleteElements_RemovesTheElementFromTheSavedXaml()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		var first = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var second = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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

	[Fact]
	public async Task SharedClients_UseOneProcessAndKeepDocumentsIsolated()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		var first = await WpfSurfaceHostClient.AcquireSharedAsync(HostDll(), timeout.Token);
		var second = await WpfSurfaceHostClient.AcquireSharedAsync(HostDll(), timeout.Token);
		try
		{
			Assert.Equal(first.ProcessId, second.ProcessId);
			Assert.Equal(first.SessionId, second.SessionId);
			Assert.NotEqual(first.DocumentId, second.DocumentId);
			var firstState = await first.OpenAsync(Snapshot(1, Xaml), timeout.Token);
			var secondState = await second.OpenAsync(Snapshot(1, Xaml.Replace("Hello", "Sibling")), timeout.Token);
			var greeting = FindByName(firstState.Tree!, "greeting")!;
			Assert.True((await first.SetPropertyAsync(1, greeting.Id, "Text", "First edited", timeout.Token)).Accepted);
			Assert.Contains("First edited", (await first.FlushAsync(1, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
			var siblingText = (await second.FlushAsync(1, timeout.Token)).Files.Single().Text;
			Assert.Contains("Sibling", siblingText, StringComparison.Ordinal);
			Assert.DoesNotContain("First edited", siblingText, StringComparison.Ordinal);
			first.Dispose();
			Assert.True(second.IsAlive);
			Assert.Contains("Sibling", (await second.FlushAsync(1, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		}
		finally
		{
			first.Dispose();
			second.Dispose();
		}
	}

	[Fact]
	public async Task SessionOpen_RendersRealWpfContentIntoTheFrame()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.NotNull(opened.Render);
		var frame = opened.Render!;

		var compressed = Convert.FromBase64String(frame.Data);
		using var compressedStream = new MemoryStream(compressed);
		using var deflate = new DeflateStream(compressedStream, CompressionMode.Decompress);
		using var pixelStream = new MemoryStream();
		deflate.CopyTo(pixelStream);
		var pixels = pixelStream.ToArray();
		var stride = frame.Width * 4;
		Assert.Equal(stride * frame.Height, pixels.Length);

		// Deliberately does not assume WHERE in the frame content ends up. Two earlier attempts
		// both did and were both wrong: one assumed the TextBlock/Button would be centered per
		// default Grid alignment (real content was elsewhere); the other used the tree's own
		// reported bounds for "go" (X/Y/Width/Height, computed via TransformToAncestor in
		// BuildNode) - real content still did not land there. There is a real, confirmed,
		// currently-unresolved mismatch between the coordinates ProGPU's replay pipeline paints
		// at and the coordinates this backend's own element tree reports (see wpf-designer.md's
		// Phase 1 progress notes) - this test does not depend on resolving that. It scans the
		// whole frame and asserts at least one pixel differs from the (0,0) background corner
		// (never part of any child's content, since the root Grid has no Background) - decisive
		// proof real content was composited, regardless of exactly where.
		var background = ReadBgra(pixels, stride, 0, 0);
		var foundNonBackgroundPixel = false;
		for (var y = 0; y < frame.Height && !foundNonBackgroundPixel; y++)
		{
			for (var x = 0; x < frame.Width; x++)
			{
				if (ReadBgra(pixels, stride, x, y) != background)
				{
					foundNonBackgroundPixel = true;
					break;
				}
			}
		}
		Assert.True(foundNonBackgroundPixel,
			"Every pixel in the frame matches the (0,0) background corner - rendering did not composite any real content.");
	}

	static (byte R, byte G, byte B, byte A) ReadBgra(byte[] pixels, int stride, int x, int y)
	{
		var offset = y * stride + x * 4;
		return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
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
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
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
		using var restarted = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		Assert.NotEqual(crashedProcessId, restarted.ProcessId);
		var reopened = await restarted.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(reopened.Accepted, reopened.Error);
		Assert.NotNull(FindByName(reopened.Tree!, "greeting"));
	}

	static void AssertFixtureNotLoadedHere()
		=> Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly =>
			string.Equals(assembly.GetName().Name, "CustomControlFixture", StringComparison.OrdinalIgnoreCase));

	[Fact]
	public async Task RenderedContent_LandsExactlyAtTheBoundsTheElementTreeReports()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		// Regression test for the coordinate-mismatch bug (see wpf-designer.md): the child used
		// to Arrange its root into a hardcoded 800x600 viewport while rendering a texture sized
		// to the root's own 400x300, so WPF's normal Stretch centering left the root a
		// VisualOffset of ((800-400)/2, (600-300)/2) = (200,150) and every rendered pixel was
		// shifted by that much relative to the coordinates the element tree reports. The fixture's
		// white-background TextBlock is the probe: its painted white region must line up exactly
		// with the bounds the tree reports for it, which is only true when the root's offset is 0.
		var greeting = FindByName(opened.Tree!, "greeting")!;
		var frame = opened.Render!;
		var pixels = Inflate(frame.Data);
		var stride = frame.Width * 4;

		int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
		for (var y = 0; y < frame.Height; y++)
		{
			for (var x = 0; x < frame.Width; x++)
			{
				var offset = y * stride + x * 4;
				// BGRA on the wire; the TextBlock's Background="White" is the only pure-white paint.
				if (pixels[offset] == 255 && pixels[offset + 1] == 255 && pixels[offset + 2] == 255)
				{
					if (x < minX) minX = x;
					if (x > maxX) maxX = x;
					if (y < minY) minY = y;
					if (y > maxY) maxY = y;
				}
			}
		}
		Assert.True(minX != int.MaxValue, "No white pixels at all - the TextBlock's background never rendered.");

		// One pixel of tolerance on each edge for anti-aliasing/rounding at the boundary.
		Assert.InRange(minX, greeting.X - 1, greeting.X + 1);
		Assert.InRange(minY, greeting.Y - 1, greeting.Y + 1);
		Assert.InRange(maxX, greeting.X + greeting.Width - 2, greeting.X + greeting.Width);
		Assert.InRange(maxY, greeting.Y + greeting.Height - 2, greeting.Y + greeting.Height);
	}

	[Fact]
	public async Task DesignSetBounds_MovesTheElement_NotOnlyResizesIt()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var before = FindByName(opened.Tree!, "greeting")!;

		// design/set-bounds used to set only Width/Height and silently drop x/y, so an
		// interactive drag could resize but never move. It now routes through the designer's own
		// PlacementOperation, which expresses the move the way the container wants it (Margin
		// under a Grid, Canvas.Left/Top under a Canvas, ...). Assert the element actually landed
		// somewhere different, not just that the call was accepted.
		var moved = await client.SetBoundsAsync(1, before.Id, 12, 34, before.Width, before.Height, timeout.Token);
		Assert.True(moved.Accepted, moved.Error);

		var after = FindByName(moved.Tree!, "greeting")!;
		Assert.Equal(12, after.X, 1);
		Assert.Equal(34, after.Y, 1);
		Assert.NotEqual(before.X, after.X);
		// The size it was told to keep must survive the move.
		Assert.Equal(before.Width, after.Width, 1);
		Assert.Equal(before.Height, after.Height, 1);
	}

	static byte[] Inflate(string data)
	{
		using var input = new MemoryStream(Convert.FromBase64String(data));
		using var deflate = new DeflateStream(input, CompressionMode.Decompress);
		using var output = new MemoryStream();
		deflate.CopyTo(output);
		return output.ToArray();
	}

	static string WpfThemeFixtureDll() => typeof(FixtureMarker).Assembly.Location;

	[Fact]
	public async Task DesignTheme_NoThemeInfo_ReportsNoThemes()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		// This test assembly itself has no embedded themes - the convention
		// must come from the project assembly, not be invented by the child.
		var opened = await client.OpenAsync(Snapshot(1, Xaml, typeof(WpfSurfaceHostRpcTests).Assembly.Location), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.False(opened.SupportsThemeSwitch);
		Assert.Empty(opened.DesignThemes);
	}

	[Fact]
	public async Task DesignTheme_Declared_EnumeratesAllEmbeddedThemes()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, ThemedXaml, WpfThemeFixtureDll()), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.True(opened.SupportsThemeSwitch);
		// Theme names come from the embedded themes/*.xaml file names - three of them here -
		// and generic.xaml is the fallback default-style dictionary, NOT a switchable theme.
		Assert.Equal(new[] { "Bright", "Midnight", "Solarized" }, opened.DesignThemes);
	}

	[Fact]
	public async Task DesignTheme_SwitchesRenderBetweenThemes()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, ThemedXaml, WpfThemeFixtureDll()), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.True(opened.SupportsThemeSwitch);

		var bright = await client.SetThemeAsync(1, "Bright", timeout.Token);
		Assert.True(bright.Accepted, bright.Error);
		Assert.NotNull(bright.Render);

		var midnight = await client.SetThemeAsync(1, "Midnight", timeout.Token);
		Assert.True(midnight.Accepted, midnight.Error);
		Assert.NotNull(midnight.Render);

		// The fixture's Bright/Midnight/Solarized dictionaries paint FixtureThemeBackground at
		// different colors - a real theme swap must change what actually got composited.
		Assert.NotEqual(bright.Render!.Data, midnight.Render!.Data);

		var solarized = await client.SetThemeAsync(1, "Solarized", timeout.Token);
		Assert.True(solarized.Accepted, solarized.Error);
		Assert.NotNull(solarized.Render);
		Assert.NotEqual(midnight.Render.Data, solarized.Render!.Data);
	}

	[Fact]
	public async Task DesignTheme_UnknownName_IsRejected()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, ThemedXaml, WpfThemeFixtureDll()), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var result = await client.SetThemeAsync(1, "Dark", timeout.Token);
		Assert.False(result.Accepted);
		Assert.False(string.IsNullOrEmpty(result.Error));
	}

	[Fact]
	public async Task DesignTheme_WithoutConvention_IsRejected()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml, typeof(WpfSurfaceHostRpcTests).Assembly.Location), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.False(opened.SupportsThemeSwitch);

		var result = await client.SetThemeAsync(1, "Dark", timeout.Token);
		Assert.False(result.Accepted);
		Assert.False(string.IsNullOrEmpty(result.Error));
	}
}
