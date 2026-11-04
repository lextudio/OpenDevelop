using Debugger.AddIn.Ios;
using ICSharpCode.SharpDevelop.Designer.Devices;
using Xunit;

namespace OpenDevelop.Base.Tests;

public class IosSimulatorDebugSupportTests
{
	[Theory]
	[InlineData("net11.0-ios", true)]
	[InlineData("net11.0-ios26.0", true)]
	[InlineData("net12.0-ios", true)]
	[InlineData("net10.0-ios", false)] // Mono: not debuggable by the CoreCLR debugger
	[InlineData("net11.0-maccatalyst", false)]
	[InlineData("net11.0", false)]
	[InlineData(null, false)]
	public void IsDebuggableTargetFramework_AcceptsOnlyNet11IosAndLater(string? targetFramework, bool expected)
	{
		Assert.Equal(expected, IosSimulatorDebugSupport.IsDebuggableTargetFramework(targetFramework));
	}

	[Fact]
	public void ParseLaunchedProcessId_ReadsSimctlLaunchOutput()
	{
		Assert.Equal(60596, IosSimulatorDebugSupport.ParseLaunchedProcessId("com.companyname.unospike: 60596\n", "com.companyname.unospike"));
		Assert.Throws<InvalidOperationException>(() => IosSimulatorDebugSupport.ParseLaunchedProcessId("An error was encountered", "com.x"));
	}

	[Fact]
	public void SelectSimulator_PrefersTheProjectChoice_ThenABootedIphone_ThenTheNewestIphone()
	{
		var oldPhone = Simulator("A", DeviceFamily.Phone, "17.2", DeviceState.Shutdown);
		var newPhone = Simulator("B", DeviceFamily.Phone, "26.5", DeviceState.Shutdown);
		var bootedPhone = Simulator("C", DeviceFamily.Phone, "26.0", DeviceState.Booted);
		var bootedPad = Simulator("D", DeviceFamily.Tablet, "26.5", DeviceState.Booted);

		Assert.Same(bootedPad, IosSimulatorProjectBehavior.SelectSimulator(new[] { Device.Host, oldPhone, bootedPhone, bootedPad }, "D"));
		Assert.Same(bootedPad, IosSimulatorProjectBehavior.SelectSimulator(new[] { bootedPad }, "ios:D"));
		Assert.Same(bootedPhone, IosSimulatorProjectBehavior.SelectSimulator(new[] { oldPhone, newPhone, bootedPhone, bootedPad }, "gone"));
		Assert.Same(newPhone, IosSimulatorProjectBehavior.SelectSimulator(new[] { oldPhone, newPhone, bootedPad }, ""));
		Assert.Null(IosSimulatorProjectBehavior.SelectSimulator(new[] { Device.Host }, ""));
		Assert.Equal("D", IosSimulatorProjectBehavior.SimctlUdid(bootedPad));
	}

	static Device Simulator(string udid, DeviceFamily family, string runtime, DeviceState state) =>
		new(DevicePlatform.IosSimulator, "ios:" + udid, "Sim " + udid, family, "iOS", runtime, state);
}
