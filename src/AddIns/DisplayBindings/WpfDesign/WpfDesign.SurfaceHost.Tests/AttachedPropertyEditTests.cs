using ICSharpCode.WpfDesign.SurfaceHost;
using Xunit;

namespace WpfDesign.SurfaceHost.Tests;

public sealed class AttachedPropertyEditTests
{
	[Fact]
	public async Task ChildHost_SetPropertyWritesAFrameworkAttachedPropertyByItsXamlName()
	{
		const string xaml = """
			<DockPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <Button x:Name="docked" Content="Docked"/>
			</DockPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var button = Assert.Single(opened.Tree!.Children);

		var docked = await client.SetPropertyAsync(1, button.Id, "DockPanel.Dock", "Top", timeout.Token);

		Assert.True(docked.Accepted, docked.Error);
		Assert.Contains("DockPanel.Dock=\"Top\"", Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text, StringComparison.Ordinal);
		// An owner outside System.Windows.Controls, or a member that is not a dependency property,
		// is not an attached property: the ordinary lookup rejects it.
		Assert.False((await client.SetPropertyAsync(1, button.Id, "Nope.Dock", "Top", timeout.Token)).Accepted);
		Assert.False((await client.SetPropertyAsync(1, button.Id, "DockPanel.Nope", "Top", timeout.Token)).Accepted);
	}

	[Fact]
	public async Task ChildHost_PublishesAndResetsTheParentPanelsAttachedLayoutProperties()
	{
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <Grid.RowDefinitions><RowDefinition/><RowDefinition/></Grid.RowDefinitions>
			  <Button x:Name="cell" Grid.Row="1" Content="Cell"/>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var button = Assert.Single(opened.Tree!.Children);
		var row = Assert.Single(button.Properties, p => p.Name == "Grid.Row");
		Assert.Equal("1", row.Value);
		Assert.True(row.ShouldSerialize);
		Assert.Equal("Layout", row.Category);
		Assert.Contains(button.Properties, p => p.Name == "Grid.ColumnSpan" && !p.ShouldSerialize);
		Assert.DoesNotContain(button.Properties, p => p.Name.StartsWith("Canvas.", StringComparison.Ordinal));
		// The root has no panel parent, so it has no attached layout rows.
		Assert.DoesNotContain(opened.Tree.Properties, p => p.Name.StartsWith("Grid.", StringComparison.Ordinal));

		var reset = await client.ResetPropertyAsync(1, button.Id, "Grid.Row", timeout.Token);

		Assert.True(reset.Accepted, reset.Error);
		Assert.DoesNotContain("Grid.Row=", Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text, StringComparison.Ordinal);
	}
}
