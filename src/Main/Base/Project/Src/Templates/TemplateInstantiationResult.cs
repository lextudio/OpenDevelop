using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Templates
{
    public sealed record TemplateInstantiationResult(
        bool Success,
        string? ErrorMessage,
        string OutputDirectory,
        IReadOnlyList<string> PrimaryOutputPaths);
}
