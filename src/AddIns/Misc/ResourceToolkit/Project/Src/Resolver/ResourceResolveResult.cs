using System;
using Hornung.ResourceToolkit.ResourceFileContent;

namespace Hornung.ResourceToolkit.Resolver
{
    public class ResourceResolveResult
    {
        readonly ResourceSetReference resourceSetReference;
        readonly string key;

        public ResourceSetReference ResourceSetReference {
            get { return this.resourceSetReference; }
        }

        public IResourceFileContent ResourceFileContent {
            get {
                if (this.ResourceSetReference == null ||
                    this.ResourceSetReference.FileName == null) {
                    return null;
                }
                return this.ResourceSetReference.ResourceFileContent;
            }
        }

        public virtual string Key {
            get { return this.key; }
        }

        public string FileName {
            get {
                IMultiResourceFileContent mrfc = this.ResourceFileContent as IMultiResourceFileContent;
                if (mrfc != null && this.Key != null) {
                    return mrfc.GetFileNameForKey(this.Key);
                } else if (this.ResourceFileContent != null) {
                    return this.ResourceFileContent.FileName;
                } else if (this.ResourceSetReference != null) {
                    return this.ResourceSetReference.FileName;
                }
                return null;
            }
        }

        public ResourceResolveResult(ResourceSetReference resourceSetReference, string key)
        {
            this.resourceSetReference = resourceSetReference;
            this.key = key;
        }
    }

    public sealed class ResourcePrefixResolveResult : ResourceResolveResult
    {
        public ResourcePrefixResolveResult(ResourceSetReference resourceSetReference, string prefix)
            : base(resourceSetReference, prefix)
        {
        }

        public override string Key {
            get { return null; }
        }

        public string Prefix {
            get { return base.Key; }
        }
    }
}
