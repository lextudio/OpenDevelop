using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Hornung.ResourceToolkit.Resolver
{
    public interface IRoslynResourceResolver
    {
        ResourceResolveResult Resolve(SyntaxNode node, SemanticModel semanticModel, string fileName, string fileContent, char? charTyped);
        IEnumerable<string> GetPossiblePatternsForFile(string fileName);
    }
}
