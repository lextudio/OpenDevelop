using System.Xml.Linq;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// WpfDesign.SurfaceHost is compiled twice: as its own project and, by source links, into the Microsoft WPF design host
/// (MicrosoftWpfDesign.SurfaceHost, which only Visual Studio's MSBuild on Windows can build). A source file added to the shared
/// directory but not linked into the Microsoft host builds everywhere except there: the host is then not deployed and every Microsoft
/// WinUI/WPF designer journey waits for it (CI run 45233603353: ImplicitXamlNamespaceReader.cs was missing, the whole lane ran 5x slower).
/// This test finds that mistake on any platform.
/// </summary>
public sealed class MicrosoftHostSourceLinksTests
{
	[Fact]
	public void EverySourceFileOfTheSharedSurfaceHost_IsLinkedIntoTheMicrosoftHost()
	{
		var root = ProductCopyrightTests.RepositoryRoot();
		var shared = Path.Combine(root, "src", "AddIns", "DisplayBindings", "WpfDesign", "WpfDesign.SurfaceHost");
		var project = Path.Combine(root, "src", "AddIns", "DisplayBindings", "WpfDesign", "MicrosoftHost", "SurfaceHost", "MicrosoftWpfDesign.SurfaceHost.csproj");

		var linked = XDocument.Load(project).Descendants("Compile")
			.Select(item => (string?)item.Attribute("Include"))
			.Where(include => include is not null)
			.Select(include => include!.Replace('\\', '/'))
			.Where(include => include.StartsWith("../../WpfDesign.SurfaceHost/", StringComparison.Ordinal))
			.Select(include => Path.GetFileName(include))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var sources = Directory.EnumerateFiles(shared, "*.cs", SearchOption.TopDirectoryOnly)
			.Select(path => Path.GetFileName(path))
			.ToList();

		Assert.NotEmpty(sources);
		Assert.Empty(sources.Where(file => !linked.Contains(file)));
	}

	[Fact]
	public void EveryLinkOfTheMicrosoftHost_PointsAtAFileThatExists()
	{
		var root = ProductCopyrightTests.RepositoryRoot();
		var shared = Path.Combine(root, "src", "AddIns", "DisplayBindings", "WpfDesign", "WpfDesign.SurfaceHost");
		var project = Path.Combine(root, "src", "AddIns", "DisplayBindings", "WpfDesign", "MicrosoftHost", "SurfaceHost", "MicrosoftWpfDesign.SurfaceHost.csproj");

		var missing = XDocument.Load(project).Descendants("Compile")
			.Select(item => (string?)item.Attribute("Include"))
			.Where(include => include is not null && include.Replace('\\', '/').StartsWith("../../WpfDesign.SurfaceHost/", StringComparison.Ordinal))
			.Select(include => Path.Combine(shared, Path.GetFileName(include!.Replace('\\', '/'))))
			.Where(path => !File.Exists(path))
			.ToList();

		Assert.Empty(missing);
	}
}
