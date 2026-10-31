using ICSharpCode.SharpDevelop.Project;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The NuGet SDK-resolver manifest names one SDK installation by absolute path, so a copy written on
/// another machine - or one naming an SDK that has since been uninstalled - breaks every SDK-style
/// project load with "Could not load SDK Resolver. A manifest file exists, but the path to the SDK
/// Resolver DLL file could not be found". v5.6.3 shipped such a manifest inside the Windows zip.
/// </summary>
public sealed class NuGetSdkResolverManifestTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), "NuGetSdkResolverManifestTests-" + Guid.NewGuid().ToString("N"));

	public NuGetSdkResolverManifestTests() => Directory.CreateDirectory(Path.Combine(root, "SdkResolvers", ManifestName));

	const string ManifestName = "Microsoft.Build.NuGetSdkResolver";

	string ManifestPath => Path.Combine(root, "SdkResolvers", ManifestName, ManifestName + ".xml");

	void WriteManifest(string declaredPath)
		=> File.WriteAllText(ManifestPath, "<SdkResolver>\n  <Path>" + declaredPath + "</Path>\n</SdkResolver>\n");

	[Fact]
	public void RemovesAManifestWhoseDllIsGone()
	{
		// The shape v5.6.3 shipped: the CI runner's own SDK.
		WriteManifest(@"C:\Program Files\dotnet\sdk\10.0.401\" + ManifestName + ".dll");

		MSBuildInternals.RemoveStaleNuGetSdkResolverManifest(root);

		Assert.False(File.Exists(ManifestPath), "a manifest naming a missing resolver would fail every project load");
	}

	[Fact]
	public void KeepsAManifestWhoseDllExists()
	{
		var resolver = Path.Combine(root, "resolver.dll");
		File.WriteAllText(resolver, "");
		WriteManifest(resolver);

		MSBuildInternals.RemoveStaleNuGetSdkResolverManifest(root);

		Assert.True(File.Exists(ManifestPath));
	}

	[Fact]
	public void IgnoresAManifestWithNoPath()
	{
		WriteManifest("");

		MSBuildInternals.RemoveStaleNuGetSdkResolverManifest(root);

		Assert.False(File.Exists(ManifestPath), "a manifest with no path cannot resolve anything and MSBuild rejects it too");
	}

	[Fact]
	public void DoesNothingWithoutAManifest()
	{
		MSBuildInternals.RemoveStaleNuGetSdkResolverManifest(root);

		Assert.False(File.Exists(ManifestPath));
		Assert.True(Directory.Exists(root));
	}

	[Fact]
	public void SurvivesAManifestThatIsNotXml()
	{
		File.WriteAllText(ManifestPath, "not xml");

		// A malformed file is the app's own to clean up; it must not throw on the startup path.
		MSBuildInternals.RemoveStaleNuGetSdkResolverManifest(root);

		Assert.True(File.Exists(ManifestPath));
	}

	[Fact]
	public void DetectsAWritableApplicationDirectory()
	{
		Assert.True(MSBuildInternals.IsDirectoryWritable(root));
	}

	[Fact]
	public void DetectsAReadOnlyApplicationDirectory()
	{
		Assert.SkipUnless(!OperatingSystem.IsWindows(), "chmod-based read-only test is Unix-only.");
		if (OperatingSystem.IsWindows())
			return;
		string directory = Path.Combine(root, "readonly");
		Directory.CreateDirectory(directory);
		var original = File.GetUnixFileMode(directory);
		File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
		try {
			// A packaged payload extracted under Program Files or /usr/lib lands here: the startup
			// path must treat it as "use the installed SDK's resolver" rather than throw.
			Assert.False(MSBuildInternals.IsDirectoryWritable(directory));
			Assert.Empty(Directory.GetFiles(directory));
		} finally {
			File.SetUnixFileMode(directory, original);
		}
	}

	public void Dispose()
	{
		try { Directory.Delete(root, true); } catch { }
	}
}
