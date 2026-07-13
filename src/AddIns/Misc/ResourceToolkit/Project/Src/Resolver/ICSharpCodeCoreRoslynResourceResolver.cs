using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Hornung.ResourceToolkit.Resolver
{
    public class ICSharpCodeCoreRoslynResourceResolver : IRoslynResourceResolver
    {
        public ResourceResolveResult Resolve(SyntaxNode node, SemanticModel semanticModel, string fileName, string fileContent, char? charTyped)
        {
            var literal = node.AncestorsAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault();
            if (literal != null && literal.IsKind(SyntaxKind.StringLiteralExpression)) {
                var invocation = literal.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation != null) {
                    var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
                    if (memberAccess != null) {
                        string methodName = memberAccess.Name.Identifier.Text;
                        if (methodName == "GetString") {
                            var target = memberAccess.Expression as IdentifierNameSyntax;
                            if (target != null && target.Identifier.Text == "ResourceService") {
                                if (invocation.ArgumentList.Arguments.Count > 0) {
                                    var firstArg = invocation.ArgumentList.Arguments[0].Expression as LiteralExpressionSyntax;
                                    if (firstArg == literal) {
                                        string key = literal.Token.ValueText;
                                        var resourceSet = ICSharpCodeCoreResourceResolver.ResolveICSharpCodeCoreResourceSet(key, fileName);
                                        return new ResourceResolveResult(resourceSet, key);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        public IEnumerable<string> GetPossiblePatternsForFile(string fileName)
        {
            return new string[] {
                "ResourceService.GetString"
            };
        }
    }
}
