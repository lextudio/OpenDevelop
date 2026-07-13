using System;
using System.Collections.Generic;
using System.Linq;
using Hornung.ResourceToolkit.ResourceFileContent;
using ICSharpCode.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Hornung.ResourceToolkit.Resolver
{
    public class BclRoslynResourceResolver : IRoslynResourceResolver
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
                        if (methodName == "GetString" || methodName == "GetObject" || methodName == "GetStream") {
                            if (invocation.ArgumentList.Arguments.Count > 0) {
                                var firstArg = invocation.ArgumentList.Arguments[0].Expression as LiteralExpressionSyntax;
                                if (firstArg != null && firstArg == literal) {
                                    string key = literal.Token.ValueText;
                                    var resourceSet = RoslynResourceResolver.GetResourceSetReference(fileName, methodName);
                                    if (key != null) {
                                        return new ResourceResolveResult(resourceSet, key);
                                    }
                                }
                            }
                        } else if (methodName == "ApplyResources") {
                            if (invocation.ArgumentList.Arguments.Count >= 2) {
                                var secondArg = invocation.ArgumentList.Arguments[1].Expression as LiteralExpressionSyntax;
                                if (secondArg != null && secondArg == literal) {
                                    string key = literal.Token.ValueText;
                                    var resourceSet = RoslynResourceResolver.GetResourceSetReference(fileName, methodName);
                                    if (key != null) {
                                        return new ResourcePrefixResolveResult(resourceSet, key);
                                    }
                                }
                            }
                        }
                    }
                }

                var elementAccess = literal.Ancestors().OfType<ElementAccessExpressionSyntax>().FirstOrDefault();
                if (elementAccess != null) {
                    var argList = elementAccess.ArgumentList;
                    if (argList != null && argList.Arguments.Count > 0) {
                        var arg = argList.Arguments[0].Expression as LiteralExpressionSyntax;
                        if (arg == literal) {
                            string key = literal.Token.ValueText;
                            var resourceSet = RoslynResourceResolver.GetResourceSetReference(fileName, null);
                            if (key != null) {
                                return new ResourceResolveResult(resourceSet, key);
                            }
                        }
                    }
                }
            }

            if (charTyped == '(') {
                var memberAccess = node.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault();
                if (memberAccess != null) {
                    string methodName = memberAccess.Name.Identifier.Text;
                    if (methodName == "GetString" || methodName == "GetObject" || methodName == "GetStream") {
                        var resourceSet = RoslynResourceResolver.GetResourceSetReference(fileName, null);
                        return new ResourceResolveResult(resourceSet, null);
                    }
                }
            }

            return null;
        }

        public IEnumerable<string> GetPossiblePatternsForFile(string fileName)
        {
            return new string[] {
                "GetString",
                "GetObject",
                "GetStream",
                "ApplyResources",
                "["
            };
        }
    }
}
