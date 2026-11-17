using System.Collections.Generic;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Edge;

namespace ICSharpCode.SharpDevelop.Templates
{
    /// <summary>
    /// Identifies the running host to Microsoft.TemplateEngine (externals/OpenDevelop/doc/technotes/template-system.md §1) - the
    /// same engine `dotnet new`/modern Visual Studio use for file and project templates, not
    /// SharpDevelop's or MonoDevelop's own proprietary template formats. The
    /// <see cref="HostIdentifier"/> value is supplied by <see cref="TemplateDiscoveryService"/>'s
    /// parameterless constructor.
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
