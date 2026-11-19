using ICSharpCode.AddInManager2.Model;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using Xunit;
using static OpenDevelop.Base.Tests.AddInTestSupport;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The online side of the AddIn Manager: trusted repositories, what they offer, which installed add-ins
/// have an update, and the download + verify + compatibility check + install of a release.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public class AddInReleaseServiceTests : IDisposable
{
	readonly string directory = Path.Combine(Path.GetTempPath(), "od-release-service-" + Guid.NewGuid().ToString("N"));

	public AddInReleaseServiceTests()
	{
		Directory.CreateDirectory(directory);
		InitializeServices();
	}

	public void Dispose()
	{
		SD.TearDownForUnitTests();
		if (Directory.Exists(directory))
			Directory.Delete(directory, true);
	}

	ReleaseHarness NewHarness(params string[] trusted) => new(directory, trusted);

	[Fact]
	public void InstallAddIn_WhenTheInstanceHasNoUserAddInDirectories_ReportsThatInsteadOfFailingOnANullPath()
	{
		var h = NewHarness();
		h.Management.Temp = null!; // an instance started with -configdir: has no install directories
		string package = Package(directory, "x.odaddin", ("Vendor.Sample.addin", Manifest()), ("Sample.dll", "dll"));

		Assert.Null(h.Setup.InstallAddIn(package, Release()));

		Assert.Equal(new[] { "AddInManager2.UserAddInsDisabled" }, h.Errors);
	}

	[Fact]
	public void InstallAddIn_ALooseManifestStillInstallsAsExternal_WithoutUserAddInDirectories()
	{
		var h = NewHarness();
		h.Management.Temp = null!;
		string manifest = Path.Combine(directory, "Loose.addin");
		File.WriteAllText(manifest, Manifest(identity: "Vendor.Loose"));

		AddIn installed = h.Setup.InstallAddIn(manifest, null);

		Assert.Empty(h.Errors);
		Assert.NotNull(installed);
	}

	// ---- trusted repositories -----------------------------------------------------------------------------

	[Fact]
	public void AddTrustedRepository_StoresItInTheSettingsAndRaisesTheEvent()
	{
		var h = NewHarness();
		int raised = 0;
		h.Service.TrustedRepositoriesChanged += (_, _) => raised++;

		Assert.True(h.Service.AddTrustedRepository("Owner/My-Addin", out var error), error);
		Assert.True(h.Service.AddTrustedRepository("https://github.com/other/thing.git", out error), error);

		Assert.Equal(new[] { "Owner/My-Addin", "other/thing" }, h.Settings.TrustedRepositories);
		Assert.Equal(new[] { "Owner/My-Addin", "other/thing" }, h.Service.TrustedRepositories.Select(r => r.FullName));
		Assert.Equal(2, raised);
	}

	[Fact]
	public void AddTrustedRepository_RefusesAnInvalidOrDuplicateRepositoryWithoutChangingAnything()
	{
		var h = NewHarness("owner/my-addin");
		int raised = 0;
		h.Service.TrustedRepositoriesChanged += (_, _) => raised++;

		Assert.False(h.Service.AddTrustedRepository("not a repo", out var invalid));
		Assert.False(h.Service.AddTrustedRepository("OWNER/MY-ADDIN", out var duplicate));

		Assert.False(string.IsNullOrEmpty(invalid));
		Assert.Contains("already", duplicate);
		Assert.Equal(new[] { "owner/my-addin" }, h.Settings.TrustedRepositories);
		Assert.Equal(0, raised);
	}

	[Fact]
	public void TrustedRepositories_DropsInvalidAndDuplicateStoredEntries()
	{
		var h = NewHarness("owner/my-addin", "garbage", "../etc/passwd", "OWNER/my-addin", "other/thing", "");

		Assert.Equal(new[] { "owner/my-addin", "other/thing" }, h.Service.TrustedRepositories.Select(r => r.FullName));
	}

	[Fact]
	public void RemoveTrustedRepository_RemovesItOnce()
	{
		var h = NewHarness("owner/my-addin", "other/thing");
		int raised = 0;
		h.Service.TrustedRepositoriesChanged += (_, _) => raised++;

		h.Service.RemoveTrustedRepository(RepositoryReference.Parse("OWNER/my-addin"));
		h.Service.RemoveTrustedRepository(RepositoryReference.Parse("owner/my-addin"));
		h.Service.RemoveTrustedRepository(null!);

		Assert.Equal(new[] { "other/thing" }, h.Settings.TrustedRepositories);
		Assert.Equal(1, raised);
	}

	// ---- refreshing and listing ---------------------------------------------------------------------------

	[Fact]
	public async Task RefreshAsync_AsksEachTrustedRepositoryOnceInOrder_AndRaisesTheEvent()
	{
		var h = NewHarness("owner/one", "owner/two");
		h.Offer("owner/one", Release("owner/one", "v1.0.0"));
		h.Offer("owner/two", Release("owner/two", "v2.0.0"));
		int raised = 0;
		h.Service.ReleasesRefreshed += (_, _) => raised++;

		await h.Service.RefreshAsync(false, default);
		await h.Service.RefreshAsync(true, default);

		Assert.Equal(new[] { ("owner/one", false), ("owner/two", false), ("owner/one", true), ("owner/two", true) }, h.Source.Requests);
		Assert.Equal(2, h.Service.LastResults.Count);
		Assert.Equal(2, raised);
	}

	[Fact]
	public async Task RefreshAsync_WithNothingTrusted_MakesNoRequest()
	{
		var h = NewHarness();

		await h.Service.RefreshAsync(false, default);

		Assert.Empty(h.Source.Requests);
		Assert.Empty(h.Service.GetEntries());
	}

	[Fact]
	public async Task RefreshAsync_PropagatesCancellationAndKeepsThePreviousResults()
	{
		var h = NewHarness("owner/one");
		h.Offer("owner/one", Release("owner/one", "v1.0.0"));
		await h.Service.RefreshAsync(false, default);
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.RefreshAsync(true, cts.Token));

		Assert.Single(h.Service.LastResults);
	}

	[Fact]
	public async Task GetEntries_OffersTheNewestStableRelease_AndPrereleasesOnlyOnRequest()
	{
		var h = NewHarness("owner/my-addin");
		h.Offer("owner/my-addin", Release(tag: "v1.0.0"), Release(tag: "v1.10.0"), Release(tag: "v2.0.0-rc1"));
		await h.Service.RefreshAsync(false, default);

		var stable = Assert.Single(h.Service.GetEntries());
		h.Settings.ShowPrereleases = true;
		var withPrereleases = Assert.Single(h.Service.GetEntries());

		Assert.Equal("v1.10.0", stable.Latest!.Tag);
		Assert.Equal("v2.0.0-rc1", withPrereleases.Latest!.Tag);
		Assert.False(stable.IsInstalled);
		Assert.False(stable.HasUpdate);
		Assert.Equal("Sample", stable.Name);
	}

	[Fact]
	public async Task GetEntries_KeepsARepositoryWhoseListFailed_WithTheReason()
	{
		var h = NewHarness("owner/gone");
		await h.Service.RefreshAsync(false, default);

		var entry = Assert.Single(h.Service.GetEntries());

		Assert.Null(entry.Latest);
		Assert.Equal(ReleaseListStatus.Failed, entry.List.Status);
		Assert.Equal("not found", entry.List.Message);
		Assert.Equal("gone", entry.Name);
	}

	[Fact]
	public async Task GetEntries_LeavesOutARepositoryRemovedSinceTheRefresh()
	{
		var h = NewHarness("owner/one", "owner/two");
		h.Offer("owner/one", Release("owner/one", "v1.0.0"));
		h.Offer("owner/two", Release("owner/two", "v1.0.0"));
		await h.Service.RefreshAsync(false, default);

		h.Service.RemoveTrustedRepository(RepositoryReference.Parse("owner/one"));

		Assert.Equal("owner/two", Assert.Single(h.Service.GetEntries()).Repository.FullName);
	}

	// ---- installed state and updates ----------------------------------------------------------------------

	[Theory]
	[InlineData("1.0.0", true)]
	[InlineData("1.9.0", true)]
	[InlineData("1.10.0", false)]
	[InlineData("1.10", false)]
	[InlineData("2.0.0", false)]
	public async Task GetUpdates_OffersAnUpdateOnlyForANewerRelease(string installedVersion, bool expected)
	{
		var h = NewHarness("owner/my-addin");
		h.InstallLocally(Manifest(version: installedVersion, extraAttributes: "__odRepository=\"owner/my-addin\""));
		h.Offer("owner/my-addin", Release(tag: "v1.0.0"), Release(tag: "v1.10.0"));
		await h.Service.RefreshAsync(false, default);

		var entry = Assert.Single(h.Service.GetEntries());

		Assert.True(entry.IsInstalled);
		Assert.Equal(expected, entry.HasUpdate);
		Assert.Equal(expected ? 1 : 0, h.Service.GetUpdates().Count);
	}

	[Fact]
	public async Task GetUpdates_IgnoresAPrereleaseUnlessAsked()
	{
		var h = NewHarness("owner/my-addin");
		h.InstallLocally(Manifest(version: "1.0.0", extraAttributes: "__odRepository=\"owner/my-addin\""));
		h.Offer("owner/my-addin", Release(tag: "v1.0.0"), Release(tag: "v2.0.0-beta1"));
		await h.Service.RefreshAsync(false, default);

		Assert.Empty(h.Service.GetUpdates());
		h.Settings.ShowPrereleases = true;
		Assert.Equal("v2.0.0-beta1", Assert.Single(h.Service.GetUpdates()).Latest!.Tag);
	}

	[Fact]
	public async Task GetEntries_MatchesAnInstalledAddInByTheRepositoryItCameFrom_NotByName()
	{
		var h = NewHarness("owner/my-addin");
		h.InstallLocally(Manifest(version: "1.0.0")); // same identity, installed from a file: no recorded source
		h.InstallLocally(Manifest(identity: "Vendor.Other", version: "1.0.0", extraAttributes: "__odRepository=\"someone/else\""));
		h.Offer("owner/my-addin", Release(tag: "v1.10.0"));
		await h.Service.RefreshAsync(false, default);

		var entry = Assert.Single(h.Service.GetEntries());

		Assert.False(entry.IsInstalled);
		Assert.False(entry.HasUpdate);
	}

	[Fact]
	public async Task GetEntries_AnUpdatePendingRestartIsNotOfferedAgain()
	{
		var h = NewHarness("owner/my-addin");
		h.InstallLocally(Manifest(version: "1.0.0", extraAttributes: "__odRepository=\"owner/my-addin\""));
		h.Offer("owner/my-addin", Release(tag: "v1.2.3"));
		h.DeliverPackage(directory, Manifest(version: "1.2.3"));
		await h.Service.RefreshAsync(false, default);
		Assert.Single(h.Service.GetUpdates());

		await h.Service.InstallAsync(h.Service.GetUpdates()[0].Latest!, default);

		Assert.Empty(h.Errors);
		Assert.Empty(h.Service.GetUpdates());
		Assert.True(Assert.Single(h.Service.GetEntries()).IsInstalled);
	}

	// ---- installing ---------------------------------------------------------------------------------------

	[Fact]
	public async Task InstallAsync_DownloadsVerifiesAndInstallsTheRelease_ThenCleansUp()
	{
		var h = NewHarness("owner/my-addin");
		h.DeliverPackage(directory, Manifest(version: "1.2.3"));

		AddIn addIn = await h.Service.InstallAsync(Release("Owner/My-Addin", "v1.2.3"), default);

		Assert.Empty(h.Errors);
		Assert.NotNull(addIn);
		Assert.Equal(1, h.OperationsStarted);
		Assert.Equal("Owner/My-Addin", addIn.Properties[OdAddinPackage.RepositoryManifestAttribute]);
		Assert.Equal("v1.2.3", addIn.Properties[OdAddinPackage.ReleaseManifestAttribute]);
		Assert.True(File.Exists(Path.Combine(h.Management.Temp, "Vendor.Sample", "Sample.dll")));
		string downloaded = Assert.Single(h.Source.DownloadedTo);
		Assert.Equal("Sample.odaddin", Path.GetFileName(downloaded));
		Assert.False(Directory.Exists(Path.GetDirectoryName(downloaded)), "the work folder must be deleted");
	}

	[Fact]
	public async Task InstallAsync_RefusesAPackageWhoseVersionDoesNotMatchItsTag()
	{
		var h = NewHarness();
		h.DeliverPackage(directory, Manifest(version: "1.2.4"));

		AddIn addIn = await h.Service.InstallAsync(Release(tag: "v1.2.3"), default);

		Assert.Null(addIn);
		Assert.Contains("does not match its release tag", Assert.Single(h.Errors));
		Assert.Empty(h.Management.AddedToTree);
		Assert.False(Directory.Exists(Path.GetDirectoryName(h.Source.DownloadedTo[0])));
	}

	[Theory]
	[InlineData("1.0.0", "v1.0.0-rc.1")] // the final package under a pre-release tag
	[InlineData("1.0.0-rc.1", "v1.0.0")] // a pre-release package under the final tag
	[InlineData("1.0.0-rc.2", "v1.0.0-rc.1")]
	[InlineData("1.0", "v1.0.0")] // not a Semantic Version at all
	public async Task InstallAsync_RefusesAPackageWhoseSemanticVersionIsNotTheTags(string packageVersion, string tag)
	{
		var h = NewHarness();
		h.DeliverPackage(directory, Manifest(version: packageVersion));

		AddIn addIn = await h.Service.InstallAsync(Release(tag: tag), default);

		Assert.Null(addIn);
		Assert.Contains("does not match its release tag", Assert.Single(h.Errors));
		Assert.Empty(h.Management.AddedToTree);
	}

	[Fact]
	public async Task InstallAsync_AcceptsAPreReleasePackageUnderItsPreReleaseTag()
	{
		var h = NewHarness();
		h.DeliverPackage(directory, Manifest(version: "1.0.0-rc.1"));

		AddIn addIn = await h.Service.InstallAsync(Release(tag: "v1.0.0-rc.1"), default);

		Assert.NotNull(addIn);
		Assert.Empty(h.Errors);
		Assert.Equal("1.0.0-rc.1", addIn.SemanticVersion.ToFullString());
	}

	[Fact]
	public async Task InstallAsync_RefusesAPackageBuiltForAnotherHostVersion_BeforeInstallingAnything()
	{
		var h = NewHarness();
		h.DeliverPackage(directory, Manifest(version: "1.2.3", hostRange: "6.0"));

		AddIn addIn = await h.Service.InstallAsync(Release(), default);

		Assert.Null(addIn);
		string message = Assert.Single(h.Errors);
		Assert.Contains("SharpDevelop", message);
		Assert.Contains("6.0", message);
		Assert.Contains("5.9.1.2", message);
		Assert.Empty(h.Management.AddedToTree);
		Assert.False(Directory.Exists(Path.Combine(h.Management.Temp, "Vendor.Sample")));
	}

	[Fact]
	public async Task InstallAsync_AcceptsAHostRangeAndRefusesOneThatEndsBeforeTheHost()
	{
		var h = NewHarness();
		h.DeliverPackage(directory, Manifest(version: "1.2.3", hostRange: "5.8-5.10"));
		Assert.NotNull(await h.Service.InstallAsync(Release(), default));
		Assert.Empty(h.Errors);

		var older = NewHarness();
		older.DeliverPackage(directory, Manifest(version: "1.2.3", hostRange: "5.7-5.8"), "older.odaddin");
		Assert.Null(await older.Service.InstallAsync(Release(), default));
		Assert.Contains("5.7 to 5.8", Assert.Single(older.Errors));
	}

	[Fact]
	public async Task InstallAsync_RefusesAPackageThatNeedsAnAddInThatIsNotInstalled()
	{
		var h = NewHarness();
		h.DeliverPackage(directory, Manifest(version: "1.2.3", extraDependencies: "<Dependency addin=\"Vendor.Needed\" />"));

		Assert.Null(await h.Service.InstallAsync(Release(), default));

		Assert.Contains("Vendor.Needed", Assert.Single(h.Errors));
		Assert.Contains("not installed", h.Errors[0]);
	}

	[Fact]
	public async Task InstallAsync_AcceptsAPackageWhoseDependencyIsInstalled()
	{
		var h = NewHarness();
		h.InstallLocally(Manifest(identity: "Vendor.Needed", version: "1.0.0"));
		h.DeliverPackage(directory, Manifest(version: "1.2.3", extraDependencies: "<Dependency addin=\"Vendor.Needed\" />"));

		Assert.NotNull(await h.Service.InstallAsync(Release(), default));
		Assert.Empty(h.Errors);
	}

	[Fact]
	public async Task InstallAsync_ReportsADownloadThatFailedVerification_AndInstallsNothing()
	{
		var h = NewHarness();
		h.Source.OnDownload = (_, _) => throw new InvalidDataException("The download's SHA-256 does not match.");

		AddIn addIn = await h.Service.InstallAsync(Release(), default);

		Assert.Null(addIn);
		Assert.Contains("SHA-256", Assert.Single(h.Errors));
		Assert.Empty(h.Management.AddedToTree);
		Assert.False(Directory.Exists(Path.GetDirectoryName(h.Source.DownloadedTo[0])));
	}

	[Fact]
	public async Task InstallAsync_RefusesASecondInstallationOfTheSameRepositoryWhileOneRuns()
	{
		var h = NewHarness();
		h.DeliverPackage(directory, Manifest(version: "1.2.3"));
		var deliver = h.Source.OnDownload!;
		var release = new TaskCompletionSource();
		h.Source.OnDownload = async (r, destination) =>
		{
			await release.Task;
			await deliver(r, destination);
		};

		Task<AddIn> first = h.Service.InstallAsync(Release("Owner/My-Addin", "v1.2.3"), default);
		AddIn second = await h.Service.InstallAsync(Release("owner/my-addin", "v1.2.3"), default); // another list, same repository
		AddIn otherTag = await h.Service.InstallAsync(Release("owner/my-addin", "v1.2.4"), default);

		Assert.Null(second);
		Assert.Null(otherTag);
		Assert.Equal(2, h.Errors.Count);
		Assert.All(h.Errors, e => Assert.Contains("already being installed", e));
		Assert.Single(h.Source.DownloadedTo); // the refused ones never reached the network
		Assert.Equal(1, h.OperationsStarted);

		release.SetResult();
		Assert.NotNull(await first);
		Assert.Single(h.Management.AddedToTree);
	}

	[Fact]
	public async Task InstallAsync_AllowsAnotherRepositoryAtTheSameTime_AndTheSameOneAgainAfterwards()
	{
		var h = NewHarness();
		var gate = new TaskCompletionSource();
		h.Source.OnDownload = async (_, _) =>
		{
			await gate.Task;
			throw new InvalidDataException("stop here");
		};

		Task<AddIn> one = h.Service.InstallAsync(Release("owner/one", "v1.0.0"), default);
		Task<AddIn> two = h.Service.InstallAsync(Release("owner/two", "v1.0.0"), default);
		Assert.Equal(2, h.Source.DownloadedTo.Count);
		gate.SetResult();
		await Task.WhenAll(one, two);
		Assert.Equal(2, h.Errors.Count); // both failed on their own, neither was refused as busy
		Assert.DoesNotContain(h.Errors, e => e.Contains("already being installed"));

		h.Source.OnDownload = (_, _) => throw new InvalidDataException("again");
		await h.Service.InstallAsync(Release("owner/one", "v1.0.0"), default);
		Assert.DoesNotContain(h.Errors.Skip(2), e => e.Contains("already being installed"));
		Assert.Equal(3, h.Source.DownloadedTo.Count);
	}

	[Fact]
	public async Task InstallAsync_ReleasesTheRepositoryEvenWhenTheInstallationFailsOrIsCancelled()
	{
		var h = NewHarness();
		h.Source.OnDownload = (_, _) => throw new OperationCanceledException();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.InstallAsync(Release(), default));

		h.Source.OnDownload = (_, _) => throw new InvalidDataException("broken");
		await h.Service.InstallAsync(Release(), default);

		Assert.DoesNotContain(h.Errors, e => e.Contains("already being installed"));
		Assert.Equal(2, h.Source.DownloadedTo.Count);
	}

	[Fact]
	public void Neutralize_BreaksUpEveryPropertyReference()
	{
		Assert.Equal("a $ {res:X} b $ {env:HOME}", AddInMessage.Neutralize("a ${res:X} b ${env:HOME}"));
		Assert.Equal("plain $ text {braces}", AddInMessage.Neutralize("plain $ text {braces}"));
		Assert.Equal(string.Empty, AddInMessage.Neutralize(null!));
	}

	[Fact]
	public async Task InstallAsync_ReportsANetworkFailure()
	{
		var h = NewHarness();
		h.Source.OnDownload = (_, _) => throw new HttpRequestException("connection reset");

		Assert.Null(await h.Service.InstallAsync(Release(), default));

		Assert.Contains("connection reset", Assert.Single(h.Errors));
	}

	[Fact]
	public async Task InstallAsync_PropagatesCancellation_AndCleansUp()
	{
		var h = NewHarness();
		h.Source.OnDownload = (_, _) => throw new OperationCanceledException();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.InstallAsync(Release(), default));

		Assert.Empty(h.Errors);
		Assert.False(Directory.Exists(Path.GetDirectoryName(h.Source.DownloadedTo[0])));
	}

	[Fact]
	public async Task InstallAsync_ReportsAMessageFromTheNetworkWithoutLettingItExpandProperties()
	{
		var h = NewHarness();
		h.Source.OnDownload = (_, _) => throw new HttpRequestException("${res:Secret} ${env:HOME}");

		await h.Service.InstallAsync(Release(), default);

		string message = Assert.Single(h.Errors);
		Assert.DoesNotContain("${", message);
		Assert.Contains("res:Secret", message);
	}

	[Fact]
	public async Task InstallAsync_AnUnsafeIdentityIsRefusedByTheInstaller_WithExactlyOneError()
	{
		var h = NewHarness();
		h.DeliverPackage(directory, Manifest(identity: "..", version: "1.2.3"));

		Assert.Null(await h.Service.InstallAsync(Release(), default));

		Assert.Single(h.Errors);
		Assert.Empty(h.Management.AddedToTree);
	}

	[Fact]
	public async Task InstallAsync_ANonPackageIsReportedAsNotAValidPackage()
	{
		var h = NewHarness();
		h.Source.OnDownload = (_, destination) =>
		{
			File.WriteAllText(destination, "this is not a zip");
			return Task.CompletedTask;
		};

		Assert.Null(await h.Service.InstallAsync(Release(), default));

		Assert.Contains("not a valid add-in package", Assert.Single(h.Errors));
	}

	// ---- compatibility rules ------------------------------------------------------------------------------

	[Theory]
	[InlineData("5.9", "5.9.1.2", true)]
	[InlineData("5.9", "5.10.0.0", false)]
	[InlineData("5.9-5.10", "5.10.3.4", true)]
	[InlineData("5.9-5.10", "5.11.0.0", false)]
	[InlineData("5.9-5.10", "5.8.9.9", false)]
	[InlineData("5.9.1", "5.9.1.7", true)]
	[InlineData("5.9.1", "5.9.2.0", false)]
	public void Problems_ChecksTheHostRange(string range, string hostVersion, bool compatible)
	{
		AddIn candidate = LoadAddIn(Manifest(hostRange: range));

		var problems = AddInCompatibility.Problems(candidate, new[] { Host(hostVersion) });

		Assert.Equal(compatible, problems.Count == 0);
	}

	[Fact]
	public void Problems_APackageWithoutAHostDependencyHasNothingToCheck()
	{
		Assert.Empty(AddInCompatibility.Problems(LoadAddIn(Manifest(hostRange: null)), new[] { Host() }));
	}

	[Fact]
	public void Problems_AnUnversionedDependencyOnTheHostAcceptsAnyVersion()
	{
		AddIn candidate = LoadAddIn(Manifest(hostRange: ""));

		Assert.Empty(AddInCompatibility.Problems(candidate, new[] { Host("99.0.0.0") }));
		Assert.Single(AddInCompatibility.Problems(candidate, Array.Empty<AddIn>()));
	}

	[Fact]
	public void Problems_ReportsAConflictWithAnInstalledAddIn()
	{
		AddIn candidate = LoadAddIn(Manifest(hostRange: null, extraDependencies: "<Conflict addin=\"Vendor.Rival\" />"));
		AddIn rival = LoadAddIn(Manifest(identity: "Vendor.Rival", version: "2.0.0", hostRange: null));

		var problems = AddInCompatibility.Problems(candidate, new[] { rival });

		Assert.Contains("Vendor.Rival", Assert.Single(problems));
		Assert.Empty(AddInCompatibility.Problems(candidate, Array.Empty<AddIn>()));
	}

	[Fact]
	public void Problems_IgnoresTheCandidateItselfAmongThePresentAddIns()
	{
		AddIn candidate = LoadAddIn(Manifest(hostRange: null, extraDependencies: "<Dependency addin=\"Vendor.Sample\" />"));

		Assert.Single(AddInCompatibility.Problems(candidate, new[] { candidate }));
	}
}
