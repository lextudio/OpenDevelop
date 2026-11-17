using System.IO.Compression;
using System.Reflection;
using System.Text;
using ICSharpCode.AddInManager2.Model;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;

namespace OpenDevelop.Base.Tests;

/// <summary>Shared fixtures for the AddIn Manager's package, installer and release-service tests.</summary>
static class AddInTestSupport
{
	/// <summary>Starts the unit-test service container with a resource service that echoes keys.</summary>
	public static void InitializeServices()
	{
		SD.InitializeForUnitTests();
		SD.Services.AddService(typeof(IResourceService), KeyEchoingResourceService.Create());
	}

	/// <summary>
	/// The unit-test service container has no string resources; error paths only need a message, so every
	/// string resolves to its own key (and anything else the interface offers returns its default).
	/// </summary>
	public class KeyEchoingResourceService : DispatchProxy
	{
		/// <summary>Texts a test needs to be real (for example a format string); every other key echoes itself.</summary>
		public static readonly Dictionary<string, string> Overrides = new()
		{
			["AddInManager2.TrustedRepositories.TrustWarning"] = "Trust {0}? An add-in runs with all your rights.",
			["AddInManager2.TrustedRepositories.AlreadyListed"] = "{0} is already in the list.",
		};

		public static IResourceService Create() => Create<IResourceService, KeyEchoingResourceService>();

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			if (targetMethod!.Name == nameof(IResourceService.GetString) && args is { Length: 1 })
				return Overrides.TryGetValue((string)args[0]!, out string? text) ? text : (string)args[0]!;
			Type returnType = targetMethod.ReturnType;
			return returnType == typeof(void) || !returnType.IsValueType ? null : Activator.CreateInstance(returnType);
		}
	}

	/// <param name="hostRange">The SharpDevelop (host) dependency's version, or null for none.</param>
	/// <param name="extraDependencies">More manifest children, for example another Dependency element.</param>
	public static string Manifest(
		string identity = "Vendor.Sample", string version = "1.2.3", string extraAttributes = "",
		string? hostRange = "5.9", string extraDependencies = "")
	{
		string host = hostRange == null ? "" : $"<Dependency addin=\"SharpDevelop\" version=\"{hostRange}\" />";
		return $"""
			<AddIn name="Sample" author="x" copyright="x" url="https://example.com/" description="x" {extraAttributes}>
			  <Manifest><Identity name="{identity}" version="{version}" />{host}{extraDependencies}</Manifest>
			  <Runtime><Import assembly="Sample.dll" /></Runtime>
			</AddIn>
			""";
	}

	public static string Package(string directory, string name, params (string Name, string Text)[] entries)
	{
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, name);
		using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
		{
			foreach (var (entryName, text) in entries)
			{
				var entry = zip.CreateEntry(entryName);
				using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
				writer.Write(text);
			}
		}
		return path;
	}

	public static AddInRelease Release(string repo = "owner/my-addin", string tag = "v1.2.3", bool prerelease = false)
	{
		string numeric = tag.TrimStart('v');
		int dash = numeric.IndexOf('-');
		if (dash >= 0)
			numeric = numeric.Substring(0, dash);
		return new(RepositoryReference.Parse(repo), tag, Version.Parse(numeric), prerelease || dash >= 0, "Sample.odaddin",
			new Uri($"https://github.com/{repo}/releases/download/{tag}/Sample.odaddin"), 4, new string('a', 64), "", null, null);
	}

	/// <summary>An AddIn read from manifest text, as the add-in tree would.</summary>
	public static AddIn LoadAddIn(string manifestText) => AddIn.Load(SD.AddInTree, new StringReader(manifestText));

	/// <summary>The host: the add-in named SharpDevelop, whose version is the IDE's.</summary>
	public static AddIn Host(string version = "5.9.1.2") =>
		LoadAddIn($"<AddIn name=\"Host\"><Manifest><Identity name=\"SharpDevelop\" version=\"{version}\"/></Manifest></AddIn>");

	public sealed class FakeSdManagement : ISDAddInManagement
	{
		public List<AddIn> Installed { get; } = new();
		public List<AddIn> AddedToTree { get; } = new();
		public List<string> AbortedRemovals { get; } = new();
		public string Temp { get; set; } = "";
		public IReadOnlyList<AddIn> AddIns => Installed;
		public string TempInstallDirectory => Temp;
		public string UserInstallDirectory => Temp;
		public string ConfigDirectory => Temp;
		public void AddToTree(AddIn addIn) { AddedToTree.Add(addIn); Installed.Add(addIn); } // the real tree lists it afterwards
		public void AbortRemoveUserAddInOnNextStart(string identity) => AbortedRemovals.Add(identity);
		public void Enable(IList<AddIn> addIns) { }
		public void Disable(IList<AddIn> addIns) { }
		public void Suspend(IList<AddIn> addIns) { }
		public void Resume(IList<AddIn> addIns) { }
		public void RemoveExternalAddIns(IList<AddIn> addIns) { }
		public void RemoveUserAddInOnNextStart(string identity) { }
		public AddIn Load(TextReader textReader) => AddIn.Load(SD.AddInTree, textReader);
		public AddIn Load(string fileName) => AddIn.Load(SD.AddInTree, fileName);
		public void AddExternalAddIns(IList<AddIn> addIns) { }
		public bool IsAddInManifestInExternalPath(AddIn addIn) => false;
	}

	// ---- fakes --------------------------------------------------------------------------------------------

	public sealed class FakeSettings : IAddInManagerSettings
	{
		public string[] PackageRepositories { get; set; } = Array.Empty<string>();
		public string[] TrustedRepositories { get; set; } = Array.Empty<string>();
		public bool ShowPreinstalledAddIns { get; set; }
		public bool ShowPrereleases { get; set; }
		public bool AutoSearchForUpdates { get; set; } = true;
	}

	public sealed class FakeSource : IAddInReleaseSource
	{
		public Dictionary<RepositoryReference, ReleaseListResult> Lists { get; } = new();
		public List<(string Repository, bool Force)> Requests { get; } = new();
		public List<string> DownloadedTo { get; } = new();
		public Func<AddInRelease, string, Task>? OnDownload { get; set; }

		public Task<ReleaseListResult> GetReleasesAsync(RepositoryReference repository, bool forceRefresh, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add((repository.FullName, forceRefresh));
			return Task.FromResult(Lists.TryGetValue(repository, out var list)
				? list
				: new ReleaseListResult(repository, ReleaseListStatus.Failed, null, null, "not found", null, null));
		}

		public async Task DownloadAsync(AddInRelease release, string destinationFile, CancellationToken cancellationToken)
		{
			DownloadedTo.Add(destinationFile);
			if (OnDownload != null)
				await OnDownload(release, destinationFile);
		}
	}

	public sealed class ReleaseHarness
	{
		public FakeSettings Settings { get; } = new();
		public FakeSource Source { get; } = new();
		public FakeSdManagement Management { get; }
		public AddInManagerEvents Events { get; } = new();
		public AddInSetup Setup { get; }
		public AddInReleaseService Service { get; }
		public List<string> Errors { get; } = new();
		public int OperationsStarted { get; private set; }

		/// <summary>The same settings, events, setup and add-in management as <paramref name="inner"/>, with another release service.</summary>
		public ReleaseHarness(ReleaseHarness inner, AddInReleaseService service)
		{
			Settings = inner.Settings;
			Source = inner.Source;
			Management = inner.Management;
			Events = inner.Events;
			Setup = inner.Setup;
			Errors = inner.Errors;
			Service = service;
		}

		public ReleaseHarness(string directory, params string[] trusted)
		{
			Settings.TrustedRepositories = trusted;
			Management = new FakeSdManagement { Temp = Path.Combine(directory, "temp-install") };
			Management.Installed.Add(Host());
			Events.AddInOperationError += (_, e) => Errors.Add(e.Message);
			Events.OperationStarted += (_, _) => OperationsStarted++;
			Setup = new AddInSetup(Events, null!, Management);
			Service = new AddInReleaseService(Source, Settings, Events, Setup, Management);
		}

		public void Offer(string repo, params AddInRelease[] releases)
		{
			var reference = RepositoryReference.Parse(repo);
			Source.Lists[reference] = new ReleaseListResult(reference, ReleaseListStatus.Ok, releases, null, null, DateTimeOffset.UtcNow, null);
		}

		/// <summary>Makes a download deliver a real .odaddin with this manifest.</summary>
		public void DeliverPackage(string directory, string manifest, string name = "delivered.odaddin")
		{
			string package = Package(directory, name, ("Vendor.Sample.addin", manifest), ("Sample.dll", "dll"));
			Source.OnDownload = (_, destination) =>
			{
				File.Copy(package, destination);
				return Task.CompletedTask;
			};
		}

		public AddIn InstallLocally(string manifest)
		{
			var addIn = LoadAddIn(manifest);
			Management.Installed.Add(addIn);
			return addIn;
		}
	}

	/// <summary>The services of one <see cref="ReleaseHarness"/>, as the AddIn Manager's view models see them.</summary>
	public sealed class TestServices(ReleaseHarness harness) : IAddInManagerServices
	{
		public IAddInManagerEvents Events => harness.Events;
		public IPackageRepositories Repositories => throw new NotSupportedException();
		public IAddInSetup Setup => harness.Setup;
		public IAddInReleaseService Releases => harness.Service;
		public INuGetPackageManager NuGet => throw new NotSupportedException();
		public IAddInManagerSettings Settings => harness.Settings;
		public ISDAddInManagement SDAddInManagement => harness.Management;
	}

	/// <summary>A message service that records what it was asked to show and answers questions as told.</summary>
	public class FakeMessageService : DispatchProxy
	{
		public bool Answer { get; set; } = true;
		public List<string> Calls { get; } = new();

		public static (IMessageService Service, FakeMessageService Fake) Create()
		{
			IMessageService proxy = Create<IMessageService, FakeMessageService>();
			return (proxy, (FakeMessageService)(object)proxy);
		}

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			Calls.Add(targetMethod!.Name + ":" + string.Join("|", args ?? Array.Empty<object?>()));
			if (targetMethod.Name == "AskQuestion")
				return Answer;
			Type returnType = targetMethod.ReturnType;
			return returnType == typeof(void) || !returnType.IsValueType ? null : Activator.CreateInstance(returnType);
		}
	}
}
