using ICSharpCode.AddInManager2.Model;
using ICSharpCode.AddInManager2.ViewModel;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using Xunit;
using static OpenDevelop.Base.Tests.AddInTestSupport;

namespace OpenDevelop.Base.Tests;

/// <summary>The AddIn Manager window's view models over the GitHub Releases service.</summary>
[Collection(ProcessWideStateCollection.Name)]
public class AddInManagerViewModelTests : IDisposable
{
	readonly string directory = Path.Combine(Path.GetTempPath(), "od-vm-" + Guid.NewGuid().ToString("N"));
	readonly FakeMessageService messages;

	public AddInManagerViewModelTests()
	{
		Directory.CreateDirectory(directory);
		InitializeServices();
		var (service, fake) = FakeMessageService.Create();
		SD.Services.AddService(typeof(IMessageService), service);
		messages = fake;
	}

	public void Dispose()
	{
		SD.TearDownForUnitTests();
		if (Directory.Exists(directory))
			Directory.Delete(directory, true);
	}

	ReleaseHarness NewHarness(params string[] trusted) => new(directory, trusted);

	static async Task<(ReleaseHarness H, TestServices S)> Refreshed(ReleaseHarness h)
	{
		await h.Service.RefreshAsync(false, default);
		return (h, new TestServices(h));
	}

	// ---- Available ----------------------------------------------------------------------------------------

	[Fact]
	public async Task Available_ListsTheEntriesOfTheTrustedRepositories_ByName()
	{
		var h = NewHarness("owner/zeta", "owner/alpha");
		h.Offer("owner/zeta", Release("owner/zeta", "v2.0.0"));
		h.Offer("owner/alpha", Release("owner/alpha", "v1.0.0"));
		var (_, services) = await Refreshed(h);

		var vm = new AvailableAddInsViewModel(services);
		vm.ReadPackages();

		Assert.Equal(new[] { "Sample", "Sample" }, vm.AddInPackages.Select(p => p.Name));
		Assert.Equal(new[] { "owner/alpha", "owner/zeta" }, vm.AddInPackages.Select(p => p.Id).Order());
		Assert.Equal(new Version(1, 0, 0), vm.AddInPackages.Single(p => p.Id == "owner/alpha").Version);
		Assert.False(vm.HasError);
		Assert.True(vm.IsSearchable);
		Assert.True(vm.AllowRefresh);
		Assert.True(vm.HasFilterForPrereleases);
		Assert.False(vm.IsPaged);
	}

	[Fact]
	public void Available_WithNoTrustedRepository_TellsTheUserWhereToAddOne()
	{
		var h = NewHarness();
		var vm = new AvailableAddInsViewModel(new TestServices(h));

		vm.ReadPackages();

		Assert.Empty(vm.AddInPackages);
		Assert.True(vm.HasError);
		Assert.Contains("NoTrustedRepositories", vm.ErrorMessage);
	}

	[Fact]
	public async Task Available_ShowsWhyARepositoryOffersNothing_AlongsideTheOnesThatDo()
	{
		var h = NewHarness("owner/good", "owner/gone", "owner/limited");
		h.Offer("owner/good", Release("owner/good", "v1.0.0"));
		h.Source.Lists[RepositoryReference.Parse("owner/limited")] = new ReleaseListResult(
			RepositoryReference.Parse("owner/limited"), ReleaseListStatus.RateLimited, null, null, "limit used up", null, DateTimeOffset.UtcNow);
		var (_, services) = await Refreshed(h);

		var vm = new AvailableAddInsViewModel(services);
		vm.ReadPackages();

		Assert.Equal(3, vm.AddInPackages.Count);
		Assert.True(vm.HasError);
		Assert.Contains("owner/gone: not found", vm.ErrorMessage);
		Assert.Contains("owner/limited: limit used up", vm.ErrorMessage);
		Assert.DoesNotContain("owner/good", vm.ErrorMessage);
		var gone = vm.AddInPackages.Single(p => p.Id == "owner/gone");
		Assert.False(gone.IsInstallable);
		Assert.Contains("not found", gone.Summary);
	}

	[Fact]
	public async Task Available_SearchMatchesTheNameOrTheRepository()
	{
		var h = NewHarness("owner/alpha", "other/beta");
		h.Offer("owner/alpha", Release("owner/alpha", "v1.0.0"));
		h.Offer("other/beta", Release("other/beta", "v1.0.0"));
		var (_, services) = await Refreshed(h);
		var vm = new AvailableAddInsViewModel(services);

		vm.SearchTerms = "BETA";
		vm.Search();
		Assert.Equal("other/beta", Assert.Single(vm.AddInPackages).Id);

		vm.SearchTerms = "nothing like it";
		vm.Search();
		Assert.Empty(vm.AddInPackages);

		vm.SearchTerms = "";
		vm.Search();
		Assert.Equal(2, vm.AddInPackages.Count);
	}

	[Fact]
	public async Task Available_ThePrereleaseFilterIsRememberedAndRebuildsTheList()
	{
		var h = NewHarness("owner/my-addin");
		h.Offer("owner/my-addin", Release(tag: "v1.0.0"), Release(tag: "v2.0.0-rc1"));
		var (_, services) = await Refreshed(h);
		var vm = new AvailableAddInsViewModel(services);
		vm.ReadPackages();
		Assert.Equal(new Version(1, 0, 0), vm.AddInPackages.Single().Version);

		vm.ShowPrereleases = true;
		vm.UpdatePrereleaseFilterCommand.Execute(null);

		Assert.True(h.Settings.ShowPrereleases);
		Assert.Equal(new Version(2, 0, 0), vm.AddInPackages.Single().Version);
		Assert.True(new AvailableAddInsViewModel(services).ShowPrereleases, "a new list starts with the remembered setting");
	}

	[Fact]
	public async Task Available_RebuildsWhenTheReleaseListsAreRefreshed()
	{
		var h = NewHarness("owner/my-addin");
		var services = new TestServices(h);
		var vm = new AvailableAddInsViewModel(services);
		vm.ReadPackages();
		Assert.Empty(vm.AddInPackages); // nothing has been read from GitHub yet
		Assert.False(vm.HasError);

		h.Offer("owner/my-addin", Release(tag: "v1.0.0"));
		await h.Service.RefreshAsync(false, default);

		Assert.Equal(new Version(1, 0, 0), Assert.Single(vm.AddInPackages).Version);
	}

	[Fact]
	public void Available_ATrustedRepositoryAddedLaterIsListedAndRefreshedAtOnce()
	{
		var h = NewHarness();
		h.Offer("owner/new", Release("owner/new", "v1.0.0"));
		var vm = new AvailableAddInsViewModel(new TestServices(h));
		vm.ReadPackages();

		Assert.True(h.Service.AddTrustedRepository("owner/new", out var error), error);

		Assert.Equal(("owner/new", false), Assert.Single(h.Source.Requests));
		Assert.Equal(new Version(1, 0, 0), Assert.Single(vm.AddInPackages).Version);
	}

	[Fact]
	public async Task Available_RefreshCommandForcesAReadOfGitHub()
	{
		var h = NewHarness("owner/my-addin");
		h.Offer("owner/my-addin", Release(tag: "v1.0.0"));
		var vm = new AvailableAddInsViewModel(new TestServices(h));

		vm.RefreshCommand.Execute(null);
		await vm.RefreshAsync(false);

		Assert.Contains(("owner/my-addin", true), h.Source.Requests);
		Assert.False(vm.IsReadingPackages);
	}

	[Fact]
	public async Task Available_AfterDisposeItStopsReactingToTheServices()
	{
		var h = NewHarness("owner/my-addin");
		var vm = new AvailableAddInsViewModel(new TestServices(h));
		vm.ReadPackages();
		vm.Dispose();
		h.Offer("owner/my-addin", Release(tag: "v1.0.0"));
		var before = vm.AddInPackages.ToList();

		await h.Service.RefreshAsync(false, default);
		h.Service.RemoveTrustedRepository(RepositoryReference.Parse("owner/my-addin"));

		Assert.True(vm.IsDisposed);
		Assert.Equal(before, vm.AddInPackages.ToList());
		Assert.Single(h.Source.Requests); // the remove did not trigger a refresh either
	}

	// ---- Updates ------------------------------------------------------------------------------------------

	[Fact]
	public async Task Updates_ListsOnlyAddInsWithANewerRelease_AndCountsThemInTheTitle()
	{
		var h = NewHarness("owner/old", "owner/current", "owner/new");
		h.InstallLocally(Manifest(identity: "Vendor.Old", version: "1.0.0", extraAttributes: "__odRepository=\"owner/old\""));
		h.InstallLocally(Manifest(identity: "Vendor.Current", version: "2.0.0", extraAttributes: "__odRepository=\"owner/current\""));
		h.Offer("owner/old", Release("owner/old", "v1.5.0"));
		h.Offer("owner/current", Release("owner/current", "v2.0.0"));
		h.Offer("owner/new", Release("owner/new", "v1.0.0"));
		var (_, services) = await Refreshed(h);

		var vm = new UpdatedAddInsViewModel(services);
		vm.ReadPackages();

		var update = Assert.Single(vm.AddInPackages);
		Assert.Equal("owner/old", update.Id);
		Assert.True(update.IsUpdate);
		Assert.Equal(new Version(1, 5, 0), update.Version);
		Assert.Equal(new Version(1, 0, 0), update.OldVersion);
		Assert.Equal(1, vm.HighlightCount);
		Assert.True(vm.HasHighlightCount);
		Assert.EndsWith(" (1)", vm.TitleWithHighlight);
		Assert.True(vm.HasFilterForPrereleases);
	}

	[Fact]
	public async Task Updates_AnInstalledUpdateLeavesTheListAndTheCount()
	{
		var h = NewHarness("owner/my-addin");
		h.InstallLocally(Manifest(version: "1.0.0", extraAttributes: "__odRepository=\"owner/my-addin\""));
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		h.DeliverPackage(directory, Manifest(version: "1.2.3"));
		var (_, services) = await Refreshed(h);
		var vm = new UpdatedAddInsViewModel(services);
		vm.ReadPackages();
		Assert.Equal(1, vm.HighlightCount);

		vm.AddInPackages.Single().UpdatePackageCommand.Execute(null);

		Assert.Empty(h.Errors);
		Assert.Empty(vm.AddInPackages);
		Assert.Equal(0, vm.HighlightCount);
	}

	// ---- one entry ----------------------------------------------------------------------------------------

	GitHubReleaseViewModel Entry(ReleaseHarness h) =>
		(GitHubReleaseViewModel)new AvailableAddInsViewModel(new TestServices(h)).Also(vm => vm.ReadPackages()).AddInPackages.Single();

	[Fact]
	public async Task Entry_NotInstalled_CanBeInstalledButNotRemovedOrUpdated()
	{
		var h = NewHarness("owner/my-addin");
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		await h.Service.RefreshAsync(false, default);

		var entry = Entry(h);

		Assert.True(entry.IsInstallable);
		Assert.False(entry.IsInstalled);
		Assert.False(entry.IsUpdate);
		Assert.True(entry.AddPackageCommand.CanExecute(null));
		Assert.False(entry.UpdatePackageCommand.CanExecute(null));
		Assert.False(entry.RemovePackageCommand.CanExecute(null));
		Assert.False(entry.IsOffline);
		Assert.True(entry.HasProjectUrl);
		Assert.Equal("https://github.com/owner/my-addin", entry.ProjectUrl.ToString());
		Assert.Equal(new[] { "owner" }, entry.Authors);
		Assert.False(entry.HasDependencies);
	}

	[Fact]
	public async Task Entry_InstalledAtTheSameVersion_CanOnlyBeRemoved()
	{
		var h = NewHarness("owner/my-addin");
		h.InstallLocally(Manifest(version: "1.2.3", extraAttributes: "__odRepository=\"owner/my-addin\""));
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		await h.Service.RefreshAsync(false, default);

		var entry = Entry(h);

		Assert.True(entry.IsInstalled);
		Assert.False(entry.IsUpdate);
		Assert.False(entry.AddPackageCommand.CanExecute(null));
		Assert.False(entry.UpdatePackageCommand.CanExecute(null));
		Assert.True(entry.RemovePackageCommand.CanExecute(null));
	}

	[Fact]
	public async Task Entry_InstalledAtAnOlderVersion_OffersTheUpdateWithBothVersions()
	{
		var h = NewHarness("owner/my-addin");
		h.InstallLocally(Manifest(version: "1.0.0", extraAttributes: "__odRepository=\"owner/my-addin\""));
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		await h.Service.RefreshAsync(false, default);

		var entry = Entry(h);

		Assert.True(entry.IsUpdate);
		Assert.True(entry.UpdatePackageCommand.CanExecute(null));
		Assert.False(entry.AddPackageCommand.CanExecute(null));
		Assert.False(entry.RemovePackageCommand.CanExecute(null));
		Assert.True(entry.ShowSplittedVersions);
		Assert.True(entry.HasOldVersion);
		Assert.True(entry.HasNewVersion);
		Assert.Equal(new Version(1, 0, 0), entry.OldVersion);
		Assert.Equal(new Version(1, 2, 3), entry.Version);
	}

	[Fact]
	public async Task Entry_MarkedForRemoval_CanBeRestored()
	{
		var h = NewHarness("owner/my-addin");
		var installed = h.InstallLocally(Manifest(version: "1.2.3", extraAttributes: "__odRepository=\"owner/my-addin\""));
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		await h.Service.RefreshAsync(false, default);
		var entry = Entry(h);

		installed.Action = AddInAction.Uninstall;
		entry.UpdateInstallationState();

		Assert.True(entry.IsRemoved);
		Assert.True(entry.CancelUninstallationCommand.CanExecute(null));
		Assert.False(entry.RemovePackageCommand.CanExecute(null));
	}

	[Fact]
	public async Task Entry_Install_RunsTheServiceAndShowsThePendingInstallation()
	{
		var h = NewHarness("owner/my-addin");
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		h.DeliverPackage(directory, Manifest(version: "1.2.3"));
		await h.Service.RefreshAsync(false, default);
		var entry = Entry(h);

		entry.AddPackageCommand.Execute(null);

		Assert.Empty(h.Errors);
		Assert.Single(h.Management.AddedToTree);
		Assert.True(entry.IsAdded);
		Assert.True(entry.CancelInstallationCommand.CanExecute(null));
		Assert.False(entry.AddPackageCommand.CanExecute(null));
		Assert.True(entry.IsInstallable, "the installing flag is cleared when the install is over");
	}

	[Fact]
	public async Task Entry_Install_ReportsAFailureThroughTheErrorEvent()
	{
		var h = NewHarness("owner/my-addin");
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		h.Source.OnDownload = (_, _) => throw new InvalidDataException("digest mismatch");
		await h.Service.RefreshAsync(false, default);
		var entry = Entry(h);

		entry.AddPackageCommand.Execute(null);

		Assert.Contains("digest mismatch", Assert.Single(h.Errors));
		Assert.False(entry.IsAdded);
		Assert.True(entry.IsInstallable);
	}

	[Fact]
	public async Task Entry_Install_AnUnexpectedExceptionIsReportedNotThrown()
	{
		var h = NewHarness("owner/my-addin");
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		h.Source.OnDownload = (_, _) => throw new InvalidOperationException("surprise");
		await h.Service.RefreshAsync(false, default);
		var entry = Entry(h);

		entry.AddPackageCommand.Execute(null);

		Assert.Contains("surprise", Assert.Single(h.Errors));
		Assert.True(entry.IsInstallable);
	}

	[Fact]
	public async Task Entry_Install_AnUnexpectedExceptionTextCannotExpandProperties()
	{
		var h = NewHarness("owner/my-addin");
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		h.Source.OnDownload = (_, _) => throw new InvalidOperationException("${res:Secret} ${env:HOME}");
		await h.Service.RefreshAsync(false, default);
		var entry = Entry(h);

		entry.AddPackageCommand.Execute(null);

		string message = Assert.Single(h.Errors);
		Assert.DoesNotContain("${", message);
		Assert.Contains("res:Secret", message);
	}

	[Fact]
	public async Task Entry_TwoListsForTheSameRepositoryCannotInstallItTwice()
	{
		var h = NewHarness("owner/my-addin");
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		h.DeliverPackage(directory, Manifest(version: "1.2.3"));
		var deliver = h.Source.OnDownload!;
		var gate = new TaskCompletionSource();
		h.Source.OnDownload = async (r, d) => { await gate.Task; await deliver(r, d); };
		await h.Service.RefreshAsync(false, default);
		var services = new TestServices(h);
		var available = new AvailableAddInsViewModel(services);
		available.ReadPackages();
		var updates = new AvailableAddInsViewModel(services); // a second list showing the same repository
		updates.ReadPackages();

		available.AddInPackages.Single().AddPackageCommand.Execute(null);
		updates.AddInPackages.Single().AddPackageCommand.Execute(null);
		gate.SetResult();
		await Task.Delay(50);

		Assert.Contains("already being installed", Assert.Single(h.Errors));
		Assert.Single(h.Management.AddedToTree);
	}

	[Fact]
	public async Task Entry_OnlyAnInstalledEntryCanBeUninstalled()
	{
		var h = NewHarness("owner/my-addin");
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		await h.Service.RefreshAsync(false, default);
		Assert.False(Entry(h).IsUninstallable);

		h.InstallLocally(Manifest(version: "1.2.3", extraAttributes: "__odRepository=\"owner/my-addin\""));

		Assert.True(Entry(h).IsUninstallable);
	}

	// ---- Installed ----------------------------------------------------------------------------------------

	[Fact]
	public void Installed_ListsTheAddIns_HidesTheHiddenOnes_AndMarksTheOnesFromGitHub()
	{
		var h = NewHarness();
		h.InstallLocally(Manifest(identity: "Vendor.Zed", version: "1.0.0").Replace("name=\"Sample\"", "name=\"Zed\""));
		h.InstallLocally(Manifest(identity: "Vendor.Alpha", version: "1.0.0", extraAttributes: "__odRepository=\"owner/alpha\"").Replace("name=\"Sample\"", "name=\"Alpha\""));
		h.InstallLocally(Manifest(identity: "Vendor.Secret", version: "1.0.0", extraAttributes: "addInManagerHidden=\"true\"").Replace("name=\"Sample\"", "name=\"Secret\""));
		var vm = new InstalledAddInsViewModel(new TestServices(h));

		vm.ReadPackages();

		var names = vm.AddInPackages.Select(p => p.Name).ToList();
		Assert.Equal(new[] { "Alpha", "Zed" }, names.Where(n => n is "Alpha" or "Zed" or "Secret"));
		Assert.DoesNotContain("Secret", names);
		Assert.True(vm.AddInPackages.Single(p => p.Name == "Alpha").HasOnlineSource);
		Assert.False(vm.AddInPackages.Single(p => p.Name == "Zed").HasOnlineSource);
		Assert.True(vm.AllowInstallFromArchive);
		Assert.True(vm.HasFilterForPreinstalled);
		Assert.False(vm.AllowRefresh);
	}

	[Fact]
	public void Installed_SearchMatchesNameIdentityAndDescription()
	{
		var h = NewHarness();
		h.InstallLocally(Manifest(identity: "Vendor.Zed", version: "1.0.0").Replace("name=\"Sample\"", "name=\"Zed\"").Replace("description=\"x\"", "description=\"draws pictures\""));
		h.InstallLocally(Manifest(identity: "Vendor.Alpha", version: "1.0.0").Replace("name=\"Sample\"", "name=\"Alpha\""));
		var vm = new InstalledAddInsViewModel(new TestServices(h));

		vm.SearchTerms = "pictures";
		vm.Search();
		Assert.Equal("Zed", Assert.Single(vm.AddInPackages).Name);

		vm.SearchTerms = "vendor.alpha";
		vm.Search();
		Assert.Equal("Alpha", Assert.Single(vm.AddInPackages).Name);
	}

	[Fact]
	public void Installed_TheListFollowsInstallationsMadeAfterwards()
	{
		var h = NewHarness();
		var vm = new InstalledAddInsViewModel(new TestServices(h));
		vm.ReadPackages();
		int before = vm.AddInPackages.Count;

		h.InstallLocally(Manifest(identity: "Vendor.Late", version: "1.0.0").Replace("name=\"Sample\"", "name=\"Late\""));
		h.Events.OnAddInInstalled(new AddInInstallationEventArgs(h.Management.Installed.Last()));

		Assert.Equal(before + 1, vm.AddInPackages.Count);
		Assert.Contains(vm.AddInPackages, p => p.Name == "Late");
	}

	// ---- trusted repositories panel -----------------------------------------------------------------------

	[Fact]
	public void TrustedRepositories_LoadShowsWhatIsTrusted()
	{
		var h = NewHarness("owner/one", "other/two");
		var vm = new TrustedRepositoriesViewModel(new TestServices(h));

		vm.Load();

		Assert.Equal(new[] { "owner/one", "other/two" }, vm.Repositories);
	}

	[Fact]
	public void TrustedRepositories_AddAsksFirstAndOnlyAddsOnYes()
	{
		var h = NewHarness();
		var vm = new TrustedRepositoriesViewModel(new TestServices(h));
		vm.Load();

		messages.Answer = false;
		vm.NewRepository = "owner/my-addin";
		vm.AddCommand.Execute(null);
		Assert.Empty(vm.Repositories);
		Assert.Contains(messages.Calls, c => c.StartsWith("AskQuestion:") && c.Contains("owner/my-addin") && c.Contains("all your rights"));

		messages.Answer = true;
		vm.AddCommand.Execute(null);
		Assert.Equal(new[] { "owner/my-addin" }, vm.Repositories);
		Assert.Equal(string.Empty, vm.NewRepository);
		Assert.Empty(h.Settings.TrustedRepositories); // nothing is stored until the dialog is accepted
	}

	[Fact]
	public void TrustedRepositories_AddRefusesWhatIsNotARepositoryOrIsAlreadyListed_WithoutAsking()
	{
		var h = NewHarness("owner/one");
		var vm = new TrustedRepositoriesViewModel(new TestServices(h));
		vm.Load();

		vm.NewRepository = "not a repo";
		vm.AddCommand.Execute(null);
		Assert.True(vm.HasMessage);
		Assert.Single(vm.Repositories);

		vm.NewRepository = "OWNER/ONE";
		vm.AddCommand.Execute(null);
		Assert.Contains("owner/one", vm.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Single(vm.Repositories);
		Assert.DoesNotContain(messages.Calls, c => c.StartsWith("AskQuestion"));
	}

	[Fact]
	public void TrustedRepositories_SaveAppliesTheDifferenceToTheService()
	{
		var h = NewHarness("owner/keep", "owner/drop");
		var vm = new TrustedRepositoriesViewModel(new TestServices(h));
		vm.Load();

		vm.SelectedRepository = "owner/drop";
		vm.RemoveCommand.Execute(null);
		vm.NewRepository = "owner/fresh";
		vm.AddCommand.Execute(null);
		vm.Save();

		Assert.Equal(new[] { "owner/keep", "owner/fresh" }, h.Settings.TrustedRepositories);
		Assert.Equal(new[] { "owner/keep", "owner/fresh" }, h.Service.TrustedRepositories.Select(r => r.FullName));
	}

	[Fact]
	public void TrustedRepositories_CommandsAreEnabledOnlyWhenTheyCanDoSomething()
	{
		var h = NewHarness("owner/one");
		var vm = new TrustedRepositoriesViewModel(new TestServices(h));
		vm.Load();

		Assert.False(vm.AddCommand.CanExecute(null));
		Assert.False(vm.RemoveCommand.CanExecute(null));
		vm.NewRepository = "x/y";
		vm.SelectedRepository = "owner/one";
		Assert.True(vm.AddCommand.CanExecute(null));
		Assert.True(vm.RemoveCommand.CanExecute(null));
	}

	// ---- the window ---------------------------------------------------------------------------------------

	[Fact]
	public async Task Window_ShowsWhatIsKnownAtOnce_ThenReadsGitHubFromTheCache()
	{
		var h = NewHarness("owner/my-addin");
		h.Offer("owner/my-addin", Release(tag: "v1.0.0"));
		var opened = 0;
		h.Events.AddInManagerViewOpened += (_, _) => opened++;

		var vm = new AddInManagerViewModel(new TestServices(h));

		Assert.Equal(1, opened);
		Assert.Equal(("owner/my-addin", false), Assert.Single(h.Source.Requests));
		Assert.Equal(3, vm.ViewModels.Count);
		Assert.True(vm.InstalledAddInsViewModel.IsExpandedInView);
		Assert.Equal(new Version(1, 0, 0), Assert.Single(vm.AvailableAddInsViewModel.AddInPackages).Version);
		await Task.CompletedTask;
	}

	[Fact]
	public void Window_ForwardsAnOperationErrorToTheUser_AndClearsItWhenANewOperationStarts()
	{
		var h = NewHarness();
		var vm = new AddInManagerViewModel(new TestServices(h));

		h.Events.OnAddInOperationError(new AddInOperationErrorEventArgs("it broke"));

		Assert.Contains(messages.Calls, c => c.StartsWith("ShowWarning:") && c.Contains("it broke"));
	}

	[Fact]
	public void Window_OnlyOneListIsExpandedAtATime()
	{
		var h = NewHarness();
		var vm = new AddInManagerViewModel(new TestServices(h));

		vm.UpdatedAddInsViewModel.IsExpandedInView = true;

		Assert.False(vm.InstalledAddInsViewModel.IsExpandedInView);
		Assert.True(vm.UpdatedAddInsViewModel.IsExpandedInView);
		Assert.False(vm.AvailableAddInsViewModel.IsExpandedInView);
	}

	[Fact]
	public void Window_DisposeDisposesTheListsAndStopsListening()
	{
		var h = NewHarness();
		var vm = new AddInManagerViewModel(new TestServices(h));

		vm.Dispose();
		messages.Calls.Clear();
		h.Events.OnAddInOperationError(new AddInOperationErrorEventArgs("after dispose"));

		Assert.All(vm.ViewModels, v => Assert.True(v.IsDisposed));
		Assert.Empty(messages.Calls);
	}
}

static class FluentExtensions
{
	public static T Also<T>(this T value, Action<T> action)
	{
		action(value);
		return value;
	}
}
