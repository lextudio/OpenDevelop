using System;
using Hornung.ResourceToolkit.Resolver;

namespace Hornung.ResourceToolkit.Refactoring
{
    public class AnyResourceReferenceFinder : IResourceReferenceFinder
    {
        public int GetNextPossibleOffset(string fileName, string fileContent, int startOffset)
        {
            if (startOffset < 0) startOffset = 0;
            int index = fileContent.IndexOf("\"", startOffset, StringComparison.Ordinal);
            if (index < 0) {
                string[] patterns = { "GetString", "GetObject", "GetStream", "ApplyResources" };
                foreach (string pattern in patterns) {
                    int pIndex = fileContent.IndexOf(pattern, startOffset, StringComparison.Ordinal);
                    if (pIndex >= 0 && (index < 0 || pIndex < index)) {
                        index = pIndex;
                    }
                }
            }
            return index;
        }

        public bool IsReferenceToResource(ResourceResolveResult result)
        {
            return result != null && result.ResourceFileContent != null;
        }
    }
}
