using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using ICSharpCode.ILSpyAddIn;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// doc/technotes/decompiled-debugging.md: ILSpy generates the decompiled sources and the PDB for a module without symbols; the PDB's
/// documents are the files it wrote, so a debugger that reports a document name reports a real, matching file.
/// </summary>
public sealed class DecompiledSymbolProviderTests : IDisposable
{
	readonly string cacheRoot = Path.Combine(Path.GetTempPath(), "od-decompiled-symbols-" + Guid.NewGuid().ToString("N"));
	static readonly string LinqPath = typeof(System.Linq.Enumerable).Assembly.Location;

	public void Dispose()
	{
		if (!Directory.Exists(cacheRoot))
			return;
		foreach (var path in Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories))
			File.SetAttributes(path, FileAttributes.Normal);
		Directory.Delete(cacheRoot, true);
	}

	static Guid ReadMvid(string assemblyPath)
	{
		using var peReader = new PEReader(File.OpenRead(assemblyPath));
		var reader = peReader.GetMetadataReader();
		return reader.GetGuid(reader.GetModuleDefinition().Mvid);
	}

	[Fact]
	public async Task Generates_APdb_WhoseDocumentsAreTheDecompiledFilesOnDisk()
	{
		var mvid = ReadMvid(LinqPath);
		var provider = new DecompiledSymbolProvider(cacheRoot);

		var pdbPath = await provider.GetSymbolsAsync(LinqPath, mvid, null, TestContext.Current.CancellationToken);

		Assert.NotNull(pdbPath);
		var moduleDirectory = DecompiledSymbolProvider.GetModuleDirectory(cacheRoot, LinqPath, mvid);
		Assert.Equal(Path.Combine(moduleDirectory, "System.Linq.pdb"), pdbPath);
		Assert.True(File.Exists(Path.Combine(moduleDirectory, ".complete")));

		using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdbPath));
		var pdb = pdbProvider.GetMetadataReader();
		var documents = pdb.Documents.Select(pdb.GetDocument).ToList();
		Assert.NotEmpty(documents);
		var sourceRoot = Path.Combine(moduleDirectory, "src");
		foreach (var document in documents) {
			var name = pdb.GetString(document.Name);
			// The name is the absolute path of a file below this module's source root
			Assert.True(Path.IsPathRooted(name), name);
			Assert.StartsWith(sourceRoot, name, StringComparison.Ordinal);
			Assert.True(File.Exists(name), name);
			// The debugger checks the file against the PDB: the hash is of exactly these bytes
			Assert.Equal(SHA256.HashData(File.ReadAllBytes(name)), pdb.GetBlobBytes(document.Hash));
			Assert.True(new FileInfo(name).Attributes.HasFlag(FileAttributes.ReadOnly), "decompiled sources are read-only: " + name);
		}
		Assert.Contains(pdb.MethodDebugInformation.Select(pdb.GetMethodDebugInformation), m => !m.SequencePointsBlob.IsNil);
	}

	[Fact]
	public async Task AModuleThatWasCompletelyGenerated_IsNotDecompiledAgain()
	{
		var mvid = ReadMvid(LinqPath);
		var provider = new DecompiledSymbolProvider(cacheRoot);
		var first = await provider.GetSymbolsAsync(LinqPath, mvid, null, TestContext.Current.CancellationToken);
		var written = File.GetLastWriteTimeUtc(first!);

		var second = await new DecompiledSymbolProvider(cacheRoot).GetSymbolsAsync(LinqPath, mvid, null, TestContext.Current.CancellationToken);

		Assert.Equal(first, second);
		Assert.Equal(written, File.GetLastWriteTimeUtc(second!));
	}

	[Fact]
	public async Task TheSameAssemblyPathWithAnotherMvid_GetsItsOwnDirectory()
	{
		var other = Guid.NewGuid();
		Assert.NotEqual(
			DecompiledSymbolProvider.GetModuleDirectory(cacheRoot, LinqPath, ReadMvid(LinqPath)),
			DecompiledSymbolProvider.GetModuleDirectory(cacheRoot, LinqPath, other));
		await Task.CompletedTask;
	}

	[Fact]
	public async Task AMissingAssembly_HasNoSymbols()
	{
		var provider = new DecompiledSymbolProvider(cacheRoot);
		Assert.Null(await provider.GetSymbolsAsync(Path.Combine(cacheRoot, "missing.dll"), Guid.NewGuid(), null, TestContext.Current.CancellationToken));
		Assert.Null(await provider.GetSymbolsAsync(null!, Guid.NewGuid(), null, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task AModuleDirectoryWithoutTheCompleteMarker_IsDeletedAndRegenerated()
	{
		var mvid = ReadMvid(LinqPath);
		var moduleDirectory = DecompiledSymbolProvider.GetModuleDirectory(cacheRoot, LinqPath, mvid);
		// What a killed IDE leaves behind: a PDB and read-only sources, but no marker
		var stale = Path.Combine(moduleDirectory, "src", "Stale", "Old.cs");
		Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
		File.WriteAllText(stale, "// from an earlier, incomplete run");
		File.WriteAllText(Path.Combine(moduleDirectory, "System.Linq.pdb"), "not a pdb");
		File.SetAttributes(stale, FileAttributes.ReadOnly);
		// A neighbour that is not part of the module must survive
		var neighbour = Path.Combine(cacheRoot, "System.Linq", "neighbour.txt");
		File.WriteAllText(neighbour, "other module");

		var pdbPath = await new DecompiledSymbolProvider(cacheRoot).GetSymbolsAsync(LinqPath, mvid, null, TestContext.Current.CancellationToken);

		Assert.False(File.Exists(stale), "the half-written sources are thrown away");
		Assert.True(File.Exists(Path.Combine(moduleDirectory, ".complete")));
		using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdbPath!));
		Assert.NotEmpty(pdbProvider.GetMetadataReader().Documents);
		Assert.True(File.Exists(neighbour), "only this module's directory is deleted");
	}

	[Fact]
	public async Task ProvidersWithDifferentCacheRoots_DoNotShareAGeneration()
	{
		var mvid = ReadMvid(LinqPath);
		var otherRoot = cacheRoot + "-other";
		try {
			var first = new DecompiledSymbolProvider(cacheRoot).GetSymbolsAsync(LinqPath, mvid, null, TestContext.Current.CancellationToken);
			var second = new DecompiledSymbolProvider(otherRoot).GetSymbolsAsync(LinqPath, mvid, null, TestContext.Current.CancellationToken);
			var paths = await Task.WhenAll(first, second);

			Assert.StartsWith(cacheRoot, paths[0], StringComparison.Ordinal);
			Assert.StartsWith(otherRoot, paths[1], StringComparison.Ordinal);
			Assert.True(File.Exists(paths[0]) && File.Exists(paths[1]));
		} finally {
			if (Directory.Exists(otherRoot)) {
				foreach (var path in Directory.EnumerateFiles(otherRoot, "*", SearchOption.AllDirectories))
					File.SetAttributes(path, FileAttributes.Normal);
				Directory.Delete(otherRoot, true);
			}
		}
	}

	[Fact]
	public async Task ACancelledWait_DoesNotCancelTheGenerationForTheNextRequest()
	{
		var mvid = ReadMvid(LinqPath);
		var provider = new DecompiledSymbolProvider(cacheRoot);
		using var cancelled = new CancellationTokenSource();
		cancelled.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetSymbolsAsync(LinqPath, mvid, null, cancelled.Token));
		var pdbPath = await provider.GetSymbolsAsync(LinqPath, mvid, null, TestContext.Current.CancellationToken);

		Assert.NotNull(pdbPath);
		Assert.True(File.Exists(pdbPath));
	}

	/// <summary>A minimal valid assembly with no debug directory at all (no CodeView entry), like one built with DebugType=none.</summary>
	static string WriteAssemblyWithoutADebugDirectory(string directory)
	{
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "NoDebugInfo.dll");
		var metadata = new MetadataBuilder();
		metadata.AddModule(0, metadata.GetOrAddString("NoDebugInfo.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
		metadata.AddAssembly(metadata.GetOrAddString("NoDebugInfo"), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
		metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
		var peBuilder = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll), new MetadataRootBuilder(metadata), new BlobBuilder(), flags: CorFlags.ILOnly);
		var image = new BlobBuilder();
		peBuilder.Serialize(image);
		File.WriteAllBytes(path, image.ToArray());
		return path;
	}

	[Fact]
	public async Task AModuleWithoutACodeViewEntry_IsRefusedBeforeAnythingIsDecompiledAndNotTriedAgain()
	{
		var assemblyPath = WriteAssemblyWithoutADebugDirectory(Path.Combine(cacheRoot, "input"));
		using (var pe = new ICSharpCode.Decompiler.Metadata.PEFile(assemblyPath))
			Assert.False(DecompiledPdbWriter.HasCodeViewDebugDirectoryEntry(pe), "the test assembly must not have a CodeView entry");
		var mvid = ReadMvid(assemblyPath);
		var provider = new DecompiledSymbolProvider(cacheRoot);
		var messages = new List<string>();
		var progress = new SynchronousProgress(messages.Add);

		Assert.Null(await provider.GetSymbolsAsync(assemblyPath, mvid, progress, TestContext.Current.CancellationToken));
		Assert.Null(await provider.GetSymbolsAsync(assemblyPath, mvid, progress, TestContext.Current.CancellationToken));

		Assert.False(Directory.Exists(DecompiledSymbolProvider.GetModuleDirectory(cacheRoot, assemblyPath, mvid)), "nothing was written");
		Assert.Single(messages);
		Assert.Contains("no debug directory entry", messages[0]);
	}

	sealed class SynchronousProgress(Action<string> report) : IProgress<string>
	{
		public void Report(string value) => report(value);
	}

	[Fact]
	public async Task AFailingGeneration_IsRetriedAFewTimesAndThenLeftAlone()
	{
		// Not an assembly at all: opening it throws, which is the exception path (a transient error looks the same to the provider)
		Directory.CreateDirectory(cacheRoot);
		var broken = Path.Combine(cacheRoot, "Broken.dll");
		File.WriteAllText(broken, "this is not a portable executable");
		var mvid = Guid.NewGuid();
		var provider = new DecompiledSymbolProvider(cacheRoot);
		var messages = new List<string>();
		var progress = new SynchronousProgress(messages.Add);

		for (int attempt = 1; attempt <= 3; attempt++) {
			Assert.Null(await provider.GetSymbolsAsync(broken, mvid, progress, TestContext.Current.CancellationToken));
			Assert.Equal(attempt, messages.Count);
			Assert.Contains("attempt " + attempt + " of 3", messages[attempt - 1]);
		}
		// Enough: the adapter asks at every stop and a whole-module decompile is expensive
		Assert.Null(await provider.GetSymbolsAsync(broken, mvid, progress, TestContext.Current.CancellationToken));
		Assert.Equal(3, messages.Count);
		var moduleDirectory = DecompiledSymbolProvider.GetModuleDirectory(cacheRoot, broken, mvid);
		Assert.False(Directory.Exists(moduleDirectory));
		Assert.False(Directory.Exists(Path.Combine(cacheRoot, "Broken")), "a failure before anything is generated leaves nothing behind");
	}

	[Fact]
	public async Task Generation_HappensInAPrivateDirectory_AndLeavesNoneBehind_AndSweepsOnlyStaleOnes()
	{
		var mvid = ReadMvid(LinqPath);
		var moduleDirectory = DecompiledSymbolProvider.GetModuleDirectory(cacheRoot, LinqPath, mvid);
		// Leftovers next to the module: a long-dead instance's directory, and a recent one that may belong to a live instance
		var stale = moduleDirectory + ".tmp-1-aaaaaaaa";
		var recent = moduleDirectory + ".tmp-2-bbbbbbbb";
		Directory.CreateDirectory(stale);
		Directory.CreateDirectory(recent);
		File.WriteAllText(Path.Combine(stale, "half.cs"), "x");
		Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

		var pdbPath = await new DecompiledSymbolProvider(cacheRoot).GetSymbolsAsync(LinqPath, mvid, null, TestContext.Current.CancellationToken);

		Assert.True(File.Exists(pdbPath));
		Assert.False(Directory.Exists(stale), "a day-old private directory is the leftover of a killed instance");
		Assert.True(Directory.Exists(recent), "a recent one may belong to a running instance");
		var ours = Directory.GetDirectories(Path.GetDirectoryName(moduleDirectory)!, "*.tmp-*").Select(Path.GetFileName).ToList();
		Assert.Equal(new[] { Path.GetFileName(recent) }, ours);
	}

	[Fact]
	public async Task AModuleAnotherInstanceCompletedFirst_IsUsedAsItIs()
	{
		var mvid = ReadMvid(LinqPath);
		var moduleDirectory = DecompiledSymbolProvider.GetModuleDirectory(cacheRoot, LinqPath, mvid);
		// What a finished instance leaves: marker and PDB. This instance must neither regenerate nor touch it.
		Directory.CreateDirectory(moduleDirectory);
		var pdb = Path.Combine(moduleDirectory, "System.Linq.pdb");
		File.WriteAllText(pdb, "written by the other instance");
		File.WriteAllText(Path.Combine(moduleDirectory, ".complete"), "");
		var written = File.GetLastWriteTimeUtc(pdb);

		var result = await new DecompiledSymbolProvider(cacheRoot).GetSymbolsAsync(LinqPath, mvid, null, TestContext.Current.CancellationToken);

		Assert.Equal(pdb, result);
		Assert.Equal("written by the other instance", File.ReadAllText(pdb));
		Assert.Equal(written, File.GetLastWriteTimeUtc(pdb));
	}

	[Fact]
	public async Task AMarkerWithoutItsPdb_IsNotACompleteModule_AndIsReplaced()
	{
		var mvid = ReadMvid(LinqPath);
		var moduleDirectory = DecompiledSymbolProvider.GetModuleDirectory(cacheRoot, LinqPath, mvid);
		// What a partly failed delete of an earlier directory can leave: the marker is there, the PDB is not
		Directory.CreateDirectory(moduleDirectory);
		File.WriteAllText(Path.Combine(moduleDirectory, ".complete"), "");

		var pdbPath = await new DecompiledSymbolProvider(cacheRoot).GetSymbolsAsync(LinqPath, mvid, null, TestContext.Current.CancellationToken);

		Assert.Equal(Path.Combine(moduleDirectory, "System.Linq.pdb"), pdbPath);
		Assert.True(File.Exists(pdbPath), "the PDB path that was returned exists");
		Assert.NotEmpty(Directory.GetFiles(Path.Combine(moduleDirectory, "src"), "*.cs", SearchOption.AllDirectories));
	}

	[Fact]
	public async Task PublishingWaitsForAnotherInstanceThatIsPublishing_AndNeverTouchesItsDirectory()
	{
		var mvid = ReadMvid(LinqPath);
		var moduleDirectory = DecompiledSymbolProvider.GetModuleDirectory(cacheRoot, LinqPath, mvid);
		Directory.CreateDirectory(Path.GetDirectoryName(moduleDirectory)!);
		// Another instance holds the exclusive publish lock of this module
		var otherInstance = new FileStream(moduleDirectory + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		Task<string> generation;
		try {
			generation = new DecompiledSymbolProvider(cacheRoot).GetSymbolsAsync(LinqPath, mvid, null, TestContext.Current.CancellationToken);

			// The decompiling is not serialised: wait until this instance has finished its private directory and is waiting to publish it
			var parent = Path.GetDirectoryName(moduleDirectory)!;
			var deadline = DateTime.UtcNow.AddMinutes(3);
			while (!Directory.GetDirectories(parent, "*.tmp-*").Any(d => File.Exists(Path.Combine(d, ".complete")))) {
				Assert.True(DateTime.UtcNow < deadline, "the generation never finished its private directory");
				await Task.Delay(50, TestContext.Current.CancellationToken);
			}
			await Task.Delay(300, TestContext.Current.CancellationToken);
			Assert.False(generation.IsCompleted, "publishing must wait for the lock");
			Assert.False(Directory.Exists(moduleDirectory), "nothing is put in place while another instance holds the lock");
		} finally {
			otherInstance.Dispose();
		}

		var pdbPath = await generation;
		Assert.True(File.Exists(pdbPath));
		Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(moduleDirectory)!, "*.tmp-*"));
	}
}
