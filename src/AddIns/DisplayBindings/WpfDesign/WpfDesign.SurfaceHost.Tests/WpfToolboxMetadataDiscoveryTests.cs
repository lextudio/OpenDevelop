using System;
using System.IO;
using System.Windows.Controls;


using Xunit;

namespace ICSharpCode.WpfDesign.SurfaceHost.Tests;

public sealed class WpfToolboxMetadataDiscoveryTests
{
	[Fact]
	public void DiscoversPublicCustomControlThroughMetadataOnly()
	{
		var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
			"../../../../SurfaceHost.Tests/Fixtures/CustomControlFixture/bin/Debug/net10.0-windows/CustomControlFixture.dll"));
		if (!File.Exists(fixture))
			fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
				"../../../../WpfDesign.SurfaceHost.Tests/Fixtures/CustomControlFixture/bin/Debug/net10.0-windows/CustomControlFixture.dll"));
		Assert.True(File.Exists(fixture), "CustomControlFixture must be built before this test runs: " + fixture);
		Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly =>
			string.Equals(assembly.GetName().Name, "CustomControlFixture", StringComparison.Ordinal));

		var controls = ICSharpCode.SharpDevelop.Designer.Remote.ToolboxMetadataDiscovery.Discover(fixture, new[] {
			fixture,
			typeof(ContentControl).Assembly.Location,
			typeof(System.Windows.FrameworkElement).Assembly.Location
		});

		var badge = Assert.Single(controls, control => control.Name == "GreetingBadge");
		Assert.Equal("CustomControlFixture", badge.Namespace);
		Assert.Equal("CustomControlFixture", badge.AssemblyName);
		Assert.False(badge.IsUserControl);
		Assert.True(badge.HasPublicDefaultConstructor);
		Assert.False(badge.IsHiddenFromToolbox);
		Assert.True(Assert.Single(controls, control => control.Name == "HiddenBadge").IsHiddenFromToolbox);
		Assert.False(Assert.Single(controls, control => control.Name == "ArgumentBadge").HasPublicDefaultConstructor);
		Assert.Collection(badge.Filters,
			allow => { Assert.Equal("WpfDesigner", allow.FilterString); Assert.Equal("Allow", allow.FilterType); },
			prevent => { Assert.Equal("WinFormsDesigner", prevent.FilterString); Assert.Equal("Prevent", prevent.FilterType); },
			require => { Assert.Equal("WpfSurface", require.FilterString); Assert.Equal("Require", require.FilterType); });
		Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly =>
			string.Equals(assembly.GetName().Name, "CustomControlFixture", StringComparison.Ordinal));
	}
}
