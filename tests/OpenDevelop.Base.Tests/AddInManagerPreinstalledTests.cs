using ICSharpCode.AddInManager2.ViewModel;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using Xunit;
using static OpenDevelop.Base.Tests.AddInTestSupport;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The Installed list's shipped-versus-user classification. The manifest's location decides: an add-in that
/// ships inside the application is preinstalled and cannot be uninstalled, even when its manifest forgets to
/// declare <c>addInManagerHidden="preinstalled"</c>; one in the user add-in directory or in the package
/// staging directory is user-installed and removable.
/// </summary>
public class AddInManagerPreinstalledTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), "od-preinstalled-" + Guid.NewGuid().ToString("N"));
	readonly List<string> addedToApplicationRoot = new();

	public AddInManagerPreinstalledTests()
	{
		Directory.CreateDirectory(root);
		InitializeServices();
	}

	public void Dispose()
	{
		SD.TearDownForUnitTests();
		Delete(root);
		foreach (string directory in addedToApplicationRoot)
			Delete(directory);
	}

	[Fact]
	public void Preinstalled_AShippedManifestThatDoesNotDeclareItself_IsStillPreinstalled()
	{
		var h = new ReleaseHarness(root);
		h.Management.UserInstallDirectory = Path.Combine(root, "user");
		// The application root is this test assembly's output directory, which is where a shipped manifest lives.
		string shippedPath = WriteManifest(Path.Combine(FileUtility.ApplicationRootPath, "AddIns-" + Guid.NewGuid().ToString("N")), "Vendor.Shipped");
		AddIn shipped = InstallFromFile(h, shippedPath);

		Assert.True(h.Setup.IsAddInPreinstalled(shipped));
		Assert.True(AddIn.IsManifestPreinstalled(shipped.FileName, null, null));
		Assert.True(AddIn.IsManifestPreinstalled(shipped.FileName, Path.Combine(root, "user"), null));
	}

	[Fact]
	public void Preinstalled_AManifestInAUserLocation_IsRemovable_EvenUnderTheApplicationRoot()
	{
		var h = new ReleaseHarness(root);
		// A -configdir: under the application root puts both user locations there too; they must still win.
		string config = Path.Combine(FileUtility.ApplicationRootPath, "config-" + Guid.NewGuid().ToString("N"));
		addedToApplicationRoot.Add(config);
		string userDirectory = Path.Combine(config, "AddIns");
		string tempDirectory = Path.Combine(config, "AddInInstallTemp");
		h.Management.UserInstallDirectory = userDirectory;
		h.Management.Temp = tempDirectory;
		string userPath = WriteManifest(userDirectory, "Vendor.User");
		AddIn user = InstallFromFile(h, userPath);
		AddIn staged = InstallFromFile(h, WriteManifest(tempDirectory, "Vendor.Staged"));
		string externalDirectory = Path.Combine(root, "elsewhere");
		AddIn external = InstallFromFile(h, WriteManifest(externalDirectory, "Vendor.External"));

		Assert.False(h.Setup.IsAddInPreinstalled(user));
		Assert.False(AddIn.IsManifestPreinstalled(user.FileName, userDirectory, tempDirectory));
		Assert.False(h.Setup.IsAddInPreinstalled(staged));
		Assert.False(AddIn.IsManifestPreinstalled(staged.FileName, userDirectory, tempDirectory));
		Assert.False(h.Setup.IsAddInPreinstalled(external));
		Assert.False(h.Setup.IsAddInPreinstalled(null));
	}

	[Fact]
	public void Installed_TheListShowsPreinstalledAddInsWithoutUninstall_AndUserAddInsWithIt()
	{
		var h = new ReleaseHarness(root);
		h.Management.UserInstallDirectory = Path.Combine(root, "user");
		InstallFromFile(h, WriteManifest(Path.Combine(FileUtility.ApplicationRootPath, "AddIns-" + Guid.NewGuid().ToString("N")), "Vendor.Shipped"));
		InstallFromFile(h, WriteManifest(h.Management.UserInstallDirectory, "Vendor.User"));
		var vm = new InstalledAddInsViewModel(new TestServices(h));
		vm.ShowPreinstalledAddIns = true;
		vm.UpdatePreinstalledFilterCommand.Execute(null);

		var shipped = vm.AddInPackages.Single(p => p.Id == "Vendor.Shipped");
		Assert.True(shipped.IsPreinstalled);
		Assert.False(shipped.IsUninstallable);
		Assert.False(shipped.RemovePackageCommand.CanExecute(null));

		var user = vm.AddInPackages.Single(p => p.Id == "Vendor.User");
		Assert.False(user.IsPreinstalled);
		Assert.True(user.IsUninstallable);
		Assert.True(user.RemovePackageCommand.CanExecute(null));

		// With the filter off, the Installed list keeps the user add-in and drops the application's own, as for
		// every other shipped add-in.
		var filtered = new InstalledAddInsViewModel(new TestServices(h));
		filtered.ShowPreinstalledAddIns = false;
		filtered.UpdatePreinstalledFilterCommand.Execute(null);
		Assert.DoesNotContain(filtered.AddInPackages, p => p.Id == "Vendor.Shipped");
		Assert.Contains(filtered.AddInPackages, p => p.Id == "Vendor.User");
	}

	AddIn InstallFromFile(ReleaseHarness harness, string manifestFile)
	{
		AddIn addIn = harness.Management.Load(manifestFile);
		harness.Management.Installed.Add(addIn);
		return addIn;
	}

	string WriteManifest(string directory, string identity)
	{
		Directory.CreateDirectory(directory);
		string file = Path.Combine(directory, identity + ".addin");
		File.WriteAllText(file, Manifest(identity: identity));
		if (FileUtility.IsBaseDirectory(FileUtility.ApplicationRootPath, file))
			addedToApplicationRoot.Add(directory);
		return file;
	}

	static void Delete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, true);
		}
		catch (IOException)
		{
		}
	}
}
