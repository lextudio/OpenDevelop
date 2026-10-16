using System.Windows.Controls;
using Xunit;

namespace OpenDevelop.XamlDom.Tests;

/// <summary>Guards the matrix itself: a host that claims to run against Microsoft WPF must really load
/// Microsoft.WindowsDesktop.App, and a LibreWPF host must not. Running on Windows says nothing about which it is.</summary>
public sealed class TestHostIdentityTests
{
	[Fact]
	public void HostLoadsTheWpfItClaims()
	{
		var location = typeof(Button).Assembly.Location;
		var isMicrosoft = location.Contains("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase);
		Assert.True(isMicrosoft == !TestHost.IsLibreWpf,
			TestHost.Name + " host loaded PresentationFramework from " + location);
	}
}
