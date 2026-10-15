using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WpfDesign.SurfaceHost;
using Xunit;

namespace WpfDesign.SurfaceHost.Tests;

public sealed class ResourceReferenceEditTests
{
	const string Xaml = """
		<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="160">
		  <StackPanel.Resources>
		    <Style x:Key="Primary" TargetType="Button"><Setter Property="Width" Value="80"/></Style>
		    <Style x:Key="Other" TargetType="Button"><Setter Property="Height" Value="30"/></Style>
		    <SolidColorBrush x:Key="Accent" Color="Red"/>
		  </StackPanel.Resources>
		  <Button x:Name="referenced" Content="R" Style="{StaticResource Primary}"/>
		  <Button x:Name="unset" Content="U"/>
		  <Button x:Name="inline" Content="I"><Button.Style><Style TargetType="Button"><Setter Property="Width" Value="40"/></Style></Button.Style></Button>
		  <Button x:Name="dynamic" Content="D" Style="{DynamicResource Other}"/>
		</StackPanel>
		""";

	static DesignerPropertyInfo Style(DesignerSessionState state, int index)
		=> Assert.Single(state.Tree!.Children[index].Properties, property => property.Name == "Style");

	[Fact]
	public async Task ChildHost_OffersAResourcePickerForReferencesAndUnsetStyles()
	{
		using var timeout = new CancellationTokenSource(System.TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);

		var referenced = Style(opened, 0);
		Assert.Equal(("ResourceReference", "Static", "{StaticResource Primary}"), (referenced.EditorKind, referenced.ResourceReferenceKind, referenced.Value));
		// Only resources whose value fits a Style are offered, not the brush.
		Assert.Equal(new[] { "Other", "Primary" }, referenced.ResourceReferenceKeys);

		var unset = Style(opened, 1);
		Assert.Equal(("ResourceReference", ""), (unset.EditorKind, unset.ResourceReferenceKind));
		Assert.Equal(new[] { "Other", "Primary" }, unset.ResourceReferenceKeys);

		Assert.NotEqual("ResourceReference", Style(opened, 2).EditorKind);   // an inline Style is not a reference
		Assert.Equal("Dynamic", Style(opened, 3).ResourceReferenceKind);
	}

	[Fact]
	public async Task ChildHost_WritesSwitchesAndClearsResourceReferences()
	{
		using var timeout = new CancellationTokenSource(System.TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		string Text() => Assert.Single(client.FlushAsync(1, timeout.Token).GetAwaiter().GetResult().Files).Text;
		var children = opened.Tree!.Children;

		var created = await client.SetResourceReferenceAsync(1, children[1].Id, "Style", "Dynamic", "Other", timeout.Token);
		Assert.True(created.Accepted, created.Error);
		Assert.Matches("x:Name=\"unset\"[^>]*Style=\"\\{DynamicResource Other\\}\"|Style=\"\\{DynamicResource Other\\}\"[^>]*x:Name=\"unset\"", Text());
		Assert.Equal(("Dynamic", "{DynamicResource Other}"), (Style(created, 1).ResourceReferenceKind, Style(created, 1).Value));

		var switched = await client.SetResourceReferenceAsync(1, children[0].Id, "Style", "Dynamic", "Other", timeout.Token);
		Assert.True(switched.Accepted, switched.Error);
		Assert.DoesNotContain("{StaticResource Primary}", Text(), System.StringComparison.Ordinal);

		var cleared = await client.SetResourceReferenceAsync(1, children[3].Id, "Style", "", "", timeout.Token);
		Assert.True(cleared.Accepted, cleared.Error);
		Assert.Equal("", Style(cleared, 3).ResourceReferenceKind);
		var dynamicTag = System.Text.RegularExpressions.Regex.Match(Text(), "<Button[^>]*x:Name=\"dynamic\"[^>]*>").Value;
		Assert.NotEmpty(dynamicTag);
		Assert.DoesNotContain("Style=", dynamicTag, System.StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_RefusesResourceReferenceEditsThatWouldBeWrongOrLossy()
	{
		using var timeout = new CancellationTokenSource(System.TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, Xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		var before = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		var children = opened.Tree!.Children;

		// A brush, an unknown key and a bad kind are refused; so is overwriting an inline Style, or a Brush property.
		Assert.False((await client.SetResourceReferenceAsync(1, children[1].Id, "Style", "Static", "Accent", timeout.Token)).Accepted);
		Assert.False((await client.SetResourceReferenceAsync(1, children[1].Id, "Style", "Static", "Missing", timeout.Token)).Accepted);
		Assert.False((await client.SetResourceReferenceAsync(1, children[1].Id, "Style", "Sideways", "Primary", timeout.Token)).Accepted);
		Assert.False((await client.SetResourceReferenceAsync(1, children[2].Id, "Style", "Static", "Primary", timeout.Token)).Accepted);
		Assert.False((await client.SetResourceReferenceAsync(1, children[1].Id, "Background", "Static", "Accent", timeout.Token)).Accepted);
		Assert.Equal(before, Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text);
	}
}
