using System;
using Hornung.ResourceToolkit.ResourceFileContent;

namespace Hornung.ResourceToolkit.Resolver
{
    public class ResourceSetReference
    {
        readonly string resourceSetName;
        readonly string fileName;

        public string ResourceSetName {
            get { return resourceSetName; }
        }

        public string FileName {
            get { return fileName; }
        }

        public IResourceFileContent ResourceFileContent {
            get {
                if (this.FileName == null) return null;
                return ResourceFileContentRegistry.GetResourceFileContent(this.FileName);
            }
        }

        public ResourceSetReference(string resourceSetName, string fileName)
        {
            if (resourceSetName == null) {
                throw new ArgumentNullException("resourceSetName");
            } else if (resourceSetName.Length == 0) {
                throw new ArgumentException("The resourceSetName must not be empty.", "resourceSetName");
            }
            this.resourceSetName = resourceSetName;
            this.fileName = fileName;
        }
    }
}
