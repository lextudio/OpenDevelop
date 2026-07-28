using System.Collections.Generic;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Edge;

namespace ICSharpCode.SharpDevelop.Templates
{
    /// <summary>
    /// Identifies the running host to Microsoft.TemplateEngine (docs/template-system.md §1) - the
    /// same engine `dotnet new`/modern Visual Studio use for file and project templates, not
    /// SharpDevelop's or MonoDevelop's own proprietary template formats. Shared by both hosts (see
    /// doc/technotes/solution-explorer.md's precedent) - this used to be two near-identical classes
    /// differing only in <see cref="HostIdentifier"/>'s value ("unodevelop" vs "opendevelop"),
    /// which <see cref="TemplateDiscoveryService"/>'s parameterless constructor now supplies
    /// directly instead.
    /// </summary>
    public static class TemplateEngineHost
    {
        public static ITemplateEngineHost Create(string hostIdentifier, string version = "1.0.0")
        {
            return new DefaultTemplateEngineHost(
                hostIdentifier: hostIdentifier,
                version: version,
                defaults: new Dictionary<string, string>());
        }
    }
}
