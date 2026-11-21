using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Devices;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The Device Manager shows one collapsible root per platform (the macOS iOS simulator list alone runs to dozens of
/// rows). These pin the grouping order and the remembered expansion state, which the window itself only wires up.
/// </summary>
public sealed class DeviceManagerGroupsTests
{
	static DeviceManagerRow Row(DevicePlatform platform, string name)
	{
		var device = new Device(platform, $"{platform}:{name}", name, DeviceFamily.Phone, "Runtime", "1.0", DeviceState.Shutdown);
		return new DeviceManagerRow(device, false, true, false, true, true, "Stopped");
	}

	[Fact]
	public void Build_MakesOneGroupPerPlatform_InModelOrder()
	{
		var groups = DeviceManagerGroups.Build(new[] {
			Row(DevicePlatform.Host, "this machine"),
			Row(DevicePlatform.IosSimulator, "iPhone 17"),
			Row(DevicePlatform.IosSimulator, "iPad (A16)"),
			Row(DevicePlatform.AndroidEmulator, "pixel"),
		});

		Assert.Equal(new[] { DevicePlatform.Host, DevicePlatform.IosSimulator, DevicePlatform.AndroidEmulator },
			groups.Select(g => g.Platform));
		Assert.Single(groups[0].Rows);
		Assert.Equal(2, groups[1].Rows.Count);
		Assert.Single(groups[2].Rows);
	}

	[Fact]
	public void Build_KeepsEveryRowExactlyOnce()
	{
		var rows = new[] {
			Row(DevicePlatform.Host, "this machine"),
			Row(DevicePlatform.IosSimulator, "a"), Row(DevicePlatform.IosSimulator, "b"),
			Row(DevicePlatform.AndroidEmulator, "c"),
		};

		var flattened = DeviceManagerGroups.Build(rows).SelectMany(g => g.Rows).ToList();

		Assert.Equal(rows.Length, flattened.Count);
		Assert.Equal(rows.Select(r => r.Device.Id), flattened.Select(r => r.Device.Id));
	}

	[Fact]
	public void Build_WithNoRows_HasNoGroups()
	{
		Assert.Empty(DeviceManagerGroups.Build(Array.Empty<DeviceManagerRow>()));
	}

	[Fact]
	public void Expansion_StartsExpanded_AndRemembersCollapse()
	{
		var expansion = new DeviceGroupExpansion();
		Assert.True(expansion.IsExpanded(DevicePlatform.IosSimulator));

		expansion.Set(DevicePlatform.IosSimulator, false);
		Assert.False(expansion.IsExpanded(DevicePlatform.IosSimulator));
		// Other platforms are unaffected.
		Assert.True(expansion.IsExpanded(DevicePlatform.AndroidEmulator));

		expansion.Set(DevicePlatform.IosSimulator, true);
		Assert.True(expansion.IsExpanded(DevicePlatform.IosSimulator));
	}
}
