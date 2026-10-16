using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WpfDesign.SurfaceHost;
using Xunit;
using WpfThemeFixture;

namespace WpfDesign.SurfaceHost.Tests;

public sealed class WpfSurfaceHostRpcTests
{
	const string Xaml = """
		<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="300">
		  <TextBlock x:Name="greeting" Text="Hello" Width="200" Height="30" Background="White" Canvas.ZIndex="7"/>
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

	const string HitTestXaml = """
		<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="300">
		  <TextBlock x:Name="greeting" Text="Hello" Width="100" Height="30" Background="White" Canvas.Left="20" Canvas.Top="30"/>
		  <Button x:Name="go" Content="Go" Width="80" Height="24" Canvas.Left="240" Canvas.Top="180"/>
		</Canvas>
		""";

	/// <summary>The child host binary under test. Defaults to the LibreWPF host; setting
	/// OPENDEVELOP_WPFSURFACEHOST_DLL points the very same suite at the Microsoft WPF host
	/// (MicrosoftHost/SurfaceHost), which source-links this same host implementation. The DDP
	/// contract is what is being verified and it is identical for both, so the tests are shared
	/// rather than duplicated - only the child binary changes.</summary>
	internal static string HostDll() =>
		Environment.GetEnvironmentVariable("OPENDEVELOP_WPFSURFACEHOST_DLL") is { Length: > 0 } overridden
			? Path.GetFullPath(overridden)
		#if MICROSOFT_WPF_DESIGNER_HOST
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
				"../../../../SurfaceHost/bin/Debug/net10.0-windows/MicrosoftWpfDesign.SurfaceHost.dll"));
		#else
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
				"../../../../WpfDesign.SurfaceHost/bin/Debug/net10.0-windows/WpfDesign.SurfaceHost.dll"));
		#endif

	internal static DesignerDocumentSnapshot Snapshot(long version, string xaml, string projectAssemblyPath = "") => new() {
		Version = version,
		PrimaryFileName = "/project/Page.xaml",
		ProjectAssemblyPath = projectAssemblyPath,
		Files = { new DesignerSourceFileSnapshot { FileName = "/project/Page.xaml", Kind = "Source", Text = xaml } }
	};

	static DesignerElementNode? FindNodeById(DesignerElementNode node, string id)
	{
		if (node.Id == id)
			return node;
		foreach (var child in node.Children)
		{
			if (FindNodeById(child, id) is { } found)
				return found;
		}
		return null;
	}

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
		Assert.Equal(DesignerLayoutMode.Grid, FindByName(opened.Tree!, "greeting")!.LayoutMode);
		var greetingInsets = FindByName(opened.Tree!, "greeting")!.LayoutInsets;
		Assert.NotNull(greetingInsets);
		Assert.Equal("Margin", greetingInsets!.Kind);
		Assert.Equal(7, FindByName(opened.Tree!, "greeting")!.ZIndex);
		Assert.True(FindByName(opened.Tree!, "greeting")!.BaselineOffset > 0);
		Assert.NotNull(FindByName(opened.Tree!, "go"));
		// RenderTargetBitmap doesn't work headlessly on macOS, but the ProGPU composition path
		// does (see wpf-designer.md's Phase 1 progress notes) - render is no longer best-effort.
		Assert.NotNull(opened.Render);
		Assert.False(string.IsNullOrEmpty(opened.Render!.Data));
	}

	[Fact]
	public async Task ChildHost_PublishesMeasuredCanvasPositionInsets()
	{
		const string canvasXaml = """
			<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="200" Height="100">
			  <Button x:Name="positioned" Canvas.Left="20" Canvas.Top="30" Width="40" Height="20" />
			</Canvas>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, canvasXaml), timeout.Token);

		Assert.True(opened.Accepted, opened.Error);
		var insets = FindByName(opened.Tree!, "positioned")!.LayoutInsets;
		Assert.NotNull(insets);
		Assert.Equal("CanvasPosition", insets!.Kind);
		Assert.Equal(20, insets.Left, 3);
		Assert.Equal(30, insets.Top, 3);
		Assert.Equal(140, insets.Right, 3);
		Assert.Equal(50, insets.Bottom, 3);
	}

	[Fact]
	public async Task DesignSetLayoutInset_ChangesOnlyTheRequestedGridMarginEdge()
	{
		const string gridXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="200" Height="100">
			  <Button x:Name="target" Margin="1,2,3,4" Width="40" Height="20" />
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, gridXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var target = FindByName(opened.Tree!, "target")!;

		var changed = await client.SetLayoutInsetAsync(1, target.Id, "Margin", "Right", 30, timeout.Token);

		Assert.True(changed.Accepted, changed.Error);
		var insets = FindByName(changed.Tree!, "target")!.LayoutInsets!;
		Assert.Equal(1, insets.Left, 3);
		Assert.Equal(2, insets.Top, 3);
		Assert.Equal(30, insets.Right, 3);
		Assert.Equal(4, insets.Bottom, 3);
		var saved = (await client.FlushAsync(1, timeout.Token)).Files.Single().Text;
		Assert.Contains("Margin=\"1,2,30,4\"", saved, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignSetLayoutInset_PreservesCanvasRightBottomAnchors()
	{
		const string canvasXaml = """
			<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="200" Height="100">
			  <Button x:Name="target" Canvas.Right="20" Canvas.Bottom="10" Margin="5" Width="40" Height="20" />
			</Canvas>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, canvasXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var target = FindByName(opened.Tree!, "target")!;

		// Canvas.Right positions the margin box. The visible left edge is 135; moving it
		// to 125 must update the authored Right anchor to 30 rather than silently adding
		// Canvas.Left="125" (and it must account for Margin.Right).
		var changed = await client.SetLayoutInsetAsync(1, target.Id, "CanvasPosition", "Left", 125, timeout.Token);

		Assert.True(changed.Accepted, changed.Error);
		var insets = FindByName(changed.Tree!, "target")!.LayoutInsets!;
		Assert.Equal(125, insets.Left, 3);
		Assert.Equal(35, insets.Right, 3);
		var saved = (await client.FlushAsync(1, timeout.Token)).Files.Single().Text;
		Assert.Contains("Canvas.Right=\"30\"", saved, StringComparison.Ordinal);
		Assert.DoesNotContain("Canvas.Left=", saved, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignSetLayoutInset_UsesTheEffectiveCanvasAnchorAndPreservesOverflow()
	{
		const string canvasXaml = """
			<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="200" Height="100">
			  <Button x:Name="both" Canvas.Left="10" Canvas.Right="20" Width="40" Height="20" />
			  <Button x:Name="overflow" Canvas.Left="190" Width="40" Height="20" />
			</Canvas>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, canvasXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var both = FindByName(opened.Tree!, "both")!;
		var overflow = FindByName(opened.Tree!, "overflow")!;
		Assert.Equal(-30, overflow.LayoutInsets!.Right, 3);

		// WPF gives Canvas.Left precedence when both horizontal anchors are authored.
		// Dragging the visible right edge therefore updates Left, not ineffective Right.
		var changed = await client.SetLayoutInsetAsync(1, both.Id, "CanvasPosition", "Right", 140, timeout.Token);
		Assert.True(changed.Accepted, changed.Error);
		var changedBoth = FindByName(changed.Tree!, "both")!;
		Assert.Equal(140, changedBoth.LayoutInsets!.Right, 3);
		var saved = (await client.FlushAsync(changed.Version, timeout.Token)).Files.Single().Text;
		Assert.Contains("Canvas.Left=\"20\"", saved, StringComparison.Ordinal);
		Assert.Contains("Canvas.Right=\"20\"", saved, StringComparison.Ordinal);

		// An overflow distance remains a real negative value and can be used as the drag input.
		var movedOverflow = await client.SetLayoutInsetAsync(changed.Version, overflow.Id, "CanvasPosition", "Right", -25, timeout.Token);
		Assert.True(movedOverflow.Accepted, movedOverflow.Error);
		Assert.Equal(-25, FindByName(movedOverflow.Tree!, "overflow")!.LayoutInsets!.Right, 3);
	}

	[Fact]
	public async Task DesignSetLayoutInset_HidesAndRejectsTransformedElements()
	{
		const string canvasXaml = """
			<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="200" Height="100">
			  <Button x:Name="target" Canvas.Left="10" Width="40" Height="20">
			    <Button.RenderTransform><TranslateTransform X="50" /></Button.RenderTransform>
			  </Button>
			</Canvas>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, canvasXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var target = FindByName(opened.Tree!, "target")!;
		Assert.Null(target.LayoutInsets);

		var rejected = await client.SetLayoutInsetAsync(1, target.Id, "CanvasPosition", "Left", 65, timeout.Token);
		Assert.False(rejected.Accepted);
		Assert.Contains("transformed", rejected.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task DesignToggleLayoutInsetAnchor_PreservesGridChildBounds()
	{
		const string gridXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="200" Height="100">
			  <Grid.RowDefinitions><RowDefinition Height="20" /><RowDefinition Height="80" /></Grid.RowDefinitions>
			  <Grid.ColumnDefinitions><ColumnDefinition Width="50" /><ColumnDefinition Width="150" /></Grid.ColumnDefinitions>
			  <Button x:Name="target" Grid.Row="1" Grid.Column="1" HorizontalAlignment="Left" VerticalAlignment="Top" Width="40" Height="20" Margin="10,5,0,0" />
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, gridXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var target = FindByName(opened.Tree!, "target")!;
		Assert.Equal(60, target.X, 3);
		Assert.Equal(25, target.Y, 3);
		Assert.Equal(40, target.Width, 3);

		var stretched = await client.ToggleLayoutInsetAnchorAsync(1, target.Id, "Left", timeout.Token);
		Assert.True(stretched.Accepted, stretched.Error);
		var stretchedTarget = FindByName(stretched.Tree!, "target")!;
		Assert.Equal(60, stretchedTarget.X, 3);
		Assert.Equal(40, stretchedTarget.Width, 3);
		Assert.Equal(10, stretchedTarget.LayoutInsets!.Left, 3);
		Assert.Equal(100, stretchedTarget.LayoutInsets.Right, 3);

		var anchored = await client.ToggleLayoutInsetAnchorAsync(stretched.Version, target.Id, "Left", timeout.Token);
		Assert.True(anchored.Accepted, anchored.Error);
		var anchoredTarget = FindByName(anchored.Tree!, "target")!;
		Assert.Equal(60, anchoredTarget.X, 3);
		Assert.Equal(40, anchoredTarget.Width, 3);
		Assert.Equal(10, anchoredTarget.LayoutInsets!.Left, 3);
		Assert.Equal(0, anchoredTarget.LayoutInsets.Right, 3);
		var bottomAnchored = await client.ToggleLayoutInsetAnchorAsync(anchored.Version, target.Id, "Bottom", timeout.Token);
		Assert.True(bottomAnchored.Accepted, bottomAnchored.Error);
		var bottomTarget = FindByName(bottomAnchored.Tree!, "target")!;
		Assert.Equal(25, bottomTarget.Y, 3);
		Assert.Equal(20, bottomTarget.Height, 3);
		Assert.Equal(0, bottomTarget.LayoutInsets!.Top, 3);
		Assert.Equal(55, bottomTarget.LayoutInsets.Bottom, 3);
		var saved = (await client.FlushAsync(bottomAnchored.Version, timeout.Token)).Files.Single().Text;
		Assert.Contains("HorizontalAlignment=\"Left\"", saved, StringComparison.Ordinal);
		Assert.Contains("VerticalAlignment=\"Bottom\"", saved, StringComparison.Ordinal);
		Assert.Contains("Width=\"40\"", saved, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignToggleLayoutInsetAnchor_RejectsAConstrainedAxis()
	{
		const string gridXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="200" Height="100">
			  <Button x:Name="target" HorizontalAlignment="Left" Width="40" MinWidth="20" />
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, gridXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var rejected = await client.ToggleLayoutInsetAnchorAsync(1, FindByName(opened.Tree!, "target")!.Id, "Left", timeout.Token);

		Assert.False(rejected.Accepted);
		Assert.Contains("width-constrained", rejected.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_ReportsDeclaredBindingsWithoutClaimingRuntimeSuccess()
	{
		const string bindingXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="100" Height="40">
			  <TextBlock x:Name="bound" Text="{Binding Title}" />
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, bindingXaml), timeout.Token);

		Assert.True(opened.Accepted, opened.Error);
		var binding = Assert.Single(FindByName(opened.Tree!, "bound")!.Bindings);
		Assert.Equal("Text", binding.Property);
		Assert.Equal("Declared", binding.Status);
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
		var opened = await client.OpenAsync(Snapshot(1, HitTestXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var rootHit = await client.HitTestAsync(1, 0, 0, timeout.Token);
		Assert.True(rootHit.Hit, "The root canvas should resolve at its origin.");
		Assert.Equal(opened.Tree!.Id, rootHit.PickPath);
		foreach (var name in new[] { "greeting", "go" })
		{
			var node = FindByName(opened.Tree!, name)!;
			var hit = await client.HitTestAsync(1, node.X + node.Width / 2, node.Y + node.Height / 2, timeout.Token);
			Assert.True(hit.Hit, $"The centre of '{name}' did not resolve to a design item.");
			Assert.Equal(node.Id, hit.PickPath);
		}
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
	public async Task DesignSetEvent_PublishesAndRoundTripsAnExistingHandlerName()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.IsAssignableFrom<IDesignHostEventBinding>(client);
		var greeting = FindByName(opened.Tree!, "greeting")!;
		Assert.Contains(greeting.Events, item => item.Name == "TargetUpdated");

		var bound = await client.SetEventAsync(1, greeting.Id, "TargetUpdated", "greeting_TargetUpdated", timeout.Token);
		Assert.True(bound.Accepted, bound.Error);
		Assert.Equal("greeting_TargetUpdated", FindByName(bound.Tree!, "greeting")!.Events.Single(item => item.Name == "TargetUpdated").Handler);
		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("TargetUpdated=\"greeting_TargetUpdated\"", flushed.Files.Single().Text, StringComparison.Ordinal);

		var cleared = await client.SetEventAsync(1, greeting.Id, "TargetUpdated", "", timeout.Token);
		Assert.True(cleared.Accepted, cleared.Error);
		Assert.DoesNotContain("TargetUpdated=\"greeting_TargetUpdated\"", (await client.FlushAsync(1, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesignResetProperty_RemovesTheExplicitValueFromSavedXaml()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var greetingPath = FindByName(opened.Tree!, "greeting")!.Id;

		var edited = await client.SetPropertyAsync(1, greetingPath, "Text", "Edited", timeout.Token);
		Assert.True(edited.Accepted, edited.Error);
		var reset = await client.ResetPropertyAsync(1, greetingPath, "Text", timeout.Token);
		Assert.True(reset.Accepted, reset.Error);

		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.DoesNotContain("Text=\"Edited\"", flushed.Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task EnumProperty_ReportsFiniteChoicesForTheRemotePropertiesPad()
	{
		const string enumXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Button x:Name="go" HorizontalAlignment="Left" Content="Go" />
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, enumXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var alignment = FindByName(opened.Tree!, "go")!.Properties.Single(property => property.Name == "HorizontalAlignment");
		Assert.Equal("Enum", alignment.Kind);
		Assert.True(alignment.IsEnum);
		Assert.Contains("Left", alignment.AllowedValues);
		Assert.Contains("Stretch", alignment.AllowedValues);
	}

	[Fact]
	public async Task ScalarWpfValues_ReportTheirCommonPropertyKinds()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var greeting = FindByName(opened.Tree!, "greeting")!;
		Assert.Equal("Brush", greeting.Properties.Single(property => property.Name == "Background").Kind);
		Assert.Equal("Thickness", greeting.Properties.Single(property => property.Name == "Margin").Kind);
		Assert.Equal("Number", greeting.Properties.Single(property => property.Name == "Width").Kind);
		Assert.Equal("Boolean", greeting.Properties.Single(property => property.Name == "IsEnabled").Kind);
	}

	[Fact]
	public async Task ScalarWpfValues_RoundTripFromTypedPropertyTextIntoSavedXaml()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var greeting = FindByName(opened.Tree!, "greeting")!;

		var brush = await client.SetPropertyAsync(1, greeting.Id, "Background", "Red", timeout.Token);
		Assert.True(brush.Accepted, brush.Error);
		var thickness = await client.SetPropertyAsync(1, greeting.Id, "Margin", "1,2,3,4", timeout.Token);
		Assert.True(thickness.Accepted, thickness.Error);

		var saved = (await client.FlushAsync(1, timeout.Token)).Files.Single().Text;
		Assert.Contains("Background=\"Red\"", saved, StringComparison.Ordinal);
		Assert.Contains("Margin=\"1,2,3,4\"", saved, StringComparison.Ordinal);
	}


	[Fact]
	public async Task MenuItem_Header_IsAnEditablePlainStringAndRoundTrips()
	{
		const string menuXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Menu><MenuItem x:Name="fileItem" Header="File" /></Menu>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, menuXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var item = FindByName(opened.Tree!, "fileItem")!;
		var header = item.Properties.Single(property => property.Name == "Header");
		Assert.Equal("String", header.Kind);
		Assert.Equal("File", header.Value);
		Assert.False(header.IsReadOnly);

		var edited = await client.SetPropertyAsync(1, item.Id, "Header", "Project", timeout.Token);
		Assert.True(edited.Accepted, edited.Error);
		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("Header=\"Project\"", flushed.Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AttachedContextMenu_IsExposedInTheDesignTree_AndItsMenuItemHeaderRoundTrips()
	{
		const string contextMenuXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Button x:Name="owner" Content="Open">
			    <Button.ContextMenu><ContextMenu x:Name="actions"><MenuItem x:Name="rename" Header="Rename" /></ContextMenu></Button.ContextMenu>
			  </Button>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, contextMenuXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var contextMenu = FindByName(opened.Tree!, "actions");
		var item = FindByName(opened.Tree!, "rename");
		Assert.NotNull(contextMenu);
		Assert.Equal("ContextMenu", contextMenu!.Type);
		Assert.NotNull(item);
		Assert.Equal("Rename", item!.Properties.Single(property => property.Name == "Header").Value);

		var edited = await client.SetPropertyAsync(1, item.Id, "Header", "Delete", timeout.Token);
		Assert.True(edited.Accepted, edited.Error);
		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("Header=\"Delete\"", flushed.Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AddMenuItem_AppendsASiblingUnderAnExistingMenu_WithTheGivenHeader()
	{
		const string menuXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Menu x:Name="mainMenu"><MenuItem x:Name="fileItem" Header="File" /></Menu>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, menuXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var menu = FindByName(opened.Tree!, "mainMenu")!;
		Assert.Single(menu.Children);

		var added = await client.AddMenuItemAsync(1, menu.Id, "Edit", timeout.Token);
		Assert.True(added.Accepted, added.Error);
		Assert.NotNull(added.CreatedElementId);
		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("Header=\"Edit\"", flushed.Files.Single().Text, StringComparison.Ordinal);

		var reopened = await client.UpdateAsync(Snapshot(2, flushed.Files.Single().Text), timeout.Token);
		var mainMenuAfter = FindByName(reopened.Tree!, "mainMenu")!;
		Assert.Equal(2, mainMenuAfter.Children.Count);
	}

	[Fact]
	public async Task AddMenuItem_AppendsASiblingUnderAnExistingContextMenu_WithTheGivenHeader()
	{
		const string contextMenuXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Button x:Name="owner" Content="Open">
			    <Button.ContextMenu><ContextMenu x:Name="actions"><MenuItem x:Name="rename" Header="Rename" /></ContextMenu></Button.ContextMenu>
			  </Button>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, contextMenuXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var contextMenu = FindByName(opened.Tree!, "actions")!;

		var added = await client.AddMenuItemAsync(1, contextMenu.Id, "Delete", timeout.Token);
		Assert.True(added.Accepted, added.Error);
		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("Header=\"Delete\"", flushed.Files.Single().Text, StringComparison.Ordinal);
		// The original item is untouched - this appended a sibling, not replaced the tray.
		Assert.Contains("Header=\"Rename\"", flushed.Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AddMenuItem_CreatesTheFirstNestedSubmenuItem_UnderAChildlessMenuItem()
	{
		const string menuXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Menu x:Name="mainMenu"><MenuItem x:Name="fileItem" Header="File" /></Menu>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, menuXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var fileItem = FindByName(opened.Tree!, "fileItem")!;
		Assert.Empty(fileItem.Children);

		var added = await client.AddMenuItemAsync(1, fileItem.Id, "Open", timeout.Token);
		Assert.True(added.Accepted, added.Error);
		Assert.NotNull(added.CreatedElementId);
		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("Header=\"Open\"", flushed.Files.Single().Text, StringComparison.Ordinal);

		var reopened = await client.UpdateAsync(Snapshot(2, flushed.Files.Single().Text), timeout.Token);
		var fileItemAfter = FindByName(reopened.Tree!, "fileItem")!;
		Assert.Single(fileItemAfter.Children);
		Assert.Equal("MenuItem", fileItemAfter.Children[0].Type);

		// A second nested item appends as a sibling of "Open" under the same (now non-empty) submenu.
		var addedSecond = await client.AddMenuItemAsync(2, fileItemAfter.Id, "Save", timeout.Token);
		Assert.True(addedSecond.Accepted, addedSecond.Error);
		var flushedSecond = await client.FlushAsync(2, timeout.Token);
		Assert.Contains("Header=\"Save\"", flushedSecond.Files.Single().Text, StringComparison.Ordinal);
		Assert.Contains("Header=\"Open\"", flushedSecond.Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AddMenuItem_WithEmptyHeader_IsRejected()
	{
		const string menuXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Menu x:Name="mainMenu"><MenuItem x:Name="fileItem" Header="File" /></Menu>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, menuXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var menu = FindByName(opened.Tree!, "mainMenu")!;

		var result = await client.AddMenuItemAsync(1, menu.Id, "   ", timeout.Token);
		Assert.False(result.Accepted);
	}

	[Fact]
	public async Task AddMenuItem_UnderANonMenuParent_IsRejected()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var result = await client.AddMenuItemAsync(1, opened.Tree!.Id, "New Item", timeout.Token);
		Assert.False(result.Accepted);
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
	public async Task Tree_ReportsNestedBindingValuesWithPathEditor()
	{
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <TextBlock x:Name="bound" Text="{Binding Title}" />
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);

		Assert.True(opened.Accepted, opened.Error);
		var text = FindByName(opened.Tree!, "bound")!.Properties.Single(property => property.Name == "Text");
		Assert.Equal("String", text.Kind);
		Assert.Equal("BindingPath", text.EditorKind);
		Assert.False(text.IsReadOnly);
		Assert.Equal("Title", text.Value);
	}

	[Fact]
	public async Task ChildHost_PreservesEscapedStringFormatAcrossBindingRewrite()
	{
		// Every accepted escape for a literal leading brace must parse to the same format string,
		// and rewriting the Binding (here, a Mode edit) must not corrupt or re-escape it.
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <TextBlock x:Name="a" Text="{Binding Price, StringFormat=\{0:N2\}}" />
			  <TextBlock x:Name="b" Text="{Binding Price, StringFormat='{}{0:N2}'}" />
			  <TextBlock x:Name="c" Text="{Binding Price, StringFormat={}{0:N2}}" />
			  <TextBlock x:Name="d" Text="{Binding Price, StringFormat='\{0:N2\}'}" />
			  <TextBlock x:Name="e" Text="{Binding Price, StringFormat='Total: {0}'}" />
			</Grid>
			""";
		var names = new[] { "a", "b", "c", "d", "e" };
		string Format(DesignerSessionState state, string name)
			=> FindByName(state.Tree!, name)!.Properties.Single(property => property.BindingOptionName == "StringFormat").Value;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var state = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(state.Accepted, state.Error);
		foreach (var name in names)
			Assert.Equal(name == "e" ? "Total: {0}" : "{0:N2}", Format(state, name));

		foreach (var name in names)
		{
			state = await client.SetBindingModeAsync(state.Version, FindByName(state.Tree!, name)!.Id, "Text", "OneWay", timeout.Token);
			Assert.True(state.Accepted, state.Error);
		}
		var flushed = (await client.FlushAsync(state.Version, timeout.Token)).Files.Single().Text;
		Assert.DoesNotContain(@"\{\}", flushed, StringComparison.Ordinal);
		var reloaded = await client.UpdateAsync(Snapshot(state.Version + 1, flushed), timeout.Token);
		Assert.True(reloaded.Accepted, reloaded.Error + "\n" + flushed);
		foreach (var name in names)
			Assert.Equal(name == "e" ? "Total: {0}" : "{0:N2}", Format(reloaded, name));
	}

	[Fact]
	public async Task ChildHost_EditsPlainListItemsAsOneCollection()
	{
		const string xaml = """
			<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="300" Height="300">
			  <ListBox x:Name="list">
			    <ListBoxItem x:Name="one" Content="One" />
			    <ListBoxItem>Two</ListBoxItem>
			  </ListBox>
			  <ListBox x:Name="bound" ItemsSource="{Binding People}" />
			  <ListBox x:Name="mixed"><Button Content="Not an item container" /></ListBox>
			</StackPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var state = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(state.Accepted, state.Error);
		DesignerPropertyInfo? Items(DesignerSessionState s, string name)
			=> FindByName(s.Tree!, name)!.Properties.SingleOrDefault(property => property.EditorKind == "ItemsCollection");
		var row = Items(state, "list")!;
		Assert.Equal(new[] { "One", "Two" }, row.CollectionItems);
		Assert.True(row.IsReadOnly);
		Assert.Null(Items(state, "bound"));
		Assert.Null(Items(state, "mixed"));
		var boundEdit = await client.SetItemsAsync(1, FindByName(state.Tree!, "bound")!.Id, new[] { new DesignerCollectionItemEdit { Text = "X" } }, timeout.Token);
		Assert.False(boundEdit.Accepted);

		var list = FindByName(state.Tree!, "list")!.Id;
		var badIndex = await client.SetItemsAsync(1, list, new[] { new DesignerCollectionItemEdit { SourceIndex = 5, Text = "X" } }, timeout.Token);
		Assert.False(badIndex.Accepted);
		var duplicate = await client.SetItemsAsync(1, list, new[] { new DesignerCollectionItemEdit { SourceIndex = 0, Text = "A" }, new DesignerCollectionItemEdit { SourceIndex = 0, Text = "B" } }, timeout.Token);
		Assert.False(duplicate.Accepted);

		// Reorder, rename, and add in one edit; the moved "one" keeps its x:Name.
		var edited = await client.SetItemsAsync(1, list, new[] {
			new DesignerCollectionItemEdit { SourceIndex = 1, Text = "Two!" },
			new DesignerCollectionItemEdit { SourceIndex = 0, Text = "One" },
			new DesignerCollectionItemEdit { SourceIndex = -1, Text = "Three" }
		}, timeout.Token);
		Assert.True(edited.Accepted, edited.Error);
		Assert.Equal(new[] { "Two!", "One", "Three" }, Items(edited, "list")!.CollectionItems);
		var text = (await client.FlushAsync(edited.Version, timeout.Token)).Files.Single().Text;
		Assert.True(text.IndexOf("Two!", StringComparison.Ordinal) < text.IndexOf("x:Name=\"one\"", StringComparison.Ordinal), text);
		Assert.True(text.IndexOf("x:Name=\"one\"", StringComparison.Ordinal) < text.IndexOf("Three", StringComparison.Ordinal), text);
		var reloaded = await client.UpdateAsync(Snapshot(edited.Version + 1, text), timeout.Token);
		Assert.True(reloaded.Accepted, reloaded.Error + "\n" + text);
		Assert.Equal(new[] { "Two!", "One", "Three" }, Items(reloaded, "list")!.CollectionItems);

		var cleared = await client.SetItemsAsync(reloaded.Version, FindByName(reloaded.Tree!, "list")!.Id, Array.Empty<DesignerCollectionItemEdit>(), timeout.Token);
		Assert.True(cleared.Accepted, cleared.Error);
		Assert.Empty(Items(cleared, "list")!.CollectionItems);
		Assert.DoesNotContain("ListBoxItem", (await client.FlushAsync(cleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_EditsTabControlPageHeadersKeepingTheirBodies()
	{
		const string xaml = """
			<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="300" Height="300">
			  <TabControl x:Name="tabs">
			    <TabItem Header="General"><TextBlock x:Name="generalBody" Text="General body" /></TabItem>
			    <TabItem Header="Advanced"><TextBlock x:Name="advancedBody" Text="Advanced body" /></TabItem>
			  </TabControl>
			  <TabControl x:Name="rich">
			    <TabItem><TabItem.Header><TextBlock Text="Rich" /></TabItem.Header></TabItem>
			  </TabControl>
			</StackPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var state = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(state.Accepted, state.Error);
		DesignerPropertyInfo? Items(DesignerSessionState s, string name)
			=> FindByName(s.Tree!, name)!.Properties.SingleOrDefault(property => property.EditorKind == "ItemsCollection");
		Assert.Equal(new[] { "General", "Advanced" }, Items(state, "tabs")!.CollectionItems);
		// A header that is an element, not text, is not a plain list.
		Assert.Null(Items(state, "rich"));

		var edited = await client.SetItemsAsync(1, FindByName(state.Tree!, "tabs")!.Id, new[] {
			new DesignerCollectionItemEdit { SourceIndex = 1, Text = "Expert" },
			new DesignerCollectionItemEdit { SourceIndex = 0, Text = "General" },
			new DesignerCollectionItemEdit { SourceIndex = -1, Text = "New page" }
		}, timeout.Token);
		Assert.True(edited.Accepted, edited.Error);
		Assert.Equal(new[] { "Expert", "General", "New page" }, Items(edited, "tabs")!.CollectionItems);
		var text = (await client.FlushAsync(edited.Version, timeout.Token)).Files.Single().Text;
		// Each page keeps its body when it moves; only the headers changed.
		Assert.True(text.IndexOf("Header=\"Expert\"", StringComparison.Ordinal) < text.IndexOf("x:Name=\"advancedBody\"", StringComparison.Ordinal), text);
		Assert.True(text.IndexOf("x:Name=\"advancedBody\"", StringComparison.Ordinal) < text.IndexOf("Header=\"General\"", StringComparison.Ordinal), text);
		Assert.True(text.IndexOf("x:Name=\"generalBody\"", StringComparison.Ordinal) < text.IndexOf("Header=\"New page\"", StringComparison.Ordinal), text);
		Assert.Contains("<TabItem Header=\"New page\" />", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_WrapsAndUnwrapsElementsKeepingTheirPlacement()
	{
		const string xaml = """
			<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="200">
			  <Button x:Name="a" Canvas.Left="40" Canvas.Top="30" Width="80" Height="30" Content="A" />
			  <Button x:Name="b" Canvas.Left="160" Canvas.Top="90" Width="80" Height="30" Content="B" />
			  <Button x:Name="c" Canvas.Left="300" Canvas.Top="20" Width="60" Height="30" Content="C" />
			</Canvas>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var state = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(state.Accepted, state.Error);
		(double X, double Y) Position(DesignerSessionState s, string name)
		{
			var node = FindByName(s.Tree!, name)!;
			return (node.X, node.Y);
		}
		var before = (A: Position(state, "a"), B: Position(state, "b"));

		var wrapped = await client.WrapElementsAsync(state.Version, new[] { FindByName(state.Tree!, "a")!.Id, FindByName(state.Tree!, "b")!.Id }, "Grid", timeout.Token);
		Assert.True(wrapped.Accepted, wrapped.Error);
		var grid = FindNodeById(wrapped.Tree!, wrapped.CreatedElementId!)!;
		Assert.Equal("Grid", grid.Type);
		Assert.Contains(grid.Children, child => child.Name == "a");
		Assert.Contains(grid.Children, child => child.Name == "b");
		// Wrapping keeps both buttons where they were rendered.
		Assert.Equal(before.A, Position(wrapped, "a"));
		Assert.Equal(before.B, Position(wrapped, "b"));
		var wrappedText = (await client.FlushAsync(wrapped.Version, timeout.Token)).Files.Single().Text;
		Assert.Contains("<Grid", wrappedText, StringComparison.Ordinal);

		var unwrapped = await client.UnwrapElementAsync(wrapped.Version, grid.Id, timeout.Token);
		Assert.True(unwrapped.Accepted, unwrapped.Error);
		Assert.DoesNotContain("<Grid", (await client.FlushAsync(unwrapped.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		Assert.Equal(before.A, Position(unwrapped, "a"));
		Assert.Equal(before.B, Position(unwrapped, "b"));

		var viewbox = await client.WrapElementsAsync(unwrapped.Version, new[] { FindByName(unwrapped.Tree!, "c")!.Id }, "Viewbox", timeout.Token);
		Assert.True(viewbox.Accepted, viewbox.Error);
		Assert.Equal("Viewbox", FindNodeById(viewbox.Tree!, viewbox.CreatedElementId!)!.Type);
		// A single wrapped element keeps its place in the parent's child order: "c" was first.
		var viewboxText = (await client.FlushAsync(viewbox.Version, timeout.Token)).Files.Single().Text;
		Assert.True(viewboxText.IndexOf("<Viewbox", StringComparison.Ordinal) < viewboxText.IndexOf("x:Name=\"a\"", StringComparison.Ordinal), viewboxText);
		Assert.True(viewboxText.IndexOf("x:Name=\"c\"", StringComparison.Ordinal) < viewboxText.IndexOf("x:Name=\"a\"", StringComparison.Ordinal), viewboxText);

		var twoInViewbox = await client.WrapElementsAsync(viewbox.Version, new[] { FindByName(viewbox.Tree!, "a")!.Id, FindByName(viewbox.Tree!, "b")!.Id }, "Viewbox", timeout.Token);
		Assert.False(twoInViewbox.Accepted);
		var root = await client.WrapElementsAsync(viewbox.Version, new[] { viewbox.Tree!.Id }, "Grid", timeout.Token);
		Assert.False(root.Accepted);
		var unwrapButton = await client.UnwrapElementAsync(viewbox.Version, FindByName(viewbox.Tree!, "a")!.Id, timeout.Token);
		Assert.False(unwrapButton.Accepted);

		// Inside a Grid parent the children are placed by Margin; unwrapping must add the
		// container's own offset there too.
		const string gridXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="200">
			  <Button x:Name="g1" HorizontalAlignment="Left" VerticalAlignment="Top" Margin="50,40,0,0" Width="80" Height="30" Content="1" />
			  <Button x:Name="g2" HorizontalAlignment="Left" VerticalAlignment="Top" Margin="170,100,0,0" Width="80" Height="30" Content="2" />
			</Grid>
			""";
		var gridState = await client.UpdateAsync(Snapshot(viewbox.Version + 1, gridXaml), timeout.Token);
		Assert.True(gridState.Accepted, gridState.Error);
		var gridBefore = (A: Position(gridState, "g1"), B: Position(gridState, "g2"));
		var inCanvas = await client.WrapElementsAsync(gridState.Version, new[] { FindByName(gridState.Tree!, "g1")!.Id, FindByName(gridState.Tree!, "g2")!.Id }, "Canvas", timeout.Token);
		Assert.True(inCanvas.Accepted, inCanvas.Error);
		Assert.Equal(gridBefore.A, Position(inCanvas, "g1"));
		var outOfCanvas = await client.UnwrapElementAsync(inCanvas.Version, inCanvas.CreatedElementId!, timeout.Token);
		Assert.True(outOfCanvas.Accepted, outOfCanvas.Error);
		Assert.Equal(gridBefore.A, Position(outOfCanvas, "g1"));
		Assert.Equal(gridBefore.B, Position(outOfCanvas, "g2"));
	}

	[Fact]
	public async Task ChildHost_ChangesZOrderWithinTheParentCollection()
	{
		const string xaml = """
			<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Button x:Name="a" Content="A" />
			  <Button x:Name="b" Content="B" />
			  <Button x:Name="c" Content="C" />
			</Canvas>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var state = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(state.Accepted, state.Error);
		async Task<string> Order(DesignerSessionState s)
		{
			var text = (await client.FlushAsync(s.Version, timeout.Token)).Files.Single().Text;
			return string.Concat(new[] { "a", "b", "c" }.OrderBy(name => text.IndexOf("x:Name=\"" + name + "\"", StringComparison.Ordinal)));
		}
		async Task<DesignerSessionState> Apply(DesignerSessionState s, string name, string placement)
		{
			var result = await client.SetZOrderAsync(s.Version, FindByName(s.Tree!, name)!.Id, placement, timeout.Token);
			Assert.True(result.Accepted, placement + ": " + result.Error);
			return result;
		}

		state = await Apply(state, "a", "front");
		Assert.Equal("bca", await Order(state));
		// The moved element's id changed with its position; the response names the new one.
		Assert.Equal(FindByName(state.Tree!, "a")!.Id, state.MovedElementId);
		state = await Apply(state, "a", "back");
		Assert.Equal("abc", await Order(state));
		state = await Apply(state, "a", "forward");
		Assert.Equal("bac", await Order(state));
		state = await Apply(state, "a", "backward");
		Assert.Equal("abc", await Order(state));

		var alreadyBack = await client.SetZOrderAsync(state.Version, FindByName(state.Tree!, "a")!.Id, "back", timeout.Token);
		Assert.False(alreadyBack.Accepted);
		Assert.Contains("already at the back", alreadyBack.Error, StringComparison.Ordinal);
		var unsupported = await client.SetZOrderAsync(state.Version, FindByName(state.Tree!, "a")!.Id, "sideways", timeout.Token);
		Assert.False(unsupported.Accepted);
		Assert.Equal("abc", await Order(state));
	}

	[Fact]
	public async Task ChildHost_EditsBindingConverterAsAStaticResourceInScope()
	{
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Grid.Resources>
			    <BooleanToVisibilityConverter x:Key="visible" />
			    <BooleanToVisibilityConverter x:Key="shown" />
			    <SolidColorBrush x:Key="brush" Color="Red" />
			  </Grid.Resources>
			  <TextBlock x:Name="bound" Text="{Binding Name}" />
			  <TextBlock x:Name="inline" Text="Inline">
			    <TextBlock.Visibility>
			      <Binding Path="IsOn"><Binding.Converter><BooleanToVisibilityConverter /></Binding.Converter></Binding>
			    </TextBlock.Visibility>
			  </TextBlock>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		DesignerPropertyInfo Converter(DesignerSessionState state, string name)
			=> FindByName(state.Tree!, name)!.Properties.Single(property => property.EditorKind == "BindingConverter");
		var row = Converter(opened, "bound");
		Assert.Equal("Enum", row.Kind);
		Assert.Equal("", row.Value);
		// Only IValueConverter resources are offered, never the brush.
		// Name order: the dictionary itself enumerates its keys in hash order.
		Assert.Equal(new[] { "", "shown", "visible" }, row.AllowedValues);
		// An inline converter object is not a StaticResource reference: no row, and no overwrite.
		Assert.DoesNotContain(FindByName(opened.Tree!, "inline")!.Properties, property => property.EditorKind == "BindingConverter");
		var inline = FindByName(opened.Tree!, "inline")!;
		var overwrite = await client.SetBindingConverterAsync(1, inline.Id, "Visibility", "visible", timeout.Token);
		Assert.False(overwrite.Accepted);

		var bound = FindByName(opened.Tree!, "bound")!;
		var set = await client.SetBindingConverterAsync(1, bound.Id, "Text", "visible", timeout.Token);
		Assert.True(set.Accepted, set.Error);
		var text = (await client.FlushAsync(set.Version, timeout.Token)).Files.Single().Text;
		Assert.Contains("Converter={StaticResource visible}", text, StringComparison.Ordinal);
		Assert.Contains("<BooleanToVisibilityConverter />", text, StringComparison.Ordinal);

		var reloaded = await client.UpdateAsync(Snapshot(set.Version + 1, text), timeout.Token);
		Assert.True(reloaded.Accepted, reloaded.Error + "\n" + text);
		Assert.Equal("visible", Converter(reloaded, "bound").Value);

		var switched = await client.SetBindingConverterAsync(reloaded.Version, FindByName(reloaded.Tree!, "bound")!.Id, "Text", "shown", timeout.Token);
		Assert.True(switched.Accepted, switched.Error);
		Assert.Contains("Converter={StaticResource shown}", (await client.FlushAsync(switched.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var outOfScope = await client.SetBindingConverterAsync(switched.Version, FindByName(switched.Tree!, "bound")!.Id, "Text", "brush", timeout.Token);
		Assert.False(outOfScope.Accepted);
		Assert.Contains("Not an IValueConverter resource in scope", outOfScope.Error, StringComparison.Ordinal);
		var cleared = await client.SetBindingConverterAsync(switched.Version, FindByName(switched.Tree!, "bound")!.Id, "Text", "", timeout.Token);
		Assert.True(cleared.Accepted, cleared.Error);
		var clearedText = (await client.FlushAsync(cleared.Version, timeout.Token)).Files.Single().Text;
		Assert.DoesNotContain("Converter=", clearedText.Substring(clearedText.IndexOf("x:Name=\"bound\"", StringComparison.Ordinal)).Split('\n')[0], StringComparison.Ordinal);
		Assert.Equal("", Converter(cleared, "bound").Value);
	}

	[Fact]
	public async Task ChildHost_EditsBindingTextOptionsWithoutOverwritingMarkupValues()
	{
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
			      xmlns:sys="clr-namespace:System;assembly=mscorlib" Width="240" Height="80">
			  <TextBlock x:Name="bound" Text="{Binding Price}" />
			  <TextBlock x:Name="static" Text="{Binding Price, FallbackValue={x:Static sys:String.Empty}}" />
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var bound = FindByName(opened.Tree!, "bound")!;
		var options = bound.Properties.Where(property => property.EditorKind == "BindingTextOption")
			.Select(property => property.BindingOptionName).ToArray();
		Assert.Equal(new[] { "StringFormat", "FallbackValue", "TargetNullValue", "ConverterParameter", "BindingGroupName", "XPath" }, options);
		Assert.DoesNotContain(FindByName(opened.Tree!, "static")!.Properties,
			property => property.EditorKind == "BindingTextOption" && property.BindingOptionName == "FallbackValue");

		var formatted = await client.SetBindingTextOptionAsync(1, bound.Id, "Text", "StringFormat", "{0:N2}", timeout.Token);
		Assert.True(formatted.Accepted, formatted.Error);
		var fallback = await client.SetBindingTextOptionAsync(formatted.Version, bound.Id, "Text", "FallbackValue", "N/A", timeout.Token);
		Assert.True(fallback.Accepted, fallback.Error);
		var grouped = await client.SetBindingTextOptionAsync(fallback.Version, bound.Id, "Text", "BindingGroupName", "checkout", timeout.Token);
		Assert.True(grouped.Accepted, grouped.Error);
		var xpath = await client.SetBindingTextOptionAsync(grouped.Version, bound.Id, "Text", "XPath", "/order/total", timeout.Token);
		Assert.True(xpath.Accepted, xpath.Error);
		var text = (await client.FlushAsync(xpath.Version, timeout.Token)).Files.Single().Text;
		// Braces inside a markup-extension argument are escaped, not emitted raw.
		Assert.Contains(@"StringFormat=\{0:N2\}", text, StringComparison.Ordinal);
		Assert.Contains("FallbackValue=N/A", text, StringComparison.Ordinal);
		Assert.Contains("BindingGroupName=checkout", text, StringComparison.Ordinal);
		Assert.Contains("XPath=/order/total", text, StringComparison.Ordinal);
		Assert.Equal("{0:N2}", FindByName(fallback.Tree!, "bound")!.Properties
			.Single(property => property.BindingOptionName == "StringFormat").Value);

		// The flushed markup must reload as the same Binding rather than a parse error.
		var reopened = await client.UpdateAsync(Snapshot(xpath.Version + 1, text), timeout.Token);
		Assert.True(reopened.Accepted, reopened.Error + "\n" + text);
		Assert.Equal("{0:N2}", FindByName(reopened.Tree!, "bound")!.Properties
			.Single(property => property.BindingOptionName == "StringFormat").Value);

		// A literal that itself begins with "{}" is content, not an escape: it must survive a
		// write, save and reload unchanged.
		var literalBraces = await client.SetBindingTextOptionAsync(reopened.Version, FindByName(reopened.Tree!, "bound")!.Id, "Text", "TargetNullValue", "{}{0}", timeout.Token);
		Assert.True(literalBraces.Accepted, literalBraces.Error);
		var literalText = (await client.FlushAsync(literalBraces.Version, timeout.Token)).Files.Single().Text;
		reopened = await client.UpdateAsync(Snapshot(literalBraces.Version + 1, literalText), timeout.Token);
		Assert.True(reopened.Accepted, reopened.Error + "\n" + literalText);
		Assert.Equal("{}{0}", FindByName(reopened.Tree!, "bound")!.Properties
			.Single(property => property.BindingOptionName == "TargetNullValue").Value);
		// An object-typed option holding a leading-brace literal must not be re-read as an extension.
		var objectLiteral = await client.SetBindingTextOptionAsync(reopened.Version, FindByName(reopened.Tree!, "bound")!.Id, "Text", "TargetNullValue", "{0}", timeout.Token);
		Assert.True(objectLiteral.Accepted, objectLiteral.Error);
		literalText = (await client.FlushAsync(objectLiteral.Version, timeout.Token)).Files.Single().Text;
		reopened = await client.UpdateAsync(Snapshot(objectLiteral.Version + 1, literalText), timeout.Token);
		Assert.True(reopened.Accepted, reopened.Error + "\n" + literalText);
		Assert.Equal("{0}", FindByName(reopened.Tree!, "bound")!.Properties
			.Single(property => property.BindingOptionName == "TargetNullValue").Value);

		// Values containing an argument separator, '=' or a quote must stay one argument through
		// save and reload (a comma format such as {0:#,0} is common).
		foreach (var (option, value) in new[] {
			("ConverterParameter", "a,b"), ("ConverterParameter", "it's x=y"), ("StringFormat", "{0:#,0}"),
			// A quoted value ending in a backslash must not swallow its closing quote.
			("ConverterParameter", @"a'\"), ("ConverterParameter", @"say ""hi""\"), ("ConverterParameter", @"C:\Folder A\file.txt") })
		{
			var edit = await client.SetBindingTextOptionAsync(reopened.Version, FindByName(reopened.Tree!, "bound")!.Id, "Text", option, value, timeout.Token);
			Assert.True(edit.Accepted, edit.Error);
			literalText = (await client.FlushAsync(edit.Version, timeout.Token)).Files.Single().Text;
			reopened = await client.UpdateAsync(Snapshot(edit.Version + 1, literalText), timeout.Token);
			Assert.True(reopened.Accepted, reopened.Error + "\n" + literalText);
			var reloaded = FindByName(reopened.Tree!, "bound")!.Properties.SingleOrDefault(property => property.BindingOptionName == option);
			Assert.True(reloaded?.Value == value, option + " did not round-trip:\n" + literalText);
		}

		var bound2 = FindByName(reopened.Tree!, "bound")!;
		var cleared = await client.SetBindingTextOptionAsync(reopened.Version, bound2.Id, "Text", "FallbackValue", "", timeout.Token);
		Assert.True(cleared.Accepted, cleared.Error);
		Assert.DoesNotContain("FallbackValue=N/A", (await client.FlushAsync(cleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);

		var staticItem = FindByName(cleared.Tree!, "static")!;
		var overwrite = await client.SetBindingTextOptionAsync(cleared.Version, staticItem.Id, "Text", "FallbackValue", "N/A", timeout.Token);
		Assert.False(overwrite.Accepted);
		Assert.Contains("x:Static", (await client.FlushAsync(cleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var unsupported = await client.SetBindingTextOptionAsync(cleared.Version, bound2.Id, "Text", "Path", "Other", timeout.Token);
		Assert.False(unsupported.Accepted);
		Assert.Contains("Unsupported Binding text option", unsupported.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_UpdatesDirectCompositeBindingPathsWithoutReplacingTheBindingGraph()
	{
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			<Grid.Resources><BooleanToVisibilityConverter x:Key="visible"/></Grid.Resources>
			<TextBlock x:Name="source" Text="source"/>
			<TextBlock x:Name="multi"><TextBlock.Text><MultiBinding StringFormat="{}{0} {1}"><Binding Path="First"/><Binding Path="Last" Mode="OneWay"/></MultiBinding></TextBlock.Text></TextBlock>
			  <TextBlock x:Name="priority"><TextBlock.Text><PriorityBinding><Binding Path="Preferred"/><Binding Path="Fallback"/></PriorityBinding></TextBlock.Text></TextBlock>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var multi = FindByName(opened.Tree!, "multi")!;
		var collection = multi.Properties.Single(property => property.EditorKind == "BindingCollection");
		Assert.Equal("Text", collection.BindingPropertyName);
		Assert.Equal(new[] { "First", "Last" }, collection.CollectionItems);
		var compositeMode = multi.Properties.Single(property => property.EditorKind == "BindingMode");
		Assert.Equal("Text", compositeMode.BindingPropertyName);
		var compositeModeEdited = await client.SetBindingModeAsync(opened.Version, multi.Id, "Text", "OneWay", timeout.Token);
		Assert.True(compositeModeEdited.Accepted, compositeModeEdited.Error);
		var compositeModeText = (await client.FlushAsync(compositeModeEdited.Version, timeout.Token)).Files.Single().Text;
		Assert.Matches("<MultiBinding(?=[^>]*Mode=\"OneWay\")", compositeModeText);
		var compositeTrigger = FindByName(compositeModeEdited.Tree!, "multi")!.Properties
			.Single(property => property.EditorKind == "BindingUpdateSourceTrigger");
		Assert.Equal("Text", compositeTrigger.BindingPropertyName);
		var compositeTriggerEdited = await client.SetBindingUpdateSourceTriggerAsync(compositeModeEdited.Version, multi.Id, "Text", "PropertyChanged", timeout.Token);
		Assert.True(compositeTriggerEdited.Accepted, compositeTriggerEdited.Error);
		var compositeTriggerText = (await client.FlushAsync(compositeTriggerEdited.Version, timeout.Token)).Files.Single().Text;
		Assert.Matches("<MultiBinding(?=[^>]*Mode=\"OneWay\")(?=[^>]*UpdateSourceTrigger=\"PropertyChanged\")", compositeTriggerText);
		opened = compositeTriggerEdited;
		multi = FindByName(opened.Tree!, "multi")!;
		var childMode = multi.Properties.Single(property => property.EditorKind == "BindingChildMode" && property.BindingChildIndex == 0);
		Assert.Equal("Text", childMode.BindingPropertyName);
		Assert.Contains("OneWay", childMode.AllowedValues);
		var modeEdited = await client.SetBindingModeAsync(opened.Version, multi.Id, "Text", "OneWay", 0, timeout.Token);
		Assert.True(modeEdited.Accepted, modeEdited.Error);
		var modeText = (await client.FlushAsync(modeEdited.Version, timeout.Token)).Files.Single().Text;
		Assert.Matches("<Binding(?=[^>]*Path=\"First\")(?=[^>]*Mode=\"OneWay\")", modeText);
		var childTrigger = FindByName(modeEdited.Tree!, "multi")!.Properties.Single(property => property.EditorKind == "BindingChildUpdateSourceTrigger" && property.BindingChildIndex == 0);
		Assert.Equal("Text", childTrigger.BindingPropertyName);
		Assert.Contains("PropertyChanged", childTrigger.AllowedValues);
		var triggerEdited = await client.SetBindingUpdateSourceTriggerAsync(modeEdited.Version, multi.Id, "Text", "PropertyChanged", 0, timeout.Token);
		Assert.True(triggerEdited.Accepted, triggerEdited.Error);
		var triggerText = (await client.FlushAsync(triggerEdited.Version, timeout.Token)).Files.Single().Text;
		Assert.Matches("<Binding(?=[^>]*Path=\"First\")(?=[^>]*UpdateSourceTrigger=\"PropertyChanged\")", triggerText);
		var childElementName = FindByName(triggerEdited.Tree!, "multi")!.Properties.Single(property => property.EditorKind == "BindingChildElementName" && property.BindingChildIndex == 0);
		Assert.Equal("Text", childElementName.BindingPropertyName);
		var elementNameEdited = await client.SetBindingElementNameAsync(triggerEdited.Version, multi.Id, "Text", "source", 0, timeout.Token);
		Assert.True(elementNameEdited.Accepted, elementNameEdited.Error);
		var elementNameText = (await client.FlushAsync(elementNameEdited.Version, timeout.Token)).Files.Single().Text;
		Assert.Matches("<Binding(?=[^>]*Path=\"First\")(?=[^>]*ElementName=\"source\")", elementNameText);
		var childConverter = FindByName(elementNameEdited.Tree!, "multi")!.Properties.Single(property => property.EditorKind == "BindingChildConverter" && property.BindingChildIndex == 0);
		Assert.Equal("Text", childConverter.BindingPropertyName);
		Assert.Contains("visible", childConverter.AllowedValues);
		var converterEdited = await client.SetBindingConverterAsync(elementNameEdited.Version, multi.Id, "Text", "visible", 0, timeout.Token);
		Assert.True(converterEdited.Accepted, converterEdited.Error);
		var converterText = (await client.FlushAsync(converterEdited.Version, timeout.Token)).Files.Single().Text;
		Assert.Matches("<Binding(?=[^>]*Path=\"First\")(?=[^>]*ElementName=\"source\")(?=[^>]*Converter=\"{StaticResource visible}\")", converterText);
		var converterCleared = await client.SetBindingConverterAsync(converterEdited.Version, multi.Id, "Text", "", 0, timeout.Token);
		Assert.True(converterCleared.Accepted, converterCleared.Error);
		Assert.DoesNotContain("Converter=\"{StaticResource visible}\"", (await client.FlushAsync(converterCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		elementNameEdited = converterCleared;
		var childFormat = FindByName(elementNameEdited.Tree!, "multi")!.Properties.Single(property => property.EditorKind == "BindingChildTextOption" && property.BindingChildIndex == 0 && property.BindingOptionName == "StringFormat");
		Assert.Equal("Text", childFormat.BindingPropertyName);
		var textOptionEdited = await client.SetBindingTextOptionAsync(elementNameEdited.Version, multi.Id, "Text", "StringFormat", "prefix {0}", 0, timeout.Token);
		Assert.True(textOptionEdited.Accepted, textOptionEdited.Error);
		var textOptionText = (await client.FlushAsync(textOptionEdited.Version, timeout.Token)).Files.Single().Text;
		Assert.Matches("<Binding(?=[^>]*Path=\"First\")(?=[^>]*ElementName=\"source\")(?=[^>]*StringFormat=\"prefix \\{0\\}\")", textOptionText);
		var textOptionCleared = await client.SetBindingTextOptionAsync(textOptionEdited.Version, multi.Id, "Text", "StringFormat", "", 0, timeout.Token);
		Assert.True(textOptionCleared.Accepted, textOptionCleared.Error);
		Assert.DoesNotContain("StringFormat=\"prefix {0}\"", (await client.FlushAsync(textOptionCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		elementNameEdited = textOptionCleared;
		var invalidTextChild = await client.SetBindingTextOptionAsync(elementNameEdited.Version, multi.Id, "Text", "StringFormat", "bad", 9, timeout.Token);
		Assert.False(invalidTextChild.Accepted);
		Assert.Contains("Binding child", invalidTextChild.Error, StringComparison.Ordinal);
		var childValidation = FindByName(elementNameEdited.Tree!, "multi")!.Properties.Single(property => property.EditorKind == "BindingChildValidationOption" && property.BindingChildIndex == 0 && property.BindingOptionName == "ValidatesOnExceptions");
		Assert.Equal("Text", childValidation.BindingPropertyName);
		var validationEdited = await client.SetBindingValidationOptionAsync(elementNameEdited.Version, multi.Id, "Text", "ValidatesOnExceptions", true, 0, timeout.Token);
		Assert.True(validationEdited.Accepted, validationEdited.Error);
		Assert.Matches("<Binding(?=[^>]*Path=\"First\")(?=[^>]*ValidatesOnExceptions=\"True\")", (await client.FlushAsync(validationEdited.Version, timeout.Token)).Files.Single().Text);
		var validationCleared = await client.SetBindingValidationOptionAsync(validationEdited.Version, multi.Id, "Text", "ValidatesOnExceptions", false, 0, timeout.Token);
		Assert.True(validationCleared.Accepted, validationCleared.Error);
		Assert.DoesNotContain("ValidatesOnExceptions", (await client.FlushAsync(validationCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var childDelay = FindByName(validationCleared.Tree!, "multi")!.Properties.Single(property => property.EditorKind == "BindingChildDelay" && property.BindingChildIndex == 0);
		Assert.Equal("Text", childDelay.BindingPropertyName);
		var delayEdited = await client.SetBindingDelayAsync(validationCleared.Version, multi.Id, "Text", 250, 0, timeout.Token);
		Assert.True(delayEdited.Accepted, delayEdited.Error);
		Assert.Matches("<Binding(?=[^>]*Path=\"First\")(?=[^>]*Delay=\"250\")", (await client.FlushAsync(delayEdited.Version, timeout.Token)).Files.Single().Text);
		var delayCleared = await client.SetBindingDelayAsync(delayEdited.Version, multi.Id, "Text", 0, 0, timeout.Token);
		Assert.True(delayCleared.Accepted, delayCleared.Error);
		Assert.DoesNotContain("Delay=\"250\"", (await client.FlushAsync(delayCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		elementNameEdited = delayCleared;
		var invalidChild = await client.SetBindingModeAsync(elementNameEdited.Version, multi.Id, "Text", "TwoWay", 9, timeout.Token);
		Assert.False(invalidChild.Accepted);
		Assert.Contains("Binding child", invalidChild.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("Mode=\"TwoWay\"", (await client.FlushAsync(modeEdited.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		opened = elementNameEdited;
		multi = FindByName(opened.Tree!, "multi")!;
		var multiPaths = multi.Properties.Where(property => property.EditorKind == "BindingChildPath").OrderBy(property => property.BindingChildIndex).ToArray();
		Assert.Equal(new[] { "First", "Last" }, multiPaths.Select(property => property.Value));
		Assert.Equal(new[] { 0, 1 }, multiPaths.Select(property => property.BindingChildIndex));
		var edited = await client.SetBindingChildPathAsync(opened.Version, multi.Id, "Text", 1, "FamilyName", timeout.Token);
		Assert.True(edited.Accepted, edited.Error);
		var text = (await client.FlushAsync(edited.Version, timeout.Token)).Files.Single().Text;
		Assert.Contains("<MultiBinding", text, StringComparison.Ordinal);
		Assert.Contains("Path=\"First\"", text, StringComparison.Ordinal);
		Assert.Contains("Path=\"FamilyName\"", text, StringComparison.Ordinal);
		Assert.Contains("Mode=\"OneWay\"", text, StringComparison.Ordinal);
		var collectionEdited = await client.SetBindingItemsAsync(edited.Version, multi.Id, "Text", new[] {
			new DesignerCollectionItemEdit { SourceIndex = 1, Text = "Surname" },
			new DesignerCollectionItemEdit { SourceIndex = -1, Text = "FullName" },
			new DesignerCollectionItemEdit { SourceIndex = 0, Text = "GivenName" }
		}, timeout.Token);
		Assert.True(collectionEdited.Accepted, collectionEdited.Error);
		text = (await client.FlushAsync(collectionEdited.Version, timeout.Token)).Files.Single().Text;
		Assert.Matches("<Binding(?=[^>]*Path=\"Surname\")(?=[^>]*Mode=\"OneWay\")", text);
		Assert.Contains("Path=\"FullName\"", text, StringComparison.Ordinal);
		Assert.Contains("Path=\"GivenName\"", text, StringComparison.Ordinal);
		var reordered = FindByName(collectionEdited.Tree!, "multi")!.Properties.Single(property => property.EditorKind == "BindingCollection");
		Assert.Equal(new[] { "Surname", "FullName", "GivenName" }, reordered.CollectionItems);
		var invalidSources = await client.SetBindingItemsAsync(collectionEdited.Version, multi.Id, "Text", new[] {
			new DesignerCollectionItemEdit { SourceIndex = 0, Text = "First" },
			new DesignerCollectionItemEdit { SourceIndex = 0, Text = "Duplicate" }
		}, timeout.Token);
		Assert.False(invalidSources.Accepted);
		Assert.Contains("source index", invalidSources.Error, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Duplicate", (await client.FlushAsync(collectionEdited.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var invalidNew = await client.SetBindingItemsAsync(collectionEdited.Version, multi.Id, "Text", new[] {
			new DesignerCollectionItemEdit { SourceIndex = -2, Text = "Invalid" }
		}, timeout.Token);
		Assert.False(invalidNew.Accepted);
		Assert.Contains("source index", invalidNew.Error, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Invalid", (await client.FlushAsync(collectionEdited.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var empty = await client.SetBindingItemsAsync(collectionEdited.Version, multi.Id, "Text", Array.Empty<DesignerCollectionItemEdit>(), timeout.Token);
		Assert.False(empty.Accepted);
		Assert.Contains("at least one", empty.Error, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("FullName", (await client.FlushAsync(collectionEdited.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var removed = await client.SetBindingItemsAsync(collectionEdited.Version, multi.Id, "Text", new[] {
			new DesignerCollectionItemEdit { SourceIndex = 0, Text = "OnlyLast" }
		}, timeout.Token);
		Assert.True(removed.Accepted, removed.Error);
		text = (await client.FlushAsync(removed.Version, timeout.Token)).Files.Single().Text;
		Assert.Contains("Path=\"OnlyLast\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain("FullName", text, StringComparison.Ordinal);
		Assert.Single(FindByName(removed.Tree!, "multi")!.Properties.Single(property => property.EditorKind == "BindingCollection").CollectionItems);
		var priority = FindByName(removed.Tree!, "priority")!;
		Assert.DoesNotContain(priority.Properties, property => property.EditorKind is "BindingMode" or "BindingUpdateSourceTrigger");
		var priorityPaths = priority.Properties.Where(property => property.EditorKind == "BindingChildPath").OrderBy(property => property.BindingChildIndex).ToArray();
		Assert.Equal(new[] { "Preferred", "Fallback" }, priorityPaths.Select(property => property.Value));
		var rejected = await client.SetBindingChildPathAsync(removed.Version, multi.Id, "Text", 1, "Nope", timeout.Token);
		Assert.False(rejected.Accepted);
		Assert.Contains("Binding child", rejected.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("Nope", (await client.FlushAsync(removed.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_UpdatesBindingPathWithoutReplacingBindingObject()
	{
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <TextBlock x:Name="source" Text="A source" />
			  <TextBlock x:Name="bound" Text="{Binding Title}" />
			  <TextBlock x:Name="relative" Text="{Binding RelativeSource={RelativeSource Self}, Path=Text}" />
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.IsAssignableFrom<IDesignHostBindingPath>(client);
		var bound = FindByName(opened.Tree!, "bound")!;
		var relative = FindByName(opened.Tree!, "relative")!;
		Assert.DoesNotContain(relative.Properties,
			property => property.EditorKind == "BindingElementName");
		var relativeSource = relative.Properties.Single(property => property.EditorKind == "BindingRelativeSource");
		Assert.Equal("Self", relativeSource.Value);
		Assert.Equal(new[] { "", "Self", "TemplatedParent" }, relativeSource.AllowedValues);
		var relativeEdit = await client.SetBindingElementNameAsync(1, relative.Id, "Text", "source", timeout.Token);
		Assert.False(relativeEdit.Accepted);
		Assert.Contains("editable ElementName Binding", relativeEdit.Error, StringComparison.Ordinal);
		var boundRelativeSource = bound.Properties.Single(property => property.EditorKind == "BindingRelativeSource");
		Assert.Equal("", boundRelativeSource.Value);
		var relativeSourceEdited = await client.SetBindingRelativeSourceAsync(1, bound.Id, "Text", "Self", timeout.Token);
		Assert.True(relativeSourceEdited.Accepted, relativeSourceEdited.Error);
		Assert.Contains("RelativeSource={RelativeSource Self}", (await client.FlushAsync(relativeSourceEdited.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		Assert.DoesNotContain(FindByName(relativeSourceEdited.Tree!, "bound")!.Properties,
			property => property.EditorKind == "BindingElementName");
		var relativeSourceCleared = await client.SetBindingRelativeSourceAsync(relativeSourceEdited.Version, bound.Id, "Text", "", timeout.Token);
		Assert.True(relativeSourceCleared.Accepted, relativeSourceCleared.Error);
		Assert.Equal("", FindByName(relativeSourceCleared.Tree!, "bound")!.Properties
			.Single(property => property.EditorKind == "BindingRelativeSource").Value);

		var edited = await client.SetBindingPathAsync(1, bound.Id, "Text", "Customer.Name", timeout.Token);
		Assert.True(edited.Accepted, edited.Error);
		var text = FindByName(edited.Tree!, "bound")!.Properties.Single(property => property.Name == "Text");
		Assert.Equal("BindingPath", text.EditorKind);
		Assert.Equal("Customer.Name", text.Value);
		Assert.Contains("Text=\"{Binding Customer.Name}\"", (await client.FlushAsync(1, timeout.Token)).Files.Single().Text,
			StringComparison.Ordinal);
		var mode = FindByName(edited.Tree!, "bound")!.Properties.Single(property => property.EditorKind == "BindingMode");
		Assert.Equal("Text", mode.BindingPropertyName);
		Assert.False(mode.ShouldSerialize);
		Assert.Contains("TwoWay", mode.AllowedValues);
		var modeEdited = await client.SetBindingModeAsync(edited.Version, bound.Id, mode.BindingPropertyName, "TwoWay", timeout.Token);
		Assert.True(modeEdited.Accepted, modeEdited.Error);
		Assert.Contains("Mode=TwoWay", (await client.FlushAsync(modeEdited.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var trigger = FindByName(modeEdited.Tree!, "bound")!.Properties.Single(property => property.EditorKind == "BindingUpdateSourceTrigger");
		Assert.Equal("Text", trigger.BindingPropertyName);
		Assert.False(trigger.ShouldSerialize);
		Assert.Contains("PropertyChanged", trigger.AllowedValues);
		var triggerEdited = await client.SetBindingUpdateSourceTriggerAsync(modeEdited.Version, bound.Id, trigger.BindingPropertyName, "PropertyChanged", timeout.Token);
		Assert.True(triggerEdited.Accepted, triggerEdited.Error);
		Assert.Contains("UpdateSourceTrigger=PropertyChanged", (await client.FlushAsync(triggerEdited.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var elementName = FindByName(triggerEdited.Tree!, "bound")!.Properties.Single(property => property.EditorKind == "BindingElementName");
		Assert.Equal("Text", elementName.BindingPropertyName);
		Assert.False(elementName.ShouldSerialize);
		var elementNameEdited = await client.SetBindingElementNameAsync(triggerEdited.Version, bound.Id, elementName.BindingPropertyName, "source", timeout.Token);
		Assert.True(elementNameEdited.Accepted, elementNameEdited.Error);
		Assert.Contains("ElementName=source", (await client.FlushAsync(elementNameEdited.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var elementNameCleared = await client.SetBindingElementNameAsync(elementNameEdited.Version, bound.Id, elementName.BindingPropertyName, "", timeout.Token);
		Assert.True(elementNameCleared.Accepted, elementNameCleared.Error);
		Assert.DoesNotContain("ElementName", (await client.FlushAsync(elementNameCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var injectedElementName = await client.SetBindingElementNameAsync(elementNameCleared.Version, bound.Id, elementName.BindingPropertyName, "source,Path=X", timeout.Token);
		Assert.False(injectedElementName.Accepted);
		Assert.Contains("Invalid Binding ElementName", injectedElementName.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("ElementName", (await client.FlushAsync(elementNameCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var notify = FindByName(elementNameCleared.Tree!, "bound")!.Properties.Single(property => property.EditorKind == "BindingNotifyOnValidationError");
		Assert.Equal("Boolean", notify.Kind);
		Assert.Equal("False", notify.Value);
		Assert.False(notify.ShouldSerialize);
		var notifyEnabled = await client.SetBindingNotifyOnValidationErrorAsync(elementNameCleared.Version, bound.Id, notify.BindingPropertyName, true, timeout.Token);
		Assert.True(notifyEnabled.Accepted, notifyEnabled.Error);
		Assert.Contains("NotifyOnValidationError=True", (await client.FlushAsync(notifyEnabled.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var notifyCleared = await client.SetBindingNotifyOnValidationErrorAsync(notifyEnabled.Version, bound.Id, notify.BindingPropertyName, false, timeout.Token);
		Assert.True(notifyCleared.Accepted, notifyCleared.Error);
		Assert.DoesNotContain("NotifyOnValidationError", (await client.FlushAsync(notifyCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var validationOptions = FindByName(notifyCleared.Tree!, "bound")!.Properties
			.Where(property => property.EditorKind == "BindingValidationOption")
			.ToDictionary(property => property.BindingOptionName, StringComparer.Ordinal);
		Assert.Equal(new[] { "BindsDirectlyToSource", "IsAsync", "NotifyOnSourceUpdated", "NotifyOnTargetUpdated",
			"ValidatesOnDataErrors", "ValidatesOnExceptions", "ValidatesOnNotifyDataErrors" },
			validationOptions.Keys.OrderBy(name => name, StringComparer.Ordinal));
		var validationOption = validationOptions["ValidatesOnExceptions"];
		Assert.Equal("BindingValidationOption", validationOption.EditorKind);
		Assert.Equal("Boolean", validationOption.Kind);
		var validationEnabled = await client.SetBindingValidationOptionAsync(notifyCleared.Version, bound.Id, validationOption.BindingPropertyName, validationOption.BindingOptionName, true, timeout.Token);
		Assert.True(validationEnabled.Accepted, validationEnabled.Error);
		Assert.Contains("ValidatesOnExceptions=True", (await client.FlushAsync(validationEnabled.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var validationCleared = await client.SetBindingValidationOptionAsync(validationEnabled.Version, bound.Id, validationOption.BindingPropertyName, validationOption.BindingOptionName, false, timeout.Token);
		Assert.True(validationCleared.Accepted, validationCleared.Error);
		Assert.DoesNotContain("ValidatesOnExceptions", (await client.FlushAsync(validationCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var asyncOption = validationOptions["IsAsync"];
		var asyncEnabled = await client.SetBindingValidationOptionAsync(validationCleared.Version, bound.Id, asyncOption.BindingPropertyName, asyncOption.BindingOptionName, true, timeout.Token);
		Assert.True(asyncEnabled.Accepted, asyncEnabled.Error);
		Assert.Contains("IsAsync=True", (await client.FlushAsync(asyncEnabled.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var asyncCleared = await client.SetBindingValidationOptionAsync(asyncEnabled.Version, bound.Id, asyncOption.BindingPropertyName, asyncOption.BindingOptionName, false, timeout.Token);
		Assert.True(asyncCleared.Accepted, asyncCleared.Error);
		Assert.DoesNotContain("IsAsync", (await client.FlushAsync(asyncCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var delay = FindByName(asyncCleared.Tree!, "bound")!.Properties.Single(property => property.EditorKind == "BindingDelay");
		Assert.Equal("Number", delay.Kind);
		Assert.Equal("0", delay.Value);
		var delayed = await client.SetBindingDelayAsync(asyncCleared.Version, bound.Id, delay.BindingPropertyName, 250, timeout.Token);
		Assert.True(delayed.Accepted, delayed.Error);
		Assert.Contains("Delay=250", (await client.FlushAsync(delayed.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var delayCleared = await client.SetBindingDelayAsync(delayed.Version, bound.Id, delay.BindingPropertyName, 0, timeout.Token);
		Assert.True(delayCleared.Accepted, delayCleared.Error);
		Assert.DoesNotContain("Delay=", (await client.FlushAsync(delayCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var negativeDelay = await client.SetBindingDelayAsync(delayCleared.Version, bound.Id, delay.BindingPropertyName, -1, timeout.Token);
		Assert.False(negativeDelay.Accepted);
		Assert.Contains("Delay must be non-negative", negativeDelay.Error, StringComparison.Ordinal);
		var unsupportedValidation = await client.SetBindingValidationOptionAsync(delayCleared.Version, bound.Id, "Text", "Mode", true, timeout.Token);
		Assert.False(unsupportedValidation.Accepted);
		Assert.Contains("Unsupported Binding validation option", unsupportedValidation.Error, StringComparison.Ordinal);
		var genericNotify = await client.SetBindingValidationOptionAsync(delayCleared.Version, bound.Id, "Text", "NotifyOnValidationError", true, timeout.Token);
		Assert.False(genericNotify.Accepted);
		Assert.Contains("Unsupported Binding validation option", genericNotify.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("NotifyOnValidationError", (await client.FlushAsync(delayCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var badTrigger = await client.SetBindingUpdateSourceTriggerAsync(delayCleared.Version, bound.Id, "Text", "NotATrigger", timeout.Token);
		Assert.False(badTrigger.Accepted);
		Assert.DoesNotContain("NotATrigger", (await client.FlushAsync(delayCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var numericTrigger = await client.SetBindingUpdateSourceTriggerAsync(delayCleared.Version, bound.Id, "Text", "99", timeout.Token);
		Assert.False(numericTrigger.Accepted);
		Assert.DoesNotContain("99", (await client.FlushAsync(delayCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var combinedTrigger = await client.SetBindingUpdateSourceTriggerAsync(delayCleared.Version, bound.Id, "Text", "PropertyChanged,LostFocus", timeout.Token);
		Assert.False(combinedTrigger.Accepted);
		Assert.DoesNotContain("UpdateSourceTrigger=Explicit", (await client.FlushAsync(delayCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var badMode = await client.SetBindingModeAsync(delayCleared.Version, bound.Id, "Text", "NotAMode", timeout.Token);
		Assert.False(badMode.Accepted);
		Assert.DoesNotContain("NotAMode", (await client.FlushAsync(delayCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var numericMode = await client.SetBindingModeAsync(delayCleared.Version, bound.Id, "Text", "99", timeout.Token);
		Assert.False(numericMode.Accepted);
		Assert.DoesNotContain("99", (await client.FlushAsync(delayCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var combinedMode = await client.SetBindingModeAsync(delayCleared.Version, bound.Id, "Text", "OneWay,OneTime", timeout.Token);
		Assert.False(combinedMode.Accepted);
		Assert.DoesNotContain("Mode=OneWayToSource", (await client.FlushAsync(delayCleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);

		var rejected = await client.SetBindingPathAsync(delayCleared.Version, bound.Id, "Width", "NotAPath", timeout.Token);
		Assert.False(rejected.Accepted);
		Assert.Contains("not an editable Binding", rejected.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("NotAPath", (await client.FlushAsync(delayCleared.Version, timeout.Token)).Files.Single().Text,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_EditsOnlyTheLevelOfAnExistingFindAncestorBinding()
	{
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Tag="outer">
			  <Grid Tag="inner">
			    <TextBlock x:Name="bound" Text="{Binding Path=Tag, RelativeSource={RelativeSource FindAncestor, AncestorType={x:Type Grid}, AncestorLevel=2}}" />
			  </Grid>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var bound = FindByName(opened.Tree!, "bound")!;
		var level = bound.Properties.Single(property => property.EditorKind == "BindingAncestorLevel");
		Assert.Equal("2", level.Value);
		Assert.Equal("Text", level.BindingPropertyName);
		Assert.DoesNotContain(bound.Properties, property => property.EditorKind == "BindingRelativeSource");

		var changed = await client.SetBindingAncestorLevelAsync(opened.Version, bound.Id, "Text", 1, timeout.Token);
		Assert.True(changed.Accepted, changed.Error);
		var text = (await client.FlushAsync(changed.Version, timeout.Token)).Files.Single().Text;
		Assert.Contains("AncestorLevel=1", text, StringComparison.Ordinal);
		// XamlDom may expand the compact RelativeSource markup into a property element, so preserve
		// the binding semantics rather than requiring the original token layout.
		Assert.Contains("AncestorType", text, StringComparison.Ordinal);
		Assert.Contains("FindAncestor", text, StringComparison.Ordinal);

		var invalid = await client.SetBindingAncestorLevelAsync(changed.Version, bound.Id, "Text", 0, timeout.Token);
		Assert.False(invalid.Accepted);
		Assert.Contains("between 1 and 1000", invalid.Error, StringComparison.Ordinal);
		var nonAncestor = FindByName(changed.Tree!, "bound")!;
		var rejected = await client.SetBindingAncestorLevelAsync(changed.Version, nonAncestor.Id, "Width", 1, timeout.Token);
		Assert.False(rejected.Accepted);
	}

	[Fact]
	public async Task ChildHost_OnlyEditsBindingSourceWhenXamlDomProvesStaticResourceSyntax()
	{
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
			      xmlns:sys="clr-namespace:System;assembly=mscorlib" Width="240" Height="80">
			  <Grid.Resources>
			    <sys:String x:Key="first">first value</sys:String>
			    <sys:String x:Key="second">second value</sys:String>
			  </Grid.Resources>
			  <TextBlock x:Name="static" Text="{Binding Path=Length, Source={StaticResource first}}" />
			  <TextBlock x:Name="element" Text="{Binding Path=Text, ElementName=static}" />
			  <TextBlock x:Name="inline">
			    <TextBlock.Text><Binding Path="Length"><Binding.Source><sys:String>inline value</sys:String></Binding.Source></Binding></TextBlock.Text>
			  </TextBlock>
			  <TextBlock x:Name="xstatic" Text="{Binding Path=Length, Source={x:Static sys:String.Empty}}" />
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var staticItem = FindByName(opened.Tree!, "static")!;
		var source = staticItem.Properties.Single(property => property.EditorKind == "BindingSource");
		Assert.Equal("first", source.Value);
		Assert.Equal(new[] { "", "first", "second" }, source.AllowedValues);
		Assert.DoesNotContain(FindByName(opened.Tree!, "inline")!.Properties, property => property.EditorKind == "BindingSource");
		Assert.DoesNotContain(FindByName(opened.Tree!, "xstatic")!.Properties, property => property.EditorKind == "BindingSource");
		foreach (var protectedName in new[] { "inline", "xstatic" })
		{
			var protectedItem = FindByName(opened.Tree!, protectedName)!;
			Assert.DoesNotContain(protectedItem.Properties, property => property.EditorKind is "BindingElementName" or "BindingRelativeSource");
			var elementNameRejected = await client.SetBindingElementNameAsync(1, protectedItem.Id, "Text", "static", timeout.Token);
			Assert.False(elementNameRejected.Accepted);
			var relativeSourceRejected = await client.SetBindingRelativeSourceAsync(1, protectedItem.Id, "Text", "Self", timeout.Token);
			Assert.False(relativeSourceRejected.Accepted);
		}

		var changed = await client.SetBindingSourceAsync(1, staticItem.Id, "Text", "second", timeout.Token);
		Assert.True(changed.Accepted, changed.Error);
		var changedText = (await client.FlushAsync(changed.Version, timeout.Token)).Files.Single().Text;
		Assert.Contains("Source={StaticResource second}", changedText, StringComparison.Ordinal);
		var cleared = await client.SetBindingSourceAsync(changed.Version, staticItem.Id, "Text", "", timeout.Token);
		Assert.True(cleared.Accepted, cleared.Error);
		Assert.DoesNotContain("Source={StaticResource", (await client.FlushAsync(cleared.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);

		var element = FindByName(cleared.Tree!, "element")!;
		Assert.Contains(element.Properties, property => property.EditorKind == "BindingSource");
		var elementChanged = await client.SetBindingSourceAsync(cleared.Version, element.Id, "Text", "first", timeout.Token);
		Assert.True(elementChanged.Accepted, elementChanged.Error);
		var elementText = (await client.FlushAsync(elementChanged.Version, timeout.Token)).Files.Single().Text;
		Assert.Contains("Source={StaticResource first}", elementText, StringComparison.Ordinal);
		Assert.DoesNotContain("ElementName=static", elementText, StringComparison.Ordinal);

		var rejected = await client.SetBindingSourceAsync(elementChanged.Version, FindByName(elementChanged.Tree!, "inline")!.Id, "Text", "first", timeout.Token);
		Assert.False(rejected.Accepted);
		Assert.Contains("StaticResource Binding Source", rejected.Error, StringComparison.Ordinal);
		Assert.Contains("inline value", (await client.FlushAsync(elementChanged.Version, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var protectedText = (await client.FlushAsync(elementChanged.Version, timeout.Token)).Files.Single().Text;
		Assert.Contains("Source={x:Static sys:String.Empty}", protectedText, StringComparison.Ordinal);
	}

	/// <summary>
	/// Tree_ nodes must report whether they are actually ON SCREEN, because the AddIn draws
	/// overlays (tab-order badges today) positioned from each node's X/Y. A hidden element still
	/// reports the coordinates it WOULD occupy, and every tab of a TabControl occupies the SAME
	/// rect - so without this an overlay for a hidden tab's child lands exactly on top of whichever
	/// tab IS showing. The WinForms surface shipped precisely that bug in its always-on outlines
	/// and name tags, where it read as "the designer renders the wrong tab" and cost hours to
	/// diagnose before anyone checked which LAYER the wrong pixels came from.
	///
	/// Both cases here are the ones that actually occur: a collapsed container's child, and a
	/// non-selected TabItem's child (whose own Visibility is never touched - WPF's
	/// UIElement.IsVisible folds the ancestor chain in, which is why no TabControl-specific logic
	/// is needed in the host).
	/// </summary>
	[Fact]
	public async Task Tree_ReportsWhetherEachElementIsActuallyOnScreen()
	{
		const string HiddenAndTabbedXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="300">
			  <StackPanel x:Name="shownPanel">
			    <TextBlock x:Name="shownChild" Text="visible" Width="100" Height="20"/>
			  </StackPanel>
			  <StackPanel x:Name="collapsedPanel" Visibility="Collapsed">
			    <TextBlock x:Name="hiddenChild" Text="hidden" Width="100" Height="20"/>
			  </StackPanel>
			  <TabControl x:Name="tabs" Width="200" Height="100">
			    <TabItem x:Name="firstTab" Header="One">
			      <TextBlock x:Name="onActiveTab" Text="active" Width="80" Height="20"/>
			    </TabItem>
			    <TabItem x:Name="secondTab" Header="Two">
			      <TextBlock x:Name="onInactiveTab" Text="inactive" Width="80" Height="20"/>
			    </TabItem>
			  </TabControl>
			</Grid>
			""";

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, HiddenAndTabbedXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		Assert.True(FindByName(opened.Tree!, "shownPanel")!.IsVisible);
		Assert.True(FindByName(opened.Tree!, "shownChild")!.IsVisible);

		// A collapsed container, and its child - whose own Visibility is Visible.
		Assert.False(FindByName(opened.Tree!, "collapsedPanel")!.IsVisible);
		Assert.False(FindByName(opened.Tree!, "hiddenChild")!.IsVisible);

		// The non-selected tab's content: the case that produced phantom overlays, since its
		// reported bounds coincide with the selected tab's content.
		if (FindByName(opened.Tree!, "onInactiveTab") is { } inactive)
			Assert.False(inactive.IsVisible);
		if (FindByName(opened.Tree!, "onActiveTab") is { } active)
			Assert.True(active.IsVisible);
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
	public async Task DesignSetBoundsBatch_MovesAllTargetsOrRejectsWithoutAPartialEdit()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var greeting = FindByName(opened.Tree!, "greeting")!;
		var go = FindByName(opened.Tree!, "go")!;

		var moved = await client.SetBoundsBatchAsync(1, new[] {
			new DesignerBoundsEdit { ElementId = greeting.Id, X = 11, Y = 12, Width = 201, Height = 31 },
			new DesignerBoundsEdit { ElementId = go.Id, X = 22, Y = 23, Width = 82, Height = 25 }
		}, timeout.Token);
		Assert.True(moved.Accepted, moved.Error);
		Assert.Equal(11, FindByName(moved.Tree!, "greeting")!.X, 1);
		Assert.Equal(22, FindByName(moved.Tree!, "go")!.X, 1);

		var rejected = await client.SetBoundsBatchAsync(1, new[] {
			new DesignerBoundsEdit { ElementId = greeting.Id, X = 99, Y = 99, Width = 1, Height = 1 },
			new DesignerBoundsEdit { ElementId = "missing", X = 0, Y = 0, Width = 1, Height = 1 }
		}, timeout.Token);
		Assert.False(rejected.Accepted);
		Assert.Contains("not found", rejected.Error, StringComparison.OrdinalIgnoreCase);
		var afterRejected = await client.FlushAsync(1, timeout.Token);
		Assert.DoesNotContain("Width=\"1\"", afterRejected.Files.Single().Text, StringComparison.Ordinal);
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
	public async Task DesignDeleteElements_RemovesAMenuItem_LeavingItsSiblingsIntact()
	{
		const string menuXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Menu x:Name="mainMenu">
			    <MenuItem x:Name="fileItem" Header="File" />
			    <MenuItem x:Name="editItem" Header="Edit" />
			  </Menu>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, menuXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var editItem = FindByName(opened.Tree!, "editItem")!;

		var deleted = await client.DeleteElementsAsync(1, new[] { editItem.Id }, timeout.Token);
		Assert.True(deleted.Accepted, deleted.Error);
		var flushed = await client.FlushAsync(1, timeout.Token);
		var text = flushed.Files.Single().Text;
		Assert.DoesNotContain("x:Name=\"editItem\"", text, StringComparison.Ordinal);
		Assert.Contains("x:Name=\"fileItem\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AddStripItem_AppendsAStatusBarItem_WithTheGivenText()
	{
		const string statusBarXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <StatusBar x:Name="statusBar"><StatusBarItem x:Name="readyItem" Content="Ready" /></StatusBar>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, statusBarXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var statusBar = FindByName(opened.Tree!, "statusBar")!;

		var added = await client.AddStripItemAsync(1, statusBar.Id, "Line 1, Col 1", timeout.Token);
		Assert.True(added.Accepted, added.Error);
		Assert.NotNull(added.CreatedElementId);
		var flushed = await client.FlushAsync(1, timeout.Token);
		var text = flushed.Files.Single().Text;
		Assert.Contains("Content=\"Line 1, Col 1\"", text, StringComparison.Ordinal);
		Assert.Contains("Content=\"Ready\"", text, StringComparison.Ordinal);

		var reopened = await client.UpdateAsync(Snapshot(2, text), timeout.Token);
		Assert.Equal(2, FindByName(reopened.Tree!, "statusBar")!.Children.Count(c => c.Type == "StatusBarItem"));
	}

	[Fact]
	public async Task AddStripItem_AppendsAButton_UnderAnExistingToolBar()
	{
		const string toolBarXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <ToolBar x:Name="mainToolBar" />
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, toolBarXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var toolBar = FindByName(opened.Tree!, "mainToolBar")!;
		Assert.Empty(toolBar.Children);

		var added = await client.AddStripItemAsync(1, toolBar.Id, "Save", timeout.Token);
		Assert.True(added.Accepted, added.Error);
		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("Content=\"Save\"", flushed.Files.Single().Text, StringComparison.Ordinal);

		var reopened = await client.UpdateAsync(Snapshot(2, flushed.Files.Single().Text), timeout.Token);
		var toolBarAfter = FindByName(reopened.Tree!, "mainToolBar")!;
		Assert.Single(toolBarAfter.Children);
		Assert.Equal("Button", toolBarAfter.Children[0].Type);
	}

	[Fact]
	public async Task AddStripItem_WithDashText_CreatesASeparator_UnderAToolBar()
	{
		const string toolBarXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <ToolBar x:Name="mainToolBar"><Button x:Name="saveButton" Content="Save" /></ToolBar>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, toolBarXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var toolBar = FindByName(opened.Tree!, "mainToolBar")!;

		var added = await client.AddStripItemAsync(1, toolBar.Id, "-", timeout.Token);
		Assert.True(added.Accepted, added.Error);
		var toolBarAfter = FindByName(added.Tree!, "mainToolBar")!;
		Assert.Equal(2, toolBarAfter.Children.Count);
		Assert.Equal("Separator", toolBarAfter.Children[1].Type);
	}

	[Fact]
	public async Task AddStripItem_WithEmptyText_IsRejected()
	{
		const string toolBarXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <ToolBar x:Name="mainToolBar" />
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, toolBarXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var toolBar = FindByName(opened.Tree!, "mainToolBar")!;

		Assert.False((await client.AddStripItemAsync(1, toolBar.Id, "   ", timeout.Token)).Accepted);
	}

	[Fact]
	public async Task AddStripItem_UnderANonStripParent_IsRejected()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		Assert.False((await client.AddStripItemAsync(1, opened.Tree!.Id, "New Item", timeout.Token)).Accepted);
	}

	[Fact]
	public async Task MoveElement_ReordersAMenuItem_AmongItsSiblings()
	{
		const string menuXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Menu x:Name="mainMenu">
			    <MenuItem x:Name="fileItem" Header="File" />
			    <MenuItem x:Name="editItem" Header="Edit" />
			    <MenuItem x:Name="viewItem" Header="View" />
			  </Menu>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, menuXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var mainMenu = FindByName(opened.Tree!, "mainMenu")!;
		Assert.Equal(new[] { "fileItem", "editItem", "viewItem" }, mainMenu.Children.Select(c => c.Name));
		var editItem = FindByName(opened.Tree!, "editItem")!;

		// Move "editItem" one step earlier: File, Edit, View -> Edit, File, View.
		var moved = await client.MoveElementAsync(1, editItem.Id, -1, timeout.Token);
		Assert.True(moved.Accepted, moved.Error);
		var mainMenuAfterFirstMove = FindByName(moved.Tree!, "mainMenu")!;
		Assert.Equal(new[] { "editItem", "fileItem", "viewItem" }, mainMenuAfterFirstMove.Children.Select(c => c.Name));

		// Move it two steps later: Edit, File, View -> File, View, Edit.
		var editItemAfter = FindByName(moved.Tree!, "editItem")!;
		var movedAgain = await client.MoveElementAsync(moved.Version, editItemAfter.Id, 2, timeout.Token);
		Assert.True(movedAgain.Accepted, movedAgain.Error);
		var flushed = await client.FlushAsync(movedAgain.Version, timeout.Token);
		var editIndex = flushed.Files.Single().Text.IndexOf("Header=\"Edit\"", StringComparison.Ordinal);
		var viewIndex = flushed.Files.Single().Text.IndexOf("Header=\"View\"", StringComparison.Ordinal);
		Assert.True(viewIndex < editIndex, "Edit should now come after View in the saved XAML");
	}

	[Fact]
	public async Task MoveElement_PastTheStartOrEndOfItsSiblings_IsRejected()
	{
		const string menuXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="240" Height="80">
			  <Menu x:Name="mainMenu">
			    <MenuItem x:Name="fileItem" Header="File" />
			    <MenuItem x:Name="editItem" Header="Edit" />
			  </Menu>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, menuXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var fileItem = FindByName(opened.Tree!, "fileItem")!;
		var editItem = FindByName(opened.Tree!, "editItem")!;

		Assert.False((await client.MoveElementAsync(1, fileItem.Id, -1, timeout.Token)).Accepted);
		Assert.False((await client.MoveElementAsync(1, editItem.Id, 1, timeout.Token)).Accepted);
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
		var added = await client.AddElementAsync(1, gridPath, toolboxItem, "added", 10, 10, cancellationToken: timeout.Token);
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
	public async Task DesignAddElement_Menu_KeepsARenderableCanvas()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.NotNull(opened.Render);

		var menu = new DesignerToolboxItemInfo {
			TypeName = "Menu",
			XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
		};
		var added = await client.AddElementAsync(1, opened.Tree!.Id, menu, "menu", 0, 0, cancellationToken: timeout.Token);
		Assert.True(added.Accepted, added.Error);
		Assert.NotNull(FindByName(added.Tree!, "menu"));
		Assert.NotNull(added.Render);
		Assert.False(string.IsNullOrEmpty(added.Render!.Data));
		Assert.True(added.Render.Width > 0);
		Assert.True(added.Render.Height > 0);
	}

	[Fact]
	public async Task DesignAddElement_Menu_InAWindowContentGrid_KeepsARenderableCanvas()
	{
		const string windowXaml = """
			<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="800" Height="450">
			  <Grid x:Name="content"><Button x:Name="button" Content="Button" Width="239" Height="51" /></Grid>
			</Window>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, windowXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var grid = FindByName(opened.Tree!, "content")!;
		var menu = new DesignerToolboxItemInfo {
			TypeName = "Menu",
			XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
		};
		var added = await client.AddElementAsync(1, grid.Id, menu, "menu", 0, 0, cancellationToken: timeout.Token);
		Assert.True(added.Accepted, added.Error);
		Assert.NotNull(added.Render);
		Assert.False(string.IsNullOrEmpty(added.Render!.Data));
		Assert.True(added.Render.Width > 0);
		Assert.True(added.Render.Height > 0);
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
		var result = await client.AddElementAsync(1, "9,9,9", toolboxItem, "shouldNotApply", 0, 0, cancellationToken: timeout.Token);
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
	public async Task SharedClients_RecoverEveryOpenDocumentAfterTheChildExits()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var first = await WpfSurfaceHostClient.AcquireSharedAsync(HostDll(), timeout.Token);
		using var second = await WpfSurfaceHostClient.AcquireSharedAsync(HostDll(), timeout.Token);
		Assert.True((await first.OpenAsync(Snapshot(1, Xaml), timeout.Token)).Accepted);
		Assert.True((await second.OpenAsync(Snapshot(1, Xaml.Replace("Hello", "Sibling")), timeout.Token)).Accepted);
		var failedProcessId = first.ProcessId;

		// This is the shared-pool failure mode: one killed child must be replaced once and
		// each document must reopen from its own parent-owned recovery snapshot.
		first.TerminateHost();
		await WaitUntilAsync(() => first.RecoveryCount > 0 && second.RecoveryCount > 0
			&& first.IsAlive && second.IsAlive && first.ProcessId != failedProcessId, timeout.Token);

		Assert.Equal(first.ProcessId, second.ProcessId);
		Assert.NotEqual(failedProcessId, first.ProcessId);
		Assert.Contains("Hello", (await first.FlushAsync(1, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		var sibling = (await second.FlushAsync(1, timeout.Token)).Files.Single().Text;
		Assert.Contains("Sibling", sibling, StringComparison.Ordinal);
		Assert.DoesNotContain("Hello", sibling, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SharedClient_RecoveryRestoresTheLastAcceptedDesignTheme()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.AcquireSharedAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, ThemedXaml, WpfThemeFixtureDll()), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var themed = await client.SetThemeAsync(1, "Midnight", timeout.Token);
		Assert.True(themed.Accepted, themed.Error);
		var expectedFrame = themed.Render!.Data;
		var recovered = new TaskCompletionSource<DesignerSessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
		client.Recovered += (_, state) => recovered.TrySetResult(state);

		client.TerminateHost();
		var afterRecovery = await recovered.Task.WaitAsync(timeout.Token);

		Assert.True(afterRecovery.Accepted, afterRecovery.Error);
		Assert.Equal(expectedFrame, afterRecovery.Render!.Data);
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

		var pixels = DesignerFrameCodec.DecodeDeflateBase64(frame.Data);
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

	/// <summary>The fixture assembly whose types the CHILD must resolve. Overridable alongside
	/// HostDll: the Microsoft WPF run needs the fixture compiled against Microsoft WindowsDesktop,
	/// or the child cannot load it. Assembly name and namespace stay "CustomControlFixture" in
	/// both builds, so the XAML in these tests is unchanged.</summary>
	static string CustomControlFixtureDll() =>
		Environment.GetEnvironmentVariable("OPENDEVELOP_WPF_CUSTOMCONTROLFIXTURE_DLL") is { Length: > 0 } overridden
			? Path.GetFullPath(overridden)
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
			#if MICROSOFT_WPF_DESIGNER_HOST
				"../../../../SurfaceHost.Tests/Fixtures/CustomControlFixture/bin/Debug/net10.0-windows/CustomControlFixture.dll"));
			#else
				"../../../../WpfDesign.SurfaceHost.Tests/Fixtures/CustomControlFixture/bin/Debug/net10.0-windows/CustomControlFixture.dll"));
			#endif

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
	public async Task PositionalMarkupExtensionArgument_KeepsEscapedLeadingBraceLiteral()
	{
		// EchoExtension has no default constructor, so its argument reaches the converter directly
		// instead of through a mapped property: the literal-brace escape must still be removed.
		var fixtureDll = CustomControlFixtureDll();
		Assert.True(File.Exists(fixtureDll), "CustomControlFixture must be built before this test runs: " + fixtureDll);
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:c="clr-namespace:CustomControlFixture;assembly=CustomControlFixture" Width="400" Height="300">
			  <StackPanel HorizontalAlignment="Left">
			    <TextBlock x:Name="reference" HorizontalAlignment="Left" Text="{}{0:N2}"/>
			    <TextBlock x:Name="leaked" HorizontalAlignment="Left" Text="{}{}{0:N2}"/>
			    <TextBlock x:Name="unquoted" HorizontalAlignment="Left" Text="{c:Echo \{0:N2\}}"/>
			    <TextBlock x:Name="quoted" HorizontalAlignment="Left" Text="{c:Echo '{}{0:N2}'}"/>
			  </StackPanel>
			</Grid>
			""";
		var snapshot = Snapshot(1, xaml);
		snapshot.ProjectAssemblyPath = fixtureDll;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		// The node carries the extension, not its evaluated text, so compare measured widths with
		// a literal "{0:N2}" and with the leaked "{}{0:N2}" form.
		double Width(string name) => FindByName(opened.Tree!, name)!.Width;
		Assert.NotEqual(Width("reference"), Width("leaked"));
		Assert.Equal(Width("reference"), Width("unquoted"));
		Assert.Equal(Width("reference"), Width("quoted"));
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
	public async Task AppXamlStaticResource_ResolvesWithoutInjectingIntoTheSavedXaml()
	{
		// The child used to splice App.xaml's resources into the page text (and rewrite its pack
		// URIs) before parsing so a page-level {StaticResource} could see them. The parsed text is
		// the document model, so that splice was written back into the user's file on save. The
		// key must still resolve (Width 250 proves it), and the flush must add nothing but the edit.
		const string appXaml = """
			<Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			  <Application.Resources>
			    <Style x:Key="WideText" TargetType="TextBlock">
			      <Setter Property="Width" Value="250"/>
			    </Style>
			  </Application.Resources>
			</Application>
			""";
		const string pageXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="300">
			  <TextBlock x:Name="styled" Text="Hi" Style="{StaticResource WideText}"/>
			  <Image x:Name="logo" Source="pack://application:,,,/Assets/logo.png"/>
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
		var styled = FindByName(opened.Tree!, "styled");
		Assert.True(styled != null, "the TextBlock did not appear in the element tree");
		Assert.Equal(250d, styled!.Width);

		var edited = await client.SetPropertyAsync(1, styled.Id, "Text", "Edited", timeout.Token);
		Assert.True(edited.Accepted, edited.Error);
		var text = (await client.FlushAsync(1, timeout.Token)).Files.Single().Text;
		Assert.Contains("Edited", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Grid.Resources", text, StringComparison.Ordinal);
		Assert.DoesNotContain("MergedDictionaries", text, StringComparison.Ordinal);
		Assert.DoesNotContain(";component/", text, StringComparison.Ordinal);
		Assert.Contains("Source=\"pack://application:,,,/Assets/logo.png\"", text, StringComparison.Ordinal);
	}

	// Hand-formatted on purpose: multi-line start tags, a comment, single quotes, an entity and
	// no trailing newline are exactly what a regenerating save used to destroy.
	const string FormattedXaml =
		"<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
		"      xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
		"      Width=\"400\" Height=\"300\">\n" +
		"    <!-- keep me -->\n" +
		"    <TextBlock x:Name=\"greeting\"\n" +
		"               Text='Hello &amp; welcome'\n" +
		"               Width=\"200\" />\n" +
		"    <Button x:Name=\"go\" Content=\"Go\" Width=\"80\"/>\n" +
		"</Grid>";

	[Fact]
	public async Task Flush_ChangesOnlyTheEditedAttribute()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, FormattedXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var go = FindByName(opened.Tree!, "go")!;

		Assert.True((await client.SetPropertyAsync(1, go.Id, "Content", "Run", timeout.Token)).Accepted);
		var text = (await client.FlushAsync(1, timeout.Token)).Files.Single().Text;
		Assert.Equal(FormattedXaml.Replace("Content=\"Go\"", "Content=\"Run\""), text);
	}

	[Fact]
	public async Task Flush_DeleteAndAdd_TouchOnlyTheirOwnLines()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, FormattedXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var go = FindByName(opened.Tree!, "go")!;

		Assert.True((await client.DeleteElementsAsync(1, new[] { go.Id }, timeout.Token)).Accepted);
		var deleted = (await client.FlushAsync(1, timeout.Token)).Files.Single().Text;
		Assert.Equal(FormattedXaml.Replace("\n    <Button x:Name=\"go\" Content=\"Go\" Width=\"80\"/>", ""), deleted);

		var toolboxItem = new DesignerToolboxItemInfo {
			TypeName = "CheckBox",
			XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
		};
		var added = await client.AddElementAsync(1, opened.Tree!.Id, toolboxItem, "added", 10, 10, cancellationToken: timeout.Token);
		Assert.True(added.Accepted, added.Error);
		var text = (await client.FlushAsync(1, timeout.Token)).Files.Single().Text;
		var lines = text.Split('\n');
		var originalLines = deleted.Split('\n');
		// Every original line survives verbatim and exactly one line - the CheckBox - is new,
		// indented like its siblings.
		Assert.Equal(originalLines.Length + 1, lines.Length);
		var newLine = Assert.Single(lines.Except(originalLines));
		Assert.StartsWith("    <CheckBox ", newLine, StringComparison.Ordinal);
		Assert.Contains("x:Name=\"added\"", newLine, StringComparison.Ordinal);
		Assert.DoesNotContain("xmlns", newLine, StringComparison.Ordinal);
		Assert.EndsWith("</Grid>", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Flush_Reorder_MovesOnlyTheMovedElement()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, FormattedXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var go = FindByName(opened.Tree!, "go")!;

		var moved = await client.MoveElementAsync(1, go.Id, -1, timeout.Token);
		Assert.True(moved.Accepted, moved.Error);
		var text = (await client.FlushAsync(moved.Version, timeout.Token)).Files.Single().Text;
		// The comment and the multi-line TextBlock are untouched; the Button now precedes the
		// TextBlock, rewritten on its own line at the siblings' indent.
		var textBlock = "    <TextBlock x:Name=\"greeting\"\n               Text='Hello &amp; welcome'\n               Width=\"200\" />";
		Assert.Contains("<!-- keep me -->", text, StringComparison.Ordinal);
		Assert.Contains(textBlock, text, StringComparison.Ordinal);
		var buttonAt = text.IndexOf("<Button ", StringComparison.Ordinal);
		Assert.True(buttonAt >= 0 && buttonAt < text.IndexOf("<TextBlock", StringComparison.Ordinal), text);
		Assert.Equal(FormattedXaml.Split('\n').Length, text.Split('\n').Length);
		Assert.EndsWith("</Grid>", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Flush_TextContentEdit_ChangesOnlyTheText()
	{
		const string xaml =
			"<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
			"            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
			"    <Button x:Name=\"go\"\n" +
			"            Width=\"80\">Go</Button>\n" +
			"    <TextBlock x:Name=\"other\" Text=\"Other\" />\n" +
			"</StackPanel>\n";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var go = FindByName(opened.Tree!, "go")!;

		Assert.True((await client.SetPropertyAsync(1, go.Id, "Content", "Run", timeout.Token)).Accepted);
		var text = (await client.FlushAsync(1, timeout.Token)).Files.Single().Text;
		// Whether the designer keeps the value as element text or moves it to an attribute, nothing
		// outside the Button may change.
		Assert.Contains("Run", text, StringComparison.Ordinal);
		Assert.DoesNotContain(">Go<", text, StringComparison.Ordinal);
		Assert.StartsWith(xaml.Substring(0, xaml.IndexOf("    <Button", StringComparison.Ordinal)), text, StringComparison.Ordinal);
		Assert.EndsWith("\n    <TextBlock x:Name=\"other\" Text=\"Other\" />\n</StackPanel>\n", text, StringComparison.Ordinal);
		Assert.Contains("<Button x:Name=\"go\"\n            Width=\"80\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ApplicationPackUri_InAFontFamily_ResolvesAgainstTheProjectWithoutChangingTheText()
	{
		// FontFamily is neither Uri nor ImageSource, the two types the parser used to map. An
		// assembly-less pack URI must still name the designed project (not this host) on the
		// instance, while the saved text keeps exactly what the user wrote.
		var fixtureDll = CustomControlFixtureDll();
		Assert.True(File.Exists(fixtureDll), "CustomControlFixture must be built before this test runs: " + fixtureDll);
		const string xaml =
			"<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Width=\"400\" Height=\"300\">\n" +
			"  <TextBlock x:Name=\"glyph\" Text=\"A\" FontFamily=\"pack://application:,,,/Fonts/#Symbols\" />\n" +
			"</Grid>";
		var snapshot = Snapshot(1, xaml);
		snapshot.ProjectAssemblyPath = fixtureDll;

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var glyph = FindByName(opened.Tree!, "glyph")!;
		var fontFamily = glyph.Properties.Single(p => p.Name == "FontFamily");
		Assert.Equal("pack://application:,,,/CustomControlFixture;component/Fonts/#Symbols", fontFamily.Value);

		Assert.True((await client.SetPropertyAsync(1, glyph.Id, "Text", "B", timeout.Token)).Accepted);
		var text = (await client.FlushAsync(1, timeout.Token)).Files.Single().Text;
		Assert.Equal(xaml.Replace("Text=\"A\"", "Text=\"B\""), text);
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
		var usingFallback = opened.Diagnostics.Any(d => d.Message.Contains("software fallback frame", StringComparison.Ordinal));
		if (usingFallback)
		{
			// The portable fallback intentionally projects only layout backgrounds, not WPF text
			// or control templates. Its DDP contract is a timely, decodable frame plus a visible
			// diagnostic; exact paint-pixel correspondence remains a requirement of ProGPU mode.
			Assert.NotEmpty(pixels);
			Assert.Contains(opened.Diagnostics, d => d.Severity == "Warning");
			return;
		}

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

	static byte[] Inflate(string data) => DesignerFrameCodec.DecodeDeflateBase64(data);

	static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
	{
		using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		wait.CancelAfter(TimeSpan.FromSeconds(30));
		while (!condition())
			await Task.Delay(25, wait.Token);
	}

	static string WpfThemeFixtureDll() => typeof(FixtureMarker).Assembly.Location;

	[Fact]
	public async Task EditACopy_CopiesTheProjectThemesImplicitStyle()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(HostDll(), timeout.Token);
		var opened = await client.OpenAsync(Snapshot(1, ThemedXaml, WpfThemeFixtureDll()), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		static string? Find(DesignerElementNode node) => node.Name == "greeting" ? node.Id : node.Children.Select(Find).FirstOrDefault(id => id != null);
		var greeting = Find(opened.Tree!)!;

		// Without a selected project theme there is nothing to copy (never the runtime's theme).
		Assert.False((await client.CopyThemeStyleAsync(1, greeting, timeout.Token)).Accepted);
		Assert.True((await client.SetThemeAsync(1, "Midnight", timeout.Token)).Accepted);
		var copied = await client.CopyThemeStyleAsync(1, greeting, timeout.Token);
		Assert.True(copied.Accepted, copied.Error);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.True(System.Text.RegularExpressions.Regex.IsMatch(text, "<Style(?=[^>]*x:Key=\"TextBlockStyle1\")(?=[^>]*TargetType=\"TextBlock\")"), text);
		Assert.Contains("<Setter Property=\"FontSize\" Value=\"13\"", text, StringComparison.Ordinal);
		Assert.Contains("Style=\"{StaticResource TextBlockStyle1}\"", text, StringComparison.Ordinal);
		Assert.True((await client.OpenAsync(Snapshot(2, text), timeout.Token)).Accepted, text);
	}

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
