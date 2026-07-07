using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Templates
{
    public sealed record TemplateSummary(
        string Identity,
        string ShortName,
        string Name,
        string? Description,
        IReadOnlyDictionary<string, string> Tags);
}
