using ICSharpCode.AndroidDeviceManager;
using ICSharpCode.SharpDevelop.Designer.Devices;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class AndroidDeviceProviderTests
{
    [Fact]
    public void AnAvdBecomesAPhoneWithItsApiLevel()
    {
        var device = AndroidDeviceProvider.ToDevice(new AvdInfo
        {
            Name = "maui_preview_35",
            Device = "pixel_6 (Google)",
            Target = "Google APIs (Google Inc.)",
            BasedOn = "Android API 35",
        });
        Assert.Equal("android:maui_preview_35", device.Id);
        Assert.Equal(DevicePlatform.AndroidEmulator, device.Platform);
        Assert.Equal(DeviceFamily.Phone, device.Family);
        Assert.Equal("maui_preview_35", device.Name);
        Assert.Equal("Android API 35", device.RuntimeLabel);
    }

    [Fact]
    public void WhenTheTextNamesNoApiLevelTheAvdConfigIsRead()
    {
        string dir = Path.Combine(Path.GetTempPath(), "avd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.ini"),
                "hw.device.name=pixel_6\nimage.sysdir.1=system-images/android-34/google_apis/arm64-v8a/\n");
            var device = AndroidDeviceProvider.ToDevice(new AvdInfo
            {
                Name = "p6", Path = dir, Target = "Google APIs (Google Inc.)", BasedOn = "Android 14.0 (\"UpsideDownCake\")",
            });
            Assert.Equal("34", device.RuntimeVersion);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Android 15.0 (API level 35)", "35")]
    [InlineData("Based on: Android API 34", "34")]
    [InlineData("Google APIs", "")]
    public void TheApiLevelIsReadFromTheTargetText(string target, string api)
    {
        var device = AndroidDeviceProvider.ToDevice(new AvdInfo { Name = "x", Target = target });
        Assert.Equal(api, device.RuntimeVersion);
    }

    [Fact]
    public void ATabletProfileOrNameIsATablet()
    {
        Assert.Equal(DeviceFamily.Tablet,
            AndroidDeviceProvider.ToDevice(new AvdInfo { Name = "x", Device = "pixel_tablet" }).Family);
        Assert.Equal(DeviceFamily.Tablet,
            AndroidDeviceProvider.ToDevice(new AvdInfo { Name = "My_Tablet_API_34", Device = "pixel_6" }).Family);
    }
}
