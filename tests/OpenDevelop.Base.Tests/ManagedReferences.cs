using System.Reflection.PortableExecutable;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Compiler references for tests that build a throwaway assembly. The runtime's own file list is not a list of managed
/// assemblies: on Windows <c>TRUSTED_PLATFORM_ASSEMBLIES</c> and the runtime folder also hold native DLLs (coreclr,
/// clrjit, PenImc, ijwhost...), and Roslyn rejects each of those with CS0009 ("PE image doesn't contain managed
/// metadata"). Only files that really carry metadata are references.
/// </summary>
internal static class ManagedReferences
{
	/// <summary>The managed assemblies the test process itself runs on.</summary>
	public static string[] TrustedPlatform()
		=> OnlyManaged(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator));

	/// <summary>The managed assemblies of the shared runtime folder.</summary>
	public static string[] RuntimeDirectory() => InDirectory(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());

	/// <summary>The managed assemblies among the <c>*.dll</c> files of <paramref name="directory"/>.</summary>
	public static string[] InDirectory(string directory) => OnlyManaged(Directory.EnumerateFiles(directory, "*.dll"));

	static string[] OnlyManaged(IEnumerable<string> paths) => paths.Where(IsManaged).ToArray();

	static bool IsManaged(string path)
	{
		try {
			using var stream = File.OpenRead(path);
			using var reader = new PEReader(stream);
			return reader.HasMetadata;
		} catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException) {
			return false;
		}
	}
}
