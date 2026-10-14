using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WpfDesign.SurfaceHost;
using Xunit;

namespace WpfDesign.SurfaceHost.Tests;

/// <summary>The in-process designer's Copy and Paste (XamlEditOperations).</summary>
public sealed class CopyPasteTests
{
	const string Xaml = """
		<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="200">
		  <Border x:Name="frame"><Button x:Name="ok" AutomationProperties.Name="ok" Content="OK"/></Border>
		  <StackPanel x:Name="inner"><TextBlock x:Name="label1" Text="Label"/></StackPanel>
		</StackPanel>
		""";

	static string Id(DesignerSessionState state, string name)
	{
		string? Find(DesignerElementNode node) => node.Name == name ? node.Id : node.Children.Select(Find).FirstOrDefault(id => id != null);
		return Find(state.Tree!) ?? throw new InvalidOperationException(name);
	}

	[Fact]
	public async Task ChildHost_CopiesAndPastesElementsWithUniqueNames()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		// Copying a container and its child yields one snippet; the root is not copyable.
		var copied = await client.CopyElementsAsync(1, new[] { Id(opened, "frame"), Id(opened, "ok") }, timeout.Token);
		Assert.True(copied.Accepted, copied.Error);
		var snippet = Assert.Single(copied.Snippets);
		Assert.Contains("x:Name=\"ok\"", snippet, StringComparison.Ordinal);
		Assert.False((await client.CopyElementsAsync(1, new[] { opened.Tree!.Id }, timeout.Token)).Accepted);

		// Pasting with the TextBlock selected goes up to its StackPanel (a TextBlock holds no
		// elements); the pasted names are made unique.
		var label = await client.CopyElementsAsync(1, new[] { Id(opened, "label1") }, timeout.Token);
		var pasted = await client.PasteElementsAsync(1, Id(opened, "label1"), copied.Snippets.Concat(label.Snippets).ToArray(), timeout.Token);
		Assert.True(pasted.Accepted, pasted.Error);
		Assert.Equal(2, pasted.CreatedElementIds.Count);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Contains("x:Name=\"frame1\"", text, StringComparison.Ordinal);
		Assert.Contains("x:Name=\"ok1\"", text, StringComparison.Ordinal);
		// An attached AutomationProperties.Name is a value, not an identity: it is not renamed.
		Assert.DoesNotContain("AutomationProperties.Name=\"ok1\"", text, StringComparison.Ordinal);
		Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "AutomationProperties.Name=\"ok\"").Count);
		Assert.Contains("x:Name=\"label2\"", text, StringComparison.Ordinal);
		Assert.Matches("<StackPanel x:Name=\"inner\">\\s*<TextBlock x:Name=\"label1\"[^>]*/>\\s*<Border x:Name=\"frame1\"", text);

		// Not XAML, or nothing that fits anywhere, is rejected without an edit.
		Assert.False((await client.PasteElementsAsync(1, Id(pasted, "inner"), new[] { "not xaml" }, timeout.Token)).Accepted);
		Assert.Equal(text, Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text);
	}

	[Fact]
	public async Task ChildHost_PastesIntoTheMeasuredGridCellUnderThePoint()
	{
		const string gridXaml = """
			<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="200" Height="100">
			  <Grid.RowDefinitions><RowDefinition Height="40"/><RowDefinition Height="*"/></Grid.RowDefinitions>
			  <Grid.ColumnDefinitions><ColumnDefinition Width="80"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
			  <Button x:Name="ok" Grid.Row="1" Grid.Column="1" Content="OK"/>
			</Grid>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, gridXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var copied = await client.CopyElementsAsync(1, new[] { Id(opened, "ok") }, timeout.Token);
		Assert.True(copied.Accepted, copied.Error);
		var gridId = opened.Tree!.Id;

		// Outside the Grid, or a target that is not a Grid, is refused without an edit.
		Assert.False((await client.PasteElementsIntoCellAsync(1, gridId, copied.Snippets.ToArray(), 250, 20, timeout.Token)).Accepted);
		Assert.False((await client.PasteElementsIntoCellAsync(1, Id(opened, "ok"), copied.Snippets.ToArray(), 10, 10, timeout.Token)).Accepted);
		var original = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.DoesNotContain("ok1", original, StringComparison.Ordinal);

		// Row 0, column 1: the copied Grid.Row is reset rather than written as 0.
		var first = await client.PasteElementsIntoCellAsync(1, gridId, copied.Snippets.ToArray(), 100, 20, timeout.Token);
		Assert.True(first.Accepted, first.Error);
		Assert.Single(first.CreatedElementIds);
		// Row 1, column 0.
		var second = await client.PasteElementsIntoCellAsync(1, gridId, copied.Snippets.ToArray(), 10, 60, timeout.Token);
		Assert.True(second.Accepted, second.Error);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		var ok1 = System.Text.RegularExpressions.Regex.Match(text, "<Button[^>]*x:Name=\"ok1\"[^>]*/>").Value;
		var ok2 = System.Text.RegularExpressions.Regex.Match(text, "<Button[^>]*x:Name=\"ok2\"[^>]*/>").Value;
		Assert.Contains("Grid.Column=\"1\"", ok1, StringComparison.Ordinal);
		Assert.DoesNotContain("Grid.Row", ok1, StringComparison.Ordinal);
		Assert.Contains("Grid.Row=\"1\"", ok2, StringComparison.Ordinal);
		Assert.DoesNotContain("Grid.Column", ok2, StringComparison.Ordinal);
		Assert.Matches("<Button(?=[^>]*x:Name=\"ok\")(?=[^>]*Grid.Row=\"1\")(?=[^>]*Grid.Column=\"1\")", text);
	}

	[Fact]
	public async Task ChildHost_CreatesAnEmptyStyleInTheRootResources()
	{
		const string styleXaml = """
			<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="200" Height="100">
			  <Button x:Name="plain" Content="Plain"/>
			  <Button x:Name="styled" Content="Styled" Style="{x:Null}"/>
			</StackPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, styleXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		// The root cannot style itself from its own resources; an existing Style is not replaced.
		Assert.False((await client.CreateEmptyStyleAsync(1, opened.Tree!.Id, timeout.Token)).Accepted);
		Assert.False((await client.CreateEmptyStyleAsync(1, Id(opened, "styled"), timeout.Token)).Accepted);

		var created = await client.CreateEmptyStyleAsync(1, Id(opened, "plain"), timeout.Token);
		Assert.True(created.Accepted, created.Error);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.True(System.Text.RegularExpressions.Regex.IsMatch(text, "<Style(?=[^>]*x:Key=\"ButtonStyle1\")(?=[^>]*TargetType=\"Button\")"), text);
		Assert.Matches("<Button(?=[^>]*x:Name=\"plain\")(?=[^>]*Style=\"\\{StaticResource ButtonStyle1\\}\")", text);

		// A second empty style gets the next free key, and the document still loads.
		var reopened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(2, text.Replace("Style=\"{x:Null}\"", "")), timeout.Token);
		Assert.True(reopened.Accepted, reopened.Error);
		var second = await client.CreateEmptyStyleAsync(2, Id(reopened, "styled"), timeout.Token);
		Assert.True(second.Accepted, second.Error);
		Assert.Contains("x:Key=\"ButtonStyle2\"", Assert.Single((await client.FlushAsync(2, timeout.Token)).Files).Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_CreatesAnEmptyControlTemplateInTheRootResources()
	{
		const string templateXaml = """
			<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="200" Height="100">
			  <Button x:Name="plain" Content="Plain"/>
			  <Border x:Name="border" Width="10" Height="10"/>
			</StackPanel>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, templateXaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		// A Border is not a Control, so it has no Template to point at a resource.
		Assert.False((await client.CreateEmptyTemplateAsync(1, Id(opened, "border"), timeout.Token)).Accepted);

		var created = await client.CreateEmptyTemplateAsync(1, Id(opened, "plain"), timeout.Token);
		Assert.True(created.Accepted, created.Error);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.True(System.Text.RegularExpressions.Regex.IsMatch(text, "<ControlTemplate(?=[^>]*x:Key=\"ButtonTemplate1\")(?=[^>]*TargetType=\"Button\")"), text);
		Assert.Matches("<Button(?=[^>]*x:Name=\"plain\")(?=[^>]*Template=\"\\{StaticResource ButtonTemplate1\\}\")", text);
		Assert.True((await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(2, text), timeout.Token)).Accepted, text);
	}
}
