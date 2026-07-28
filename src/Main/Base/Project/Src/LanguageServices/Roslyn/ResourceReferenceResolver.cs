using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace ICSharpCode.SharpDevelop.LanguageServices.Roslyn;

/// <summary>
/// Resolves resource-key string literals in a C# or VB file - the editor-caret resolution,
/// unused-key detection, and rename-refactoring foundation of OpenDevelop's Hornung.ResourceToolkit.
///
/// Faithful adaptation of OpenDevelop's BclRoslynResourceResolver + ICSharpCodeCoreRoslynResourceResolver
/// (Project/Src/Resolver/*.cs) - same syntax-node pattern matching, but deliberately NOT using
/// OpenDevelop's RoslynAstCacheService (a separate ad-hoc SyntaxTree cache maintained outside the
/// app's real Roslyn workspace). Neither of the two ported resolvers actually uses the
/// SemanticModel parameter (verified against the source), so this only needs a syntax tree per
/// call - no separate, diverging Roslyn AST source alongside CSharpVBLanguageService's real
/// workspace.
///
/// Lives in this Base project (not the SharpDevelop app project) so CSharpVBLanguageService -
/// which already serves both the C# and VB LanguageService instances - can call it directly for
/// real editor code completion, not just as a DevFlow-only test hook. The two syntax-specific
/// implementations (CSharpResourceReferenceResolver / VBResourceReferenceResolver) mirror each
/// other; this type is the shared, language-dispatching facade both addins call through.
/// </summary>
public static class ResourceReferenceResolver
{
    public enum ResourceReferenceKind
    {
        /// <summary>ICSharpCode.Core.ResourceService.GetString("key") - resolve live via SD.ResourceService.</summary>
        CoreResourceService,

        /// <summary>BCL-style X.GetString/GetObject/GetStream("key"), X.ApplyResources(_, "key"), or X["key"] - resolve via .resx lookup.</summary>
        BclResourceManager,
    }

    public sealed record ResourceReference(string Key, ResourceReferenceKind Kind);

    /// <summary>A resource reference found at a specific span in a file (for whole-file scans).</summary>
    public sealed record ResourceReferenceOccurrence(string Key, ResourceReferenceKind Kind, int Offset, int Length);

    /// <summary>
    /// Finds the resource-key string literal at <paramref name="offset"/> in <paramref name="fileContent"/>,
    /// if the caret is on the key argument of a recognized resource-access pattern, dispatching to
    /// the C# or VB syntax matcher per <paramref name="language"/> (<see cref="LanguageNames.CSharp"/>
    /// or <see cref="LanguageNames.VisualBasic"/>). Returns null if the caret isn't on such a
    /// literal, or the language isn't one of those two.
    /// </summary>
    public static ResourceReference? FindResourceKeyAtCursor(string language, string fileContent, int offset)
        => language switch
        {
            LanguageNames.CSharp => CSharpResourceReferenceResolver.FindResourceKeyAtCursor(fileContent, offset),
            LanguageNames.VisualBasic => VBResourceReferenceResolver.FindResourceKeyAtCursor(fileContent, offset),
            _ => null,
        };

    /// <summary>
    /// Finds every resource-key string literal reference anywhere in <paramref name="fileContent"/>
    /// (not just at one cursor position) - the primitive unused-key detection and rename
    /// refactoring build on, mirroring OpenDevelop's IResourceReferenceFinder/AnyResourceReferenceFinder
    /// whole-file scan (Refactoring/AnyResourceReferenceFinder.cs), adapted to this file's simpler
    /// syntax-only matching (no IResourceFileContent/ResourceFileContentRegistry abstraction).
    /// </summary>
    public static IReadOnlyList<ResourceReferenceOccurrence> FindAllResourceReferences(string language, string fileContent)
        => language switch
        {
            LanguageNames.CSharp => CSharpResourceReferenceResolver.FindAllResourceReferences(fileContent),
            LanguageNames.VisualBasic => VBResourceReferenceResolver.FindAllResourceReferences(fileContent),
            _ => Array.Empty<ResourceReferenceOccurrence>(),
        };

    /// <summary>Language inferred from a file extension - ".cs" -&gt; C#, ".vb" -&gt; VB, else null.</summary>
    public static string? LanguageFromFileName(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName);
        if (string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase))
            return LanguageNames.CSharp;
        if (string.Equals(ext, ".vb", StringComparison.OrdinalIgnoreCase))
            return LanguageNames.VisualBasic;
        return null;
    }
}
