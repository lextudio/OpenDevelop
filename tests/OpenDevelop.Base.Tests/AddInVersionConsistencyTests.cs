using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using ICSharpCode.SharpDevelop;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Every built-in add-in must carry the IDE's product version (src/Main/GlobalAssemblyInfo.cs, generated from GitVersion by
/// Directory.Build.targets, is compiled into every versioned project). The Version Info tab of the About box lists these
/// assemblies, and a mixed list is how a project that was left out - or a stale build output - shows up.
/// The deployed AddIns/ folder only exists after the host and add-ins were built (for example by building the integration
/// test project), so the test skips on a tree that has not been built that far.
/// </summary>
public sealed class AddInVersionConsistencyTests
{
	[Fact]
	public void EveryDeployedAddInAssembly_HasTheHostVersion()
	{
		string root = ProductCopyrightTests.RepositoryRoot();
		string addInRoot = Path.Combine(root, "AddIns");
		var manifests = Directory.Exists(addInRoot)
			? Directory.GetFiles(addInRoot, "*.addin", SearchOption.AllDirectories)
			: Array.Empty<string>();
		Assert.SkipWhen(manifests.Length == 0, "No add-ins are deployed under " + addInRoot + "; build the integration test project first.");

		Version host = typeof(ProductCopyright).Assembly.GetName().Version!;
		Assert.NotEqual(new Version(0, 0, 0, 0), host);
		// The product version must not drift with the commit count: only a new v tag moves it.
		Assert.Equal(0, host.Revision);

		var checkedAssemblies = new List<string>();
		var mismatches = new List<string>();
		foreach (string manifest in manifests) {
			foreach (string path in ImportedAssemblies(manifest)) {
				// A manifest that names a file which was not deployed is a different defect (the add-in cannot load); this
				// test is about the version of the assemblies that are there.
				if (!File.Exists(path))
					continue;
				checkedAssemblies.Add(path);
				Version actual = AssemblyName.GetAssemblyName(path).Version!;
				if (actual != host)
					mismatches.Add(Path.GetRelativePath(addInRoot, path) + ": " + actual + " (host " + host + ")");
			}
		}

		Assert.NotEmpty(checkedAssemblies);
		Assert.True(mismatches.Count == 0,
			"Add-in assemblies that do not carry the host version (a stale build output also shows up here):" + Environment.NewLine
			+ string.Join(Environment.NewLine, mismatches.OrderBy(m => m, StringComparer.Ordinal)));
	}

	/// <summary>
	/// The runtime assemblies a manifest imports by file name. References to the host (":Name") and to another add-in's
	/// assembly ("$Id/file.dll") are not files of this add-in and are skipped; they are checked where they are defined.
	/// </summary>
	static IEnumerable<string> ImportedAssemblies(string manifest)
	{
		string directory = Path.GetDirectoryName(manifest)!;
		return XDocument.Load(manifest).Descendants()
			.Where(e => e.Name.LocalName == "Import" && e.Attribute("assembly") != null)
			.Select(e => e.Attribute("assembly")!.Value)
			.Where(value => !value.StartsWith(":", StringComparison.Ordinal) && !value.StartsWith("$", StringComparison.Ordinal))
			.Select(value => Path.GetFullPath(Path.Combine(directory, value)))
			.Distinct();
	}
}
