#nullable enable
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Templates
{
    /// <summary>
    /// Result of template instantiation or dry-run (externals/OpenDevelop/doc/technotes/template-system.md slice 2).
    /// Decoupled from <c>ITemplateCreationResult</c> the same way <see cref="TemplateSummary"/>
    /// decouples from <c>ITemplateInfo</c> — testable without a real template engine instance.
    /// </summary>
    public sealed record TemplateInstantiationResult(
        bool Success,
        string? ErrorMessage,
        string OutputDirectory,
        IReadOnlyList<string> PrimaryOutputPaths);
}
