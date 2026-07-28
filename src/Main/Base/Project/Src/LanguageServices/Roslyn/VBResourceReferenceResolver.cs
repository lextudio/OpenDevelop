using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using static ICSharpCode.SharpDevelop.LanguageServices.Roslyn.ResourceReferenceResolver;

namespace ICSharpCode.SharpDevelop.LanguageServices.Roslyn;

/// <summary>
/// VB syntax matcher behind <see cref="ResourceReferenceResolver"/> - mirrors
/// <see cref="CSharpResourceReferenceResolver"/>'s pattern matching using VB's own syntax node
/// types. One real VB-specific difference: VB has no distinct element-access syntax node - `X("key")`
/// parses as an InvocationExpressionSyntax whose Expression is a bare IdentifierNameSyntax (arrays,
/// indexers, and function calls are syntactically identical in VB), so that case is handled as an
/// invocation with a non-member-access target rather than a separate ElementAccessExpressionSyntax
/// match like the C# resolver has.
/// </summary>
static class VBResourceReferenceResolver
{
    public static ResourceReference? FindResourceKeyAtCursor(string fileContent, int offset)
    {
        if (string.IsNullOrEmpty(fileContent) || offset < 0 || offset > fileContent.Length)
            return null;

        var tree = TryParse(fileContent);
        if (tree is null)
            return null;

        var root = tree.GetRoot();
        if (offset >= root.FullSpan.Length)
            offset = Math.Max(0, root.FullSpan.Length - 1);

        var token = root.FindToken(offset);
        var node = token.Parent;
        if (node is null)
            return null;

        var literal = node.AncestorsAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault();
        if (literal is null || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            return null;

        var kind = MatchLiteral(literal);
        return kind is null ? null : new ResourceReference(literal.Token.ValueText, kind.Value);
    }

    public static IReadOnlyList<ResourceReferenceOccurrence> FindAllResourceReferences(string fileContent)
    {
        var tree = TryParse(fileContent);
        if (tree is null)
            return Array.Empty<ResourceReferenceOccurrence>();

        var results = new List<ResourceReferenceOccurrence>();
        foreach (var literal in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                continue;

            var kind = MatchLiteral(literal);
            if (kind is null)
                continue;

            var span = literal.Span;
            results.Add(new ResourceReferenceOccurrence(literal.Token.ValueText, kind.Value, span.Start, span.Length));
        }

        return results;
    }

    static SyntaxTree? TryParse(string fileContent)
    {
        if (string.IsNullOrEmpty(fileContent))
            return null;

        try
        {
            return VisualBasicSyntaxTree.ParseText(fileContent);
        }
        catch
        {
            return null;
        }
    }

    static ResourceReferenceKind? MatchLiteral(LiteralExpressionSyntax literal)
    {
        var invocation = literal.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation?.ArgumentList is not { } argumentList)
            return null;

        var arguments = argumentList.Arguments;

        // ICSharpCode.Core.ResourceService.GetString("key")
        if (invocation.Expression is MemberAccessExpressionSyntax coreAccess
            && coreAccess.Name.Identifier.Text == "GetString"
            && coreAccess.Expression is IdentifierNameSyntax { Identifier.Text: "ResourceService" }
            && arguments.Count > 0
            && ArgumentExpression(arguments[0]) == literal)
        {
            return ResourceReferenceKind.CoreResourceService;
        }

        // X.GetString/GetObject/GetStream("key") or X.ApplyResources(_, "key")
        if (invocation.Expression is MemberAccessExpressionSyntax bclAccess)
        {
            var methodName = bclAccess.Name.Identifier.Text;
            if (methodName is "GetString" or "GetObject" or "GetStream"
                && arguments.Count > 0
                && ArgumentExpression(arguments[0]) == literal)
            {
                return ResourceReferenceKind.BclResourceManager;
            }

            if (methodName == "ApplyResources"
                && arguments.Count >= 2
                && ArgumentExpression(arguments[1]) == literal)
            {
                return ResourceReferenceKind.BclResourceManager;
            }
        }

        // X("key") - VB's indexer/array-access syntax is indistinguishable from a function call;
        // this is the VB equivalent of the C# resolver's ElementAccessExpressionSyntax case.
        if (invocation.Expression is IdentifierNameSyntax
            && arguments.Count > 0
            && ArgumentExpression(arguments[0]) == literal)
        {
            return ResourceReferenceKind.BclResourceManager;
        }

        return null;
    }

    static ExpressionSyntax? ArgumentExpression(ArgumentSyntax argument)
        => argument is SimpleArgumentSyntax simple ? simple.Expression : null;
}
