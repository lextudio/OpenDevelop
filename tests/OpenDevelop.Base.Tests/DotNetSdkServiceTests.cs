using ICSharpCode.SharpDevelop.Project.Sdk;
using Xunit;

namespace OpenDevelop.Base.Tests;

public class DotNetSdkServiceTests
{
	[Fact]
	public void TryDescribeCustomRoot_ReportsMissingDotnetExecutable()
	{
		using var root = new TemporaryDirectory();

		bool valid = DotNetSdkService.TryDescribeCustomRoot(root.Path, out var sdk, out string? error);

		Assert.False(valid);
		Assert.Null(sdk);
		Assert.Contains("dotnet executable", error);
	}

	[Fact]
	public void TryDescribeCustomRoot_ReportsMissingSdkDirectory()
	{
		using var root = new TemporaryDirectory();
		File.WriteAllText(System.IO.Path.Combine(root.Path, "dotnet"), "");

		bool valid = DotNetSdkService.TryDescribeCustomRoot(root.Path, out var sdk, out string? error);

		Assert.False(valid);
		Assert.Null(sdk);
		Assert.Contains("sdk folder", error);
	}

	[Fact]
	public void TryDescribeCustomRoot_ReturnsHighestStableSdk()
	{
		using var root = new TemporaryDirectory();
		File.WriteAllText(System.IO.Path.Combine(root.Path, "dotnet"), "");
		Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "sdk", "9.0.100"));
		Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "sdk", "10.0.200-preview.1"));
		Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "sdk", "10.0.201"));

		bool valid = DotNetSdkService.TryDescribeCustomRoot(root.Path, out var sdk, out string? error);

		Assert.True(valid, error);
		Assert.NotNull(sdk);
		Assert.Equal("10.0.201", sdk.HighestSdkVersion);
		Assert.Equal(DotNetSdkOrigin.Custom, sdk.Origin);
	}

	sealed class TemporaryDirectory : IDisposable
	{
		public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("opendevelop-sdk-test-").FullName;
		public string Path { get; }
		public void Dispose() => Directory.Delete(Path, recursive: true);
	}
}
