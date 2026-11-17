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
		public static IResourceService Create() => Create<IResourceService, KeyEchoingResourceService>();

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			if (targetMethod!.Name == nameof(IResourceService.GetString) && args is { Length: 1 })
				return "${res:" + args[0] + "}";
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
			<AddIn name="Sample" author="x" copyright="x" url="x" description="x" {extraAttributes}>
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
}
