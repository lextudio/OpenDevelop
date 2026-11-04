using System.Xml.Linq;

namespace ICSharpCode.SharpDevelop.Designer.Devices;

/// <summary>The platforms a project builds for, read from its project file. Decides which device
/// previews a designer may offer: an iOS or Android preview only makes sense when the project
/// targets that platform. Pure and tolerant: an unreadable file means "nothing known" (desktop only).</summary>
public sealed record ProjectTargets(IReadOnlyList<string> TargetFrameworks)
{
    public static ProjectTargets None { get; } = new(Array.Empty<string>());

    public bool SupportsIos => Has("-ios") || Has("-maccatalyst");

    public bool SupportsAndroid => Has("-android");

    bool Has(string platformSuffix) => TargetFrameworks.Any(tfm =>
        tfm.Contains(platformSuffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads <c>TargetFramework</c>, <c>TargetFrameworks</c> and Uno's <c>UnoTargetFrameworkOverride</c> from the
    /// project file (conditions are ignored: a framework listed anywhere counts as targeted).</summary>
    public static ProjectTargets ReadFromFile(string? projectFile)
    {
        if (string.IsNullOrEmpty(projectFile) || !File.Exists(projectFile))
            return None;
        try
        {
            return Parse(File.ReadAllText(projectFile));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return None;
        }
    }

    public static ProjectTargets Parse(string projectXml)
    {
        try
        {
            var document = XDocument.Parse(projectXml);
            var frameworks = document.Descendants()
                .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks" or "UnoTargetFrameworkOverride")
                .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(tfm => tfm.Length > 0 && !tfm.Contains("$(", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ProjectTargets(frameworks);
        }
        catch (System.Xml.XmlException)
        {
            return None;
        }
    }
}
