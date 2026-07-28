using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Templates
{
    /// <summary>
    /// Presentation-shaped template listing entry — decoupled from
    /// <c>Microsoft.TemplateEngine.Abstractions.ITemplateInfo</c> the same way
    /// <c>NuGetSearchResult</c> decouples from <c>NuGet.Protocol</c>'s search metadata
    /// (docs/nuget-manager.md), so listing/filtering logic is testable without a real
    /// installed template package.
    /// </summary>
    public sealed record TemplateSummary(
        string Identity,
        string ShortName,
        string Name,
        string? Description,
        IReadOnlyDictionary<string, string> Tags);
}
