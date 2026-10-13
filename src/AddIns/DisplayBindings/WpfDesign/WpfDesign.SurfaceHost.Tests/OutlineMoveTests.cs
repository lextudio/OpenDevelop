using ICSharpCode.WpfDesign.SurfaceHost;
using Xunit;

namespace WpfDesign.SurfaceHost.Tests;

/// <summary>The in-process Outline's drag and drop (OutlineNodeBase.Insert).</summary>
public sealed class OutlineMoveTests
{
	const string Xaml = """
		<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="200">
		  <Button x:Name="first" Content="First"/>
		  <Border x:Name="frame"/>
		  <StackPanel x:Name="inner">
		    <StackPanel.ContextMenu><ContextMenu x:Name="menu"/></StackPanel.ContextMenu>
		    <TextBlock x:Name="label" Text="Label"/>
		  </StackPanel>
		  <Button x:Name="last" Content="Last"/>
		</StackPanel>
		""";

	[Fact]
	public async Task ChildHost_MovesElementsIntoContainersAndRejectsImpossibleMoves()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		string Id(ICSharpCode.SharpDevelop.Designer.Remote.DesignerSessionState state, string name)
		{
			string? Find(ICSharpCode.SharpDevelop.Designer.Remote.DesignerElementNode node)
				=> node.Name == name ? node.Id : node.Children.Select(Find).FirstOrDefault(id => id != null);
			return Find(state.Tree!) ?? throw new InvalidOperationException(name);
		}

		// Into a panel after a child, then into an empty single-content Border.
		var intoPanel = await client.MoveIntoAsync(1, new[] { Id(opened, "last") }, Id(opened, "inner"), Id(opened, "label"), timeout.Token);
		Assert.True(intoPanel.Accepted, intoPanel.Error);
		Assert.Equal(Id(intoPanel, "last"), intoPanel.MovedElementId);
		var intoBorder = await client.MoveIntoAsync(1, new[] { Id(intoPanel, "first") }, Id(intoPanel, "frame"), null, timeout.Token);
		Assert.True(intoBorder.Accepted, intoBorder.Error);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Matches("x:Name=\"label\"[^<]*/>\\s*<Button x:Name=\"last\"", text);
		Assert.Matches("<Border x:Name=\"frame\">\\s*<Button x:Name=\"first\"", text);

		// Rejected without an edit: a full single-content target, into itself/a descendant, the root.
		Assert.False((await client.MoveIntoAsync(1, new[] { Id(intoBorder, "label") }, Id(intoBorder, "frame"), null, timeout.Token)).Accepted);
		Assert.False((await client.MoveIntoAsync(1, new[] { Id(intoBorder, "inner") }, Id(intoBorder, "inner"), null, timeout.Token)).Accepted);
		Assert.False((await client.MoveIntoAsync(1, new[] { "" }, Id(intoBorder, "inner"), null, timeout.Token)).Accepted);
		// The Outline shows the ContextMenu as a child of "inner", but it is no insertion anchor.
		var menuNode = intoBorder.Tree!.Children.Single(child => child.Name == "inner").Children.Single(child => child.Id.Contains('@'));
		Assert.False((await client.MoveIntoAsync(1, new[] { Id(intoBorder, "frame") }, Id(intoBorder, "inner"), menuNode.Id, timeout.Token)).Accepted);
		// A single content holding text is not empty either.
		Assert.False((await client.MoveIntoAsync(1, new[] { Id(intoBorder, "label") }, Id(intoBorder, "last"), null, timeout.Token)).Accepted);
		// An anchor in another container, and an anchor into a single-content target.
		Assert.False((await client.MoveIntoAsync(1, new[] { Id(intoBorder, "frame") }, Id(intoBorder, "root"), Id(intoBorder, "label"), timeout.Token)).Accepted);
		Assert.False((await client.MoveIntoAsync(1, new[] { Id(intoBorder, "label") }, Id(intoBorder, "frame"), Id(intoBorder, "first"), timeout.Token)).Accepted);
		Assert.Equal(text, Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text);
	}

	[Fact]
	public async Task ChildHost_MoveKeepsOnlyTheDestinationPanelsLayoutProperties()
	{
		const string xaml = """
			<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root">
			  <StackPanel x:Name="source">
			    <Button x:Name="toGrid" Grid.Row="1" Canvas.Left="11" DockPanel.Dock="Right" />
			    <Button x:Name="toCanvas" Grid.Row="2" Canvas.Left="22" Canvas.Top="33" DockPanel.Dock="Bottom" />
			    <Button x:Name="toDock" Grid.Row="3" Canvas.Left="44" DockPanel.Dock="Left" />
			    <Button x:Name="manyFirst" Grid.Row="4" Canvas.Left="55" DockPanel.Dock="Top" />
			    <Button x:Name="manySecond" Grid.Row="5" Canvas.Left="66" DockPanel.Dock="Right" />
			  </StackPanel>
			  <Grid x:Name="grid" />
			  <Canvas x:Name="canvas" />
			  <DockPanel x:Name="dock" />
			</StackPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		string Id(ICSharpCode.SharpDevelop.Designer.Remote.DesignerSessionState state, string name)
		{
			string? Find(ICSharpCode.SharpDevelop.Designer.Remote.DesignerElementNode node)
				=> node.Name == name ? node.Id : node.Children.Select(Find).FirstOrDefault(id => id != null);
			return Find(state.Tree!) ?? throw new InvalidOperationException(name);
		}

		var intoGrid = await client.MoveIntoAsync(1, new[] { Id(opened, "toGrid") }, Id(opened, "grid"), null, timeout.Token);
		Assert.True(intoGrid.Accepted, intoGrid.Error);
		var intoCanvas = await client.MoveIntoAsync(1, new[] { Id(intoGrid, "toCanvas") }, Id(intoGrid, "canvas"), null, timeout.Token);
		Assert.True(intoCanvas.Accepted, intoCanvas.Error);
		var intoDock = await client.MoveIntoAsync(1, new[] { Id(intoCanvas, "toDock") }, Id(intoCanvas, "dock"), null, timeout.Token);
		Assert.True(intoDock.Accepted, intoDock.Error);
		// A single host transaction with two elements must apply the same destination cleanup to both.
		var multi = await client.MoveIntoAsync(1, new[] { Id(intoDock, "manyFirst"), Id(intoDock, "manySecond") }, Id(intoDock, "grid"), Id(intoDock, "toGrid"), timeout.Token);
		Assert.True(multi.Accepted, multi.Error);

		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Matches("<Button(?=[^>]*x:Name=\"toGrid\")(?=[^>]*Grid.Row=\"1\")", text);
		Assert.DoesNotMatch("<Button(?=[^>]*x:Name=\"toGrid\")[^>]*(Canvas.Left|DockPanel.Dock)", text);
		Assert.Matches("<Button(?=[^>]*x:Name=\"toCanvas\")(?=[^>]*Canvas.Left=\"22\")(?=[^>]*Canvas.Top=\"33\")", text);
		Assert.DoesNotMatch("<Button(?=[^>]*x:Name=\"toCanvas\")[^>]*(Grid.Row|DockPanel.Dock)", text);
		Assert.Matches("<Button(?=[^>]*x:Name=\"toDock\")(?=[^>]*DockPanel.Dock=\"Left\")", text);
		Assert.DoesNotMatch("<Button(?=[^>]*x:Name=\"toDock\")[^>]*(Grid.Row|Canvas.Left)", text);
		Assert.Matches("<Button(?=[^>]*x:Name=\"manyFirst\")(?=[^>]*Grid.Row=\"4\")", text);
		Assert.Matches("<Button(?=[^>]*x:Name=\"manySecond\")(?=[^>]*Grid.Row=\"5\")", text);
		Assert.DoesNotMatch("<Button(?=[^>]*x:Name=\"many(?:First|Second)\")[^>]*(Canvas.Left|DockPanel.Dock)", text);
	}
}
