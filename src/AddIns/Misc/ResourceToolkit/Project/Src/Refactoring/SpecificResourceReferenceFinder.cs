using System;
using Hornung.ResourceToolkit.Resolver;
using ICSharpCode.Core;

namespace Hornung.ResourceToolkit.Refactoring
{
    public class SpecificResourceReferenceFinder : IResourceReferenceFinder
    {
        readonly string resourceFileName;
        readonly string key;

        public SpecificResourceReferenceFinder(string resourceFileName, string key)
        {
            this.resourceFileName = resourceFileName;
            this.key = key;
        }

        public int GetNextPossibleOffset(string fileName, string fileContent, int startOffset)
        {
            if (startOffset < 0) startOffset = 0;
            int index = fileContent.IndexOf(this.key, startOffset, StringComparison.Ordinal);
            return index;
        }

        public bool IsReferenceToResource(ResourceResolveResult result)
        {
            if (result == null || result.Key == null) {
                return false;
            }
            if (!FileUtility.IsEqualFileName(result.FileName, this.resourceFileName)) {
                return false;
            }
            return result.Key.Equals(this.key, StringComparison.OrdinalIgnoreCase);
        }
    }
}
