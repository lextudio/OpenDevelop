using System;
using System.Collections.Generic;
using ICSharpCode.SharpDevelop.Editor;

namespace Hornung.ResourceToolkit.Resolver
{
    public interface IResourceResolver
    {
        ResourceResolveResult Resolve(ITextEditor editor, char? charTyped);
        bool SupportsFile(string fileName);
        IEnumerable<string> GetPossiblePatternsForFile(string fileName);
    }
}
