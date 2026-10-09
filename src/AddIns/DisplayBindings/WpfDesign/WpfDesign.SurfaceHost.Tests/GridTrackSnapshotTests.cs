using ICSharpCode.WpfDesign.SurfaceHost;
using Xunit;

namespace WpfDesign.SurfaceHost.Tests;

public sealed class GridTrackSnapshotTests
{
	[Fact]
	public async Task ChildHost_GridNodeCarriesTheSameMeasuredTracksAsTheGridGuidesRpc()
	{
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <Grid.RowDefinitions><RowDefinition Height="40"/><RowDefinition Height="*"/></Grid.RowDefinitions>
			  <Grid.ColumnDefinitions><ColumnDefinition Width="90"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
			  <TextBlock x:Name="label" Grid.Row="1" Grid.Column="1" Text="Tracks"/>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);

		Assert.True(opened.Accepted, opened.Error);
		Assert.NotNull(opened.Tree!.GridTracks);
		var tracks = opened.Tree.GridTracks!;
		var rpc = await client.QueryGridGuidesAsync(1, opened.Tree.Id, timeout.Token);
		Assert.True(rpc.Accepted, rpc.Error);
		Assert.Equal(rpc.RowTracks.Count, tracks.RowTracks.Count);
		Assert.Equal(rpc.ColumnTracks.Count, tracks.ColumnTracks.Count);
		Assert.Equal(rpc.RowTracks[0].Size, tracks.RowTracks[0].Size, 3);
		Assert.Equal(rpc.ColumnTracks[0].Size, tracks.ColumnTracks[0].Size, 3);
	}

	[Fact]
	public async Task ChildHost_SplitGridTrackInsertsADefinitionAndPreservesChildCells()
	{
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <Grid.RowDefinitions><RowDefinition Height="40"/><RowDefinition Height="*"/></Grid.RowDefinitions>
			  <TextBlock x:Name="label" Grid.Row="1" Text="Tracks"/>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var split = await client.SplitGridTrackAsync(1, opened.Tree!.Id, isRow: true, position: 20, timeout.Token);

		Assert.True(split.Accepted, split.Error);
		Assert.Equal(3, split.Tree!.GridTracks!.RowTracks.Count);
		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("Grid.Row=\"2\"", Assert.Single(flushed.Files).Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_SplitGridTrackExtendsOnlyAChildWhichVisiblyCrossesTheNewDivider()
	{
		const string xaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <Grid.RowDefinitions><RowDefinition Height="80"/><RowDefinition Height="40"/></Grid.RowDefinitions>
			  <TextBlock x:Name="spanning" Grid.Row="0" Grid.RowSpan="2" Height="100" VerticalAlignment="Top" Text="Tracks"/>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var split = await client.SplitGridTrackAsync(1, opened.Tree!.Id, isRow: true, position: 40, timeout.Token);

		Assert.True(split.Accepted, split.Error);
		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("Grid.RowSpan=\"3\"", Assert.Single(flushed.Files).Text, StringComparison.Ordinal);
	}
}
