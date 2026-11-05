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

	[Fact]
	public void IsFreshBuild_AcceptsOnlyABundleThatARecentBuildWrote()
	{
		var root = Directory.CreateTempSubdirectory("od-ios-bundle-");
		try {
			var bundle = Directory.CreateDirectory(Path.Combine(root.FullName, "App.app")).FullName;
			var executable = Path.Combine(bundle, "App");
			File.WriteAllText(executable, "");
			var written = new DateTime(2026, 11, 4, 12, 0, 30, DateTimeKind.Utc);
			File.SetLastWriteTimeUtc(executable, written);
			var started = written.AddSeconds(-20);
			var finished = written.AddSeconds(5);
			var age = TimeSpan.FromMinutes(2);

			Assert.True(IosSimulatorDebugSupport.IsFreshBuild(bundle, started, finished, finished.AddSeconds(1), age));
			// Written before that build started: an earlier build's output, which this build may not have refreshed
			Assert.False(IosSimulatorDebugSupport.IsFreshBuild(bundle, written.AddSeconds(1), finished, finished.AddSeconds(1), age));
			Assert.False(IosSimulatorDebugSupport.IsFreshBuild(bundle, started, finished, finished + age + TimeSpan.FromSeconds(1), age));
			Assert.False(IosSimulatorDebugSupport.IsFreshBuild(null, started, finished, finished.AddSeconds(1), age));
			Assert.False(IosSimulatorDebugSupport.IsFreshBuild(Path.Combine(root.FullName, "Missing.app"), started, finished, finished.AddSeconds(1), age));
		} finally {
			root.Delete(true);
		}
	}

	[Fact]
	public void BuildChoices_ListsAutomaticThenSimulators_AndChecksTheStoredOne()
	{
		var phone = Simulator("P", DeviceFamily.Phone, "26.5", DeviceState.Booted);
		var pad = Simulator("T", DeviceFamily.Tablet, "26.5", DeviceState.Shutdown);
		var devices = new[] { Device.Host, pad, phone };

		var automatic = IosSimulatorMenuBuilder.BuildChoices(devices, "", "Automatic");
		Assert.Equal(new[] { "Automatic", "Sim P (iOS 26.5) \u2022", "Sim T (iOS 26.5)" }, automatic.Select(c => c.Label));
		Assert.Equal(new[] { true, false, false }, automatic.Select(c => c.IsChecked));
		Assert.Equal(new string?[] { null, "P", "T" }, automatic.Select(c => c.Udid));

		Assert.Equal(new[] { false, false, true }, IosSimulatorMenuBuilder.BuildChoices(devices, "T", "Automatic").Select(c => c.IsChecked));
		Assert.Equal(new[] { false, false, true }, IosSimulatorMenuBuilder.BuildChoices(devices, "ios:T", "Automatic").Select(c => c.IsChecked));
		// A stored simulator that was deleted falls back to Automatic, which is what F5 then does
		Assert.Equal(new[] { true, false, false }, IosSimulatorMenuBuilder.BuildChoices(devices, "gone", "Automatic").Select(c => c.IsChecked));
	}

	static Device Simulator(string udid, DeviceFamily family, string runtime, DeviceState state) =>
		new(DevicePlatform.IosSimulator, "ios:" + udid, "Sim " + udid, family, "iOS", runtime, state);
}
