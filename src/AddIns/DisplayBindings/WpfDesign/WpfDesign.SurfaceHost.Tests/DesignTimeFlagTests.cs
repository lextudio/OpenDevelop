using ICSharpCode.WpfDesign.SurfaceHost;
using Xunit;

namespace WpfDesign.SurfaceHost.Tests;

/// <summary>The in-process Outline's eye (d:IsHidden) and lock (d:IsLocked) toggles.</summary>
public sealed class DesignTimeFlagTests
{
	[Fact]
	public async Task ChildHost_HidesAndLocksElementsWithDesignTimeAttributes()
	{
		const string xaml = """
			<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="root" Width="240" Height="120">
			  <Button x:Name="back" Canvas.Left="10" Canvas.Top="10" Width="100" Height="40" Content="Back"/>
			  <Button x:Name="front" Canvas.Left="10" Canvas.Top="10" Width="100" Height="40" Content="Front"/>
			</Canvas>
			""";
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await WpfSurfaceHostClient.StartAsync(WpfSurfaceHostRpcTests.HostDll(), timeout.Token);
		var opened = await client.OpenAsync(WpfSurfaceHostRpcTests.Snapshot(1, xaml), timeout.Token);
		Assert.True(opened.Accepted, opened.Error);
		Assert.Null(opened.Tree!.IsDesignTimeHidden);
		Assert.Equal((false, false), (opened.Tree.Children[1].IsDesignTimeHidden, opened.Tree.Children[1].IsDesignTimeLocked));

		// Locking publishes the flag (the IDE's hit tester then skips the element).
		var locked = await client.SetDesignTimeFlagAsync(1, opened.Tree.Children[1].Id, "Locked", true, timeout.Token);
		Assert.True(locked.Accepted, locked.Error);
		Assert.True(locked.Tree!.Children[1].IsDesignTimeLocked);

		// Hiding takes the element off the surface (and out of hit testing) but not out of the tree.
		var hidden = await client.SetDesignTimeFlagAsync(1, opened.Tree.Children[0].Id, "Hidden", true, timeout.Token);
		Assert.True(hidden.Accepted, hidden.Error);
		Assert.True(hidden.Tree!.Children[0].IsDesignTimeHidden);
		Assert.False(hidden.Tree.Children[0].IsVisible);
		var text = Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Contains("IsLocked=\"True\"", text, StringComparison.Ordinal);
		Assert.Contains("IsHidden=\"True\"", text, StringComparison.Ordinal);

		// Clearing removes the attribute; the root and unknown flags are rejected.
		var unlocked = await client.SetDesignTimeFlagAsync(1, opened.Tree.Children[1].Id, "Locked", false, timeout.Token);
		Assert.True(unlocked.Accepted, unlocked.Error);
		Assert.DoesNotContain("IsLocked", Assert.Single((await client.FlushAsync(1, timeout.Token)).Files).Text, StringComparison.Ordinal);
		Assert.False((await client.SetDesignTimeFlagAsync(1, opened.Tree.Id, "Hidden", true, timeout.Token)).Accepted);
		Assert.False((await client.SetDesignTimeFlagAsync(1, opened.Tree.Children[1].Id, "Frozen", true, timeout.Token)).Accepted);
	}
}
