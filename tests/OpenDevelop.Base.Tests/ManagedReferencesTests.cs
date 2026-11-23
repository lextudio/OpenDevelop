using Xunit;

namespace OpenDevelop.Base.Tests;

public class ManagedReferencesTests
{
	[Fact]
	public void TrustedPlatform_KeepsManagedAssemblies()
	{
		var references = ManagedReferences.TrustedPlatform();
		Assert.Contains(typeof(object).Assembly.Location, references);
		Assert.All(references, path => Assert.True(File.Exists(path), path));
	}

	[Fact]
	public void InDirectory_KeepsManagedAssembliesAndDropsFilesWithoutMetadata()
	{
		// A native DLL (coreclr.dll, PenImc_cor3.dll...) has a PE header but no CLR metadata, and a file that is not a PE
		// image at all is the same case for Roslyn: both are CS0009 when passed as references.
		var directory = Directory.CreateTempSubdirectory("ManagedReferences-");
		try {
			var managed = Path.Combine(directory.FullName, "Managed.dll");
			File.Copy(typeof(object).Assembly.Location, managed);
			var notAnImage = Path.Combine(directory.FullName, "NotAnImage.dll");
			File.WriteAllBytes(notAnImage, new byte[] { 0x4D, 0x5A, 0, 0, 0, 0, 0, 0 });
			var empty = Path.Combine(directory.FullName, "Empty.dll");
			File.WriteAllBytes(empty, Array.Empty<byte>());

			Assert.Equal(new[] { managed }, ManagedReferences.InDirectory(directory.FullName));
		} finally {
			directory.Delete(true);
		}
	}
}
