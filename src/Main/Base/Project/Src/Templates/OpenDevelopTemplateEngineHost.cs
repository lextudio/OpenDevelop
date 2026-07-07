using System.Collections.Generic;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Edge;

namespace ICSharpCode.SharpDevelop.Templates
{
    public static class OpenDevelopTemplateEngineHost
    {
        public const string HostIdentifier = "opendevelop";

        public static ITemplateEngineHost Create()
        {
            return new DefaultTemplateEngineHost(
                hostIdentifier: HostIdentifier,
                version: "1.0.0",
                defaults: new Dictionary<string, string>());
        }
    }
}
