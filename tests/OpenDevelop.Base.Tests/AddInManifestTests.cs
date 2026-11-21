using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The add-in manifests and the main menu they describe, checked against the repository rather than a
/// running IDE: an Import that names a file nobody built stops the add-in from ever loading
/// (CodeQuality and AddInScout shipped that way), and the Help menu carried SharpDevelop-era links
/// (dead domains, CodePlex, a wiki for another project) until it was repointed at OpenDevelop.
/// </summary>
public sealed class AddInManifestTests
{
    static string Root => ProductCopyrightTests.RepositoryRoot();
    static string AddInRoot => Path.Combine(Root, "AddIns");
    static string MainManifest => Path.Combine(Root, "src", "Main", "Base", "Project", "ICSharpCode.SharpDevelop.addin");

    [Fact]
    public void DeployedAddIns_ImportAssembliesThatExist()
    {
        List<string> manifests = DeployedManifests();
        Assert.SkipWhen(manifests.Count == 0, "No add-ins are deployed under " + AddInRoot + "; build the integration test project first.");

        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string manifest in manifests) {
            XElement identity = XDocument.Load(manifest).Root?.Element("Manifest")?.Element("Identity");
            string? name = (string?)identity?.Attribute("name");
            if (!string.IsNullOrEmpty(name))
                identities.TryAdd(name, Path.GetDirectoryName(manifest)!);
        }

        var missing = new List<string>();
        foreach (string manifest in manifests) {
            string directory = Path.GetDirectoryName(manifest)!;
            foreach (XElement import in XDocument.Load(manifest).Descendants("Import")) {
                string? assembly = (string?)import.Attribute("assembly");
                if (string.IsNullOrEmpty(assembly) || assembly.StartsWith(':'))
                    continue; // ":X" is a host assembly the IDE already provides.

                string path;
                if (assembly.StartsWith('$')) {
                    // "$Identity/File.dll" names an assembly in another add-in's directory.
                    int slash = assembly.IndexOf('/');
                    string other = slash < 0 ? assembly[1..] : assembly[1..slash];
                    string file = slash < 0 ? "" : assembly[(slash + 1)..];
                    if (!identities.TryGetValue(other, out string? otherDirectory)) {
                        missing.Add($"{Path.GetRelativePath(AddInRoot, manifest)}: unknown add-in identity '{other}'");
                        continue;
                    }
                    path = Path.Combine(otherDirectory, file);
                } else {
                    path = Path.Combine(directory, assembly);
                }

                if (!File.Exists(path))
                    missing.Add($"{Path.GetRelativePath(AddInRoot, manifest)}: {assembly}");
            }
        }

        Assert.True(missing.Count == 0,
            "These manifests import an assembly that was not deployed, so the add-in cannot load:" +
            Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void DeployedAddIns_HaveOneManifestPerIdentity()
    {
        List<string> manifests = DeployedManifests();
        Assert.SkipWhen(manifests.Count == 0, "No add-ins are deployed under " + AddInRoot + "; build the integration test project first.");

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string manifest in manifests) {
            string? identity = (string?)XDocument.Load(manifest).Root?.Element("Manifest")?.Element("Identity")?.Attribute("name");
            if (string.IsNullOrEmpty(identity))
                continue;
            Assert.False(seen.TryGetValue(identity, out string? first),
                $"'{identity}' is declared by both {first} and {manifest}");
            seen.Add(identity, manifest);
        }
    }

    [Fact]
    public void MainMenu_HelpLinksToOpenDevelop_NotToRetiredSharpDevelopSites()
    {
        XElement help = HelpPath();
        string[] retired = ["icsharpcode.net", "sharpdevelop.net", "codeplex.com", "msdn.microsoft.com/net"];

        var offenders = new List<string>();
        foreach (XElement item in help.DescendantsAndSelf("MenuItem")) {
            string? link = (string?)item.Attribute("link");
            if (string.IsNullOrEmpty(link))
                continue;
            string id = (string?)item.Attribute("id") ?? "?";
            if (retired.Any(domain => link.Contains(domain, StringComparison.OrdinalIgnoreCase)))
                offenders.Add($"{id} -> {link}");
            else if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                offenders.Add($"{id} is not https -> {link}");
        }

        Assert.True(offenders.Count == 0,
            "The Help menu still points at retired or insecure sites:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void MainMenu_WebMenuOffersTheCurrentProjectAndItsComponents()
    {
        XElement web = HelpPath().Elements("MenuItem").Single(e => (string?)e.Attribute("id") == "Web");
        string[] expected = ["Home", "Downloads", "Issues", "Contributing", "ThirdPartyNotices", "Sponsor",
            "AvalonEdit", "AvalonDock", "ILSpy", "LibreWPF", "SharpDbg"];
        var ids = web.Elements("MenuItem")
            .Where(e => (string?)e.Attribute("type") != "Separator")
            .Select(e => (string?)e.Attribute("id"))
            .ToList();

        Assert.Equal(expected, ids);
    }

    [Fact]
    public void MainMenu_ResourceKeysExistInTheNeutralResourceFile()
    {
        HashSet<string> keys = ResourceKeys(Path.Combine(Root, "data", "resources", "StringResources.resx"));
        var referenced = Regex.Matches(File.ReadAllText(MainManifest), @"\$\{res:([^}]+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .Where(k => !keys.Contains(k))
            .ToList();

        Assert.True(referenced.Count == 0,
            "The main manifest uses resource keys with no entry in StringResources.resx:" +
            Environment.NewLine + string.Join(Environment.NewLine, referenced));
    }

    static List<string> DeployedManifests() =>
        Directory.Exists(AddInRoot)
            ? Directory.GetFiles(AddInRoot, "*.addin", SearchOption.AllDirectories).ToList()
            : [];

    /// <summary>The Help menu's Path node in the main add-in manifest.</summary>
    static XElement HelpPath()
    {
        XElement root = XDocument.Load(MainManifest).Root!;
        XElement helpMenu = root.Descendants("Path")
            .Single(p => (string?)p.Attribute("name") == "/SharpDevelop/Workbench/MainMenu")
            .Elements("MenuItem")
            .Single(m => (string?)m.Attribute("id") == "Help");
        return helpMenu;
    }

    static HashSet<string> ResourceKeys(string resx)
    {
        return XDocument.Load(resx).Root!.Elements("data")
            .Select(d => (string?)d.Attribute("name"))
            .Where(n => n != null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
