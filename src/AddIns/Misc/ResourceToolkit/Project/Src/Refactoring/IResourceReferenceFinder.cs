using System;
using Hornung.ResourceToolkit.Resolver;

namespace Hornung.ResourceToolkit.Refactoring
{
    public interface IResourceReferenceFinder
    {
        int GetNextPossibleOffset(string fileName, string fileContent, int startOffset);
        bool IsReferenceToResource(ResourceResolveResult result);
    }
}
