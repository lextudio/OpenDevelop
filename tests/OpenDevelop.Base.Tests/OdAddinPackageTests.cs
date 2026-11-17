using System.IO.Compression;
using System.Text;
using ICSharpCode.AddInManager2.Model;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using Xunit;
using static OpenDevelop.Base.Tests.AddInTestSupport;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Installing a .odaddin: reading and unpacking an untrusted zip, validating the identity that becomes the
/// install folder, and recording (or refusing to record) where an add-in came from.
/// </summary>
public class OdAddinPackageTests : IDisposable
{
	readonly string directory = Path.Combine(Path.GetTempPath(), "od-odaddin-" + Guid.NewGuid().ToString("N"));

	public OdAddinPackageTests()
	{
		Directory.CreateDirectory(directory);
		AddInTestSupport.InitializeServices();
	}

	public void Dispose()
	{
		SD.TearDownForUnitTests();
		if (Directory.Exists(directory))
			Directory.Delete(directory, true);
	}

	// ---- helpers ------------------------------------------------------------------------------------------

	static string Manifest(string identity = "Vendor.Sample", string version = "1.2.3", string extraAttributes = "") =>
		AddInTestSupport.Manifest(identity, version, extraAttributes);

	string Package(string name, params (string Name, string Text)[] entries) => AddInTestSupport.Package(directory, name, entries);

	string GoodPackage(string name = "Sample.odaddin", string identity = "Vendor.Sample", string extraAttributes = "") =>
		Package(name,
			("Vendor.Sample.addin", Manifest(identity, extraAttributes: extraAttributes)),
			("Sample.dll", "dll"),
			("Resources/strings.txt", "hello"));

	/// <summary>
	/// Rewrites the declared uncompressed size of the named entry (central directory and local header) so
	/// the zip claims less than it holds: a package lying about its size.
	/// </summary>
	static void ShrinkDeclaredSize(string packageFile, string entryName, uint declared)
	{
		byte[] data = File.ReadAllBytes(packageFile);
		byte[] name = Encoding.UTF8.GetBytes(entryName);
		int patched = 0;
		for (int i = 0; i + 46 < data.Length; i++)
		{
			bool central = data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 0x01 && data[i + 3] == 0x02;
			bool local = data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 0x03 && data[i + 3] == 0x04;
			if (!central && !local)
				continue;
			int nameOffset = central ? i + 46 : i + 30;
			int nameLength = BitConverter.ToUInt16(data, central ? i + 28 : i + 26);
			if (nameLength != name.Length || !data.AsSpan(nameOffset, nameLength).SequenceEqual(name))
				continue;
			int sizeOffset = central ? i + 24 : i + 22;
			BitConverter.GetBytes(declared).CopyTo(data, sizeOffset);
			patched++;
		}
		Assert.Equal(2, patched); // one local header and one central directory record
		File.WriteAllBytes(packageFile, data);
	}

	static AddInRelease Release(string repo = "owner/my-addin", string tag = "v1.2.3") => AddInTestSupport.Release(repo, tag);

	// ---- identity -----------------------------------------------------------------------------------------

	[Theory]
	[InlineData("Vendor.Sample")]
	[InlineData("ICSharpCode.WorkflowDesigner")]
	[InlineData("a")]
	[InlineData("My-Addin_2.Beta")]
	public void IsSafeIdentity_AcceptsOrdinaryNames(string identity)
	{
		Assert.True(OdAddinPackage.IsSafeIdentity(identity));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(".")]
	[InlineData("..")]
	[InlineData("../outside")]
	[InlineData("..\\outside")]
	[InlineData("a/b")]
	[InlineData("a\\b")]
	[InlineData("/rooted")]
	[InlineData("C:\\temp")]
	[InlineData("C:")]
	[InlineData(".hidden")]
	[InlineData("trailing.")]
	[InlineData("a..b")]
	[InlineData("has space")]
	[InlineData("tab\there")]
	[InlineData("nul\0byte")]
	public void IsSafeIdentity_RejectsAnythingThatCouldNameAnotherFolder(string identity)
	{
		Assert.False(OdAddinPackage.IsSafeIdentity(identity));
	}

	[Fact]
	public void IsSafeIdentity_RejectsAnOverlongName()
	{
		Assert.True(OdAddinPackage.IsSafeIdentity(new string('a', 200)));
		Assert.False(OdAddinPackage.IsSafeIdentity(new string('a', 201)));
	}

	// ---- reading the manifest -----------------------------------------------------------------------------

	[Fact]
	public void ReadManifestText_ReturnsTheRootManifest()
	{
		string text = OdAddinPackage.ReadManifestText(GoodPackage());

		Assert.Contains("Vendor.Sample", text);
	}

	[Fact]
	public void ReadManifestText_IgnoresAManifestInASubfolder()
	{
		string package = Package("nested.odaddin", ("Vendor.Sample.addin", Manifest()), ("samples/other.addin", Manifest("Vendor.Other")));

		Assert.Contains("Vendor.Sample", OdAddinPackage.ReadManifestText(package));
	}

	[Fact]
	public void ReadManifestText_RefusesNoManifestSeveralManifestsAndNonZips()
	{
		Assert.Contains("no .addin", Assert.Throws<InvalidDataException>(() =>
			OdAddinPackage.ReadManifestText(Package("none.odaddin", ("Sample.dll", "x")))).Message);
		Assert.Contains("several", Assert.Throws<InvalidDataException>(() =>
			OdAddinPackage.ReadManifestText(Package("two.odaddin", ("A.addin", Manifest()), ("B.addin", Manifest("Vendor.B"))))).Message);
		string notAZip = Path.Combine(directory, "bad.odaddin");
		File.WriteAllText(notAZip, "this is not a zip");
		Assert.Throws<InvalidDataException>(() => OdAddinPackage.ReadManifestText(notAZip));
		Assert.Throws<InvalidDataException>(() => OdAddinPackage.ReadManifestText(Path.Combine(directory, "missing.odaddin")));
	}

	[Fact]
	public void ReadManifestText_RefusesAManifestBiggerThanTheCap_EvenWhenTheHeaderUnderstatesIt()
	{
		string padding = new string(' ', OdAddinPackage.MaxManifestBytes + 1000);
		string honest = Package("big.odaddin", ("Vendor.Sample.addin", Manifest() + padding));
		string lying = Package("lying.odaddin", ("Vendor.Sample.addin", Manifest() + padding));
		ShrinkDeclaredSize(lying, "Vendor.Sample.addin", 100);

		Assert.Contains("larger than", Assert.Throws<InvalidDataException>(() => OdAddinPackage.ReadManifestText(honest)).Message);
		// .NET itself truncates a lying entry and fails on its CRC; either way the package is refused.
		Assert.Throws<InvalidDataException>(() => OdAddinPackage.ReadManifestText(lying));
	}

	[Fact]
	public void ReadAtMost_StopsAtTheCap_WhateverTheStreamClaims()
	{
		byte[] atCap = new byte[1000];
		Assert.Equal(1000, OdAddinPackage.ReadAtMost(new MemoryStream(atCap), 1000).Length);
		Assert.Empty(OdAddinPackage.ReadAtMost(new MemoryStream(), 1000));
		Assert.Contains("larger than", Assert.Throws<InvalidDataException>(() =>
			OdAddinPackage.ReadAtMost(new MemoryStream(new byte[1001]), 1000)).Message);
		// A stream that never ends must be cut off, not buffered.
		Assert.Throws<InvalidDataException>(() => OdAddinPackage.ReadAtMost(new EndlessStream(), 1 << 20));
	}

	[Fact]
	public void CopyAtMost_RefusesMoreBytesThanDeclared_AndAcceptsExactlyTheDeclaredAmount()
	{
		var exact = new MemoryStream();
		OdAddinPackage.CopyAtMost(new MemoryStream(new byte[5000]), exact, 5000);
		Assert.Equal(5000, exact.Length);

		Assert.Contains("declared size", Assert.Throws<InvalidDataException>(() =>
			OdAddinPackage.CopyAtMost(new MemoryStream(new byte[5001]), new MemoryStream(), 5000)).Message);
		Assert.Throws<InvalidDataException>(() => OdAddinPackage.CopyAtMost(new EndlessStream(), new MemoryStream(), 1 << 20));
	}

	sealed class EndlessStream : Stream
	{
		public override int Read(byte[] buffer, int offset, int count) { Array.Clear(buffer, offset, count); return count; }
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	[Fact]
	public void ReadManifestText_MatchesTheExtensionCaseInsensitively()
	{
		string package = Package("upper.odaddin", ("Vendor.Sample.ADDIN", Manifest()));

		Assert.Contains("Vendor.Sample", OdAddinPackage.ReadManifestText(package));
	}

	[Fact]
	public void FindUnpackedManifest_FindsOneManifestAnyCase_AndNothingOtherwise()
	{
		string one = Path.Combine(directory, "one");
		Directory.CreateDirectory(one);
		File.WriteAllText(Path.Combine(one, "Vendor.Sample.ADDIN"), "x");
		File.WriteAllText(Path.Combine(one, "Sample.dll"), "x");
		string none = Path.Combine(directory, "none");
		Directory.CreateDirectory(none);
		File.WriteAllText(Path.Combine(none, "Sample.dll"), "x");
		string two = Path.Combine(directory, "two");
		Directory.CreateDirectory(two);
		File.WriteAllText(Path.Combine(two, "a.addin"), "x");
		File.WriteAllText(Path.Combine(two, "b.ADDIN"), "x");
		string nested = Path.Combine(directory, "nested");
		Directory.CreateDirectory(Path.Combine(nested, "sub"));
		File.WriteAllText(Path.Combine(nested, "sub", "a.addin"), "x");

		Assert.Equal("Vendor.Sample.ADDIN", OdAddinPackage.FindUnpackedManifest(one)!.Name);
		Assert.Null(OdAddinPackage.FindUnpackedManifest(none));
		Assert.Null(OdAddinPackage.FindUnpackedManifest(two));
		Assert.Null(OdAddinPackage.FindUnpackedManifest(nested));
	}

	// ---- unpacking ----------------------------------------------------------------------------------------

	[Fact]
	public void Extract_UnpacksFilesAndSubfolders()
	{
		string target = Path.Combine(directory, "out");

		OdAddinPackage.Extract(GoodPackage(), target);

		Assert.True(File.Exists(Path.Combine(target, "Vendor.Sample.addin")));
		Assert.Equal("dll", File.ReadAllText(Path.Combine(target, "Sample.dll")));
		Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "Resources", "strings.txt")));
	}

	[Theory]
	[InlineData("../escaped.txt")]
	[InlineData("a/../../escaped.txt")]
	[InlineData("..\\escaped.txt")]
	[InlineData("/etc/escaped.txt")]
	[InlineData("C:/escaped.txt")]
	[InlineData("C:\\escaped.txt")]
	public void Extract_RefusesAnEntryThatWouldLeaveTheFolder_AndWritesNothingOutside(string entryName)
	{
		string package = Package("evil.odaddin", ("Vendor.Sample.addin", Manifest()), (entryName, "gotcha"));
		string target = Path.Combine(directory, "out");

		Assert.Throws<InvalidDataException>(() => OdAddinPackage.Extract(package, target));

		Assert.False(File.Exists(Path.Combine(directory, "escaped.txt")));
		Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(directory)!, "escaped.txt")));
	}

	[Fact]
	public void Extract_RefusesADuplicateEntry_InsteadOfOverwriting()
	{
		string path = Path.Combine(directory, "dup.odaddin");
		using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
		{
			foreach (string text in new[] { "first", "second" })
			{
				using var writer = new StreamWriter(zip.CreateEntry("Sample.dll").Open());
				writer.Write(text);
			}
		}
		string target = Path.Combine(directory, "out");

		Assert.Throws<InvalidDataException>(() => OdAddinPackage.Extract(path, target));
	}

	[Fact]
	public void Extract_RefusesAnEntryLargerThanItsDeclaredSize()
	{
		string package = Package("lying.odaddin", ("Vendor.Sample.addin", Manifest()), ("Sample.dll", new string('x', 5000)));
		ShrinkDeclaredSize(package, "Sample.dll", 10);

		// .NET itself truncates a lying entry and fails on its CRC; either way the package is refused.
		Assert.Throws<InvalidDataException>(() => OdAddinPackage.Extract(package, Path.Combine(directory, "out")));
	}

	[Fact]
	public void Extract_RefusesMoreEntriesThanTheLimit()
	{
		string path = Path.Combine(directory, "many.odaddin");
		using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
		{
			for (int i = 0; i <= OdAddinPackage.MaxEntries; i++)
				zip.CreateEntry("f" + i);
		}

		Assert.Contains("entries", Assert.Throws<InvalidDataException>(() => OdAddinPackage.Extract(path, Path.Combine(directory, "out"))).Message);
	}

	[Fact]
	public void Extract_RefusesASymbolicLink()
	{
		string path = Path.Combine(directory, "link.odaddin");
		using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
		{
			var link = zip.CreateEntry("lib");
			link.ExternalAttributes = (0xA000 | 0x1FF) << 16;
			using var writer = new StreamWriter(link.Open());
			writer.Write("/etc");
		}

		var ex = Assert.Throws<InvalidDataException>(() => OdAddinPackage.Extract(path, Path.Combine(directory, "out")));

		Assert.Contains("symbolic link", ex.Message);
	}

	[Fact]
	public void Extract_RefusesANonZip()
	{
		string notAZip = Path.Combine(directory, "bad.odaddin");
		File.WriteAllText(notAZip, "this is not a zip");

		Assert.Throws<InvalidDataException>(() => OdAddinPackage.Extract(notAZip, Path.Combine(directory, "out")));
	}

	// ---- source attributes --------------------------------------------------------------------------------

	[Fact]
	public void PatchSourceAttributes_RecordsTheRepositoryAndTag()
	{
		string manifest = Path.Combine(directory, "m.addin");
		File.WriteAllText(manifest, Manifest());

		OdAddinPackage.PatchSourceAttributes(manifest, Release("Owner/My-Addin", "v1.2.3"));

		string text = File.ReadAllText(manifest);
		Assert.Contains("__odRepository=\"Owner/My-Addin\"", text);
		Assert.Contains("__odRelease=\"v1.2.3\"", text);
		Assert.Contains("Vendor.Sample", text);
	}

	[Fact]
	public void PatchSourceAttributes_RemovesAClaimThePackageMadeItself()
	{
		string manifest = Path.Combine(directory, "m.addin");
		File.WriteAllText(manifest, Manifest(extraAttributes: "__odRepository=\"evil/claim\" __odRelease=\"v9.9.9\""));

		OdAddinPackage.PatchSourceAttributes(manifest, null);

		string text = File.ReadAllText(manifest);
		Assert.DoesNotContain("__odRepository", text);
		Assert.DoesNotContain("__odRelease", text);
	}

	[Fact]
	public void PatchSourceAttributes_ReplacesAClaimWithTheRealSource()
	{
		string manifest = Path.Combine(directory, "m.addin");
		File.WriteAllText(manifest, Manifest(extraAttributes: "__odRepository=\"evil/claim\""));

		OdAddinPackage.PatchSourceAttributes(manifest, Release("owner/my-addin"));

		string text = File.ReadAllText(manifest);
		Assert.DoesNotContain("evil/claim", text);
		Assert.Contains("__odRepository=\"owner/my-addin\"", text);
	}

	[Fact]
	public void PatchSourceAttributes_DoesNotResolveExternalEntities()
	{
		string manifest = Path.Combine(directory, "m.addin");
		string secret = Path.Combine(directory, "secret.txt");
		File.WriteAllText(secret, "TOP SECRET");
		File.WriteAllText(manifest, $"<!DOCTYPE AddIn [<!ENTITY x SYSTEM \"{new Uri(secret).AbsoluteUri}\">]>\n<AddIn name=\"&x;\"><Manifest><Identity name=\"A\" version=\"1.0\"/></Manifest></AddIn>");

		Assert.ThrowsAny<Exception>(() => OdAddinPackage.PatchSourceAttributes(manifest, null));
		Assert.DoesNotContain("TOP SECRET", File.ReadAllText(manifest));
	}

	// ---- the install glue (AddInSetup) --------------------------------------------------------------------

	(AddInSetup Setup, FakeSdManagement Management, AddInManagerEvents Events, List<string> Errors) NewSetup()
	{
		var management = new FakeSdManagement { Temp = Path.Combine(directory, "temp-install") };
		var events = new AddInManagerEvents();
		var errors = new List<string>();
		events.AddInOperationError += (_, e) => errors.Add(e.Message);
		return (new AddInSetup(events, null, management), management, events, errors);
	}

	[Fact]
	public void InstallAddIn_FromARelease_UnpacksAndRecordsTheSource()
	{
		var (setup, management, events, errors) = NewSetup();
		var installed = new List<AddIn>();
		events.AddInInstalled += (_, e) => installed.Add(e.AddIn);

		AddIn addIn = setup.InstallAddIn(GoodPackage(), Release("Owner/My-Addin", "v1.2.3"));

		Assert.Empty(errors);
		Assert.NotNull(addIn);
		Assert.Equal(AddInAction.Install, addIn.Action);
		Assert.Same(addIn, Assert.Single(management.AddedToTree));
		string folder = Path.Combine(management.Temp, "Vendor.Sample");
		Assert.Equal(Path.Combine(folder, "Vendor.Sample.addin"), addIn.FileName);
		Assert.True(File.Exists(Path.Combine(folder, "Sample.dll")));
		Assert.Contains("__odRepository=\"Owner/My-Addin\"", File.ReadAllText(addIn.FileName));
		Assert.Equal("Owner/My-Addin", addIn.Properties[OdAddinPackage.RepositoryManifestAttribute]);
		Assert.Same(addIn, Assert.Single(installed));
		var managed = Assert.Single(setup.AddInsWithMarkedForInstallation.Where(m => m.AddIn == addIn));
		Assert.Equal(RepositoryReference.Parse("owner/my-addin"), managed.InstalledFromRepository);
		Assert.Equal("v1.2.3", managed.InstalledFromReleaseTag);
	}

	[Fact]
	public void InstallAddIn_FromALocalFile_RecordsNoSourceEvenIfThePackageClaimsOne()
	{
		var (setup, management, _, errors) = NewSetup();

		AddIn addIn = setup.InstallAddIn(GoodPackage(extraAttributes: "__odRepository=\"evil/claim\" __odRelease=\"v9.9.9\""));

		Assert.Empty(errors);
		Assert.NotNull(addIn);
		Assert.False(addIn.Properties.Contains(OdAddinPackage.RepositoryManifestAttribute));
		Assert.DoesNotContain("evil/claim", File.ReadAllText(addIn.FileName));
		var managed = Assert.Single(setup.AddInsWithMarkedForInstallation.Where(m => m.AddIn == addIn));
		Assert.Null(managed.InstalledFromRepository);
		Assert.Null(managed.InstalledFromReleaseTag);
	}

	[Fact]
	public void InstallAddIn_OverAnInstalledAddInWithTheSameIdentity_IsAnUpdate()
	{
		var (setup, management, _, errors) = NewSetup();
		management.Installed.Add(AddIn.Load(SD.AddInTree, new StringReader(Manifest(version: "1.0.0"))));

		AddIn addIn = setup.InstallAddIn(GoodPackage(), Release());

		Assert.Empty(errors);
		Assert.Equal(AddInAction.Update, addIn.Action);
		Assert.Empty(management.AddedToTree);
		var marked = Assert.Single(setup.AddInsWithMarkedForInstallation, m => m.AddIn == addIn);
		Assert.True(marked.IsUpdate);
		Assert.Equal(new Version(1, 0, 0), marked.OldVersion);
	}

	[Theory]
	[InlineData("..")]
	[InlineData("../escape")]
	[InlineData("a/b")]
	public void InstallAddIn_WithAnIdentityThatIsNotAFolderName_IsRefusedBeforeAnythingIsCreatedOrDeleted(string identity)
	{
		var (setup, management, _, errors) = NewSetup();
		string precious = Path.Combine(directory, "precious.txt");
		File.WriteAllText(precious, "keep me");

		AddIn addIn = setup.InstallAddIn(GoodPackage(identity: identity), Release());

		Assert.Null(addIn);
		Assert.Single(errors);
		Assert.Empty(management.AddedToTree);
		Assert.False(Directory.Exists(management.Temp) && Directory.EnumerateFileSystemEntries(management.Temp).Any());
		Assert.True(File.Exists(precious));
	}

	[Fact]
	public void InstallAddIn_WithAPackageThatCannotBeUnpackedSafely_FailsCleanly()
	{
		var (setup, management, events, errors) = NewSetup();
		string evil = Package("evil.odaddin", ("Vendor.Sample.addin", Manifest()), ("../escaped.txt", "gotcha"));
		var installed = new List<AddIn>();
		events.AddInInstalled += (_, e) => installed.Add(e.AddIn);

		AddIn addIn = setup.InstallAddIn(evil, Release());

		Assert.Null(addIn);
		Assert.Single(errors);
		Assert.Empty(installed);
		Assert.Empty(management.AddedToTree);
		Assert.False(Directory.Exists(Path.Combine(management.Temp, "Vendor.Sample")));
		Assert.False(File.Exists(Path.Combine(management.Temp, "escaped.txt")));
	}

	[Fact]
	public void InstallAddIn_WithAnEntryLargerThanItsDeclaredSize_FailsCleanly()
	{
		var (setup, management, _, errors) = NewSetup();
		string package = Package("lying.odaddin", ("Vendor.Sample.addin", Manifest()), ("Sample.dll", new string('x', 5000)));
		ShrinkDeclaredSize(package, "Sample.dll", 10);

		Assert.Null(setup.InstallAddIn(package, Release()));

		Assert.Single(errors);
		Assert.Empty(management.AddedToTree);
		Assert.False(Directory.Exists(Path.Combine(management.Temp, "Vendor.Sample")));
	}

	[Fact]
	public void InstallAddIn_WithAManifestWhoseExtensionIsUpperCase_Installs()
	{
		var (setup, management, _, errors) = NewSetup();
		string package = Package("upper.odaddin", ("Vendor.Sample.ADDIN", Manifest()), ("Sample.dll", "dll"));

		AddIn addIn = setup.InstallAddIn(package, Release());

		Assert.Empty(errors);
		Assert.NotNull(addIn);
		Assert.Equal(Path.Combine(management.Temp, "Vendor.Sample", "Vendor.Sample.ADDIN"), addIn.FileName);
		Assert.Contains("__odRepository", File.ReadAllText(addIn.FileName));
	}

	[Fact]
	public void InstallAddIn_WithANonPackage_ReportsAnError()
	{
		var (setup, management, _, errors) = NewSetup();
		string notAZip = Path.Combine(directory, "bad.odaddin");
		File.WriteAllText(notAZip, "this is not a zip");

		Assert.Null(setup.InstallAddIn(notAZip, Release()));

		Assert.Single(errors);
		Assert.Empty(management.AddedToTree);
	}

	[Theory]
	[InlineData("Sample.sdaddin")]
	[InlineData("Sample.zip")]
	public void InstallAddIn_NoLongerAcceptsTheOldPackageFormats(string name)
	{
		var (setup, management, _, errors) = NewSetup();
		string file = Path.Combine(directory, name);
		File.Copy(GoodPackage(), file);

		Assert.Null(setup.InstallAddIn(file));

		Assert.Single(errors);
		Assert.Empty(management.AddedToTree);
	}
}
