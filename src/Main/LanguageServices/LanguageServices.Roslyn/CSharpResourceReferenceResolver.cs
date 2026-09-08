#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ICSharpCode.SharpDevelop.LanguageServices.Roslyn.ResourceReferenceResolver;

namespace ICSharpCode.SharpDevelop.LanguageServices.Roslyn;

/// <summary>
/// C# syntax matcher behind <see cref="ResourceReferenceResolver"/> - see that type's remarks.
/// </summary>
static class CSharpResourceReferenceResolver
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
            return CSharpSyntaxTree.ParseText(fileContent);
        }
        catch
        {
            return null;
        }
    }

    static ResourceReferenceKind? MatchLiteral(LiteralExpressionSyntax literal)
    {
        // ICSharpCode.Core.ResourceService.GetString("key")
        var invocation = literal.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation?.Expression is MemberAccessExpressionSyntax coreAccess
            && coreAccess.Name.Identifier.Text == "GetString"
            && coreAccess.Expression is IdentifierNameSyntax { Identifier.Text: "ResourceService" }
            && invocation.ArgumentList.Arguments.Count > 0
            && invocation.ArgumentList.Arguments[0].Expression == literal)
        {
            return ResourceReferenceKind.CoreResourceService;
        }

        // X.GetString/GetObject/GetStream("key") or X.ApplyResources(_, "key")
        if (invocation?.Expression is MemberAccessExpressionSyntax bclAccess)
        {
            var methodName = bclAccess.Name.Identifier.Text;
            if (methodName is "GetString" or "GetObject" or "GetStream"
                && invocation.ArgumentList.Arguments.Count > 0
                && invocation.ArgumentList.Arguments[0].Expression == literal)
            {
                return ResourceReferenceKind.BclResourceManager;
            }

            if (methodName == "ApplyResources"
                && invocation.ArgumentList.Arguments.Count >= 2
                && invocation.ArgumentList.Arguments[1].Expression == literal)
            {
                return ResourceReferenceKind.BclResourceManager;
            }
        }

        // X["key"]
        var elementAccess = literal.Ancestors().OfType<ElementAccessExpressionSyntax>().FirstOrDefault();
        if (elementAccess?.ArgumentList.Arguments.Count > 0
            && elementAccess.ArgumentList.Arguments[0].Expression == literal)
        {
            return ResourceReferenceKind.BclResourceManager;
        }

        return null;
    }
}
