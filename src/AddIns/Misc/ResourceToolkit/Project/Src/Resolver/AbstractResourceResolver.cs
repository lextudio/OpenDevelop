using System;
using System.Collections.Generic;
using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;

namespace Hornung.ResourceToolkit.Resolver
{
    public abstract class AbstractResourceResolver : IResourceResolver
    {
        public virtual ResourceResolveResult Resolve(ITextEditor editor, char? charTyped)
        {
            if (editor == null || editor.Document == null) {
                LoggingService.Debug("ResourceToolkit: "+this.GetType().ToString()+".Resolve called with null editor");
                return null;
            }
            return null;
        }

        public abstract bool SupportsFile(string fileName);

        public abstract IEnumerable<string> GetPossiblePatternsForFile(string fileName);

        protected static string FindResourceFileName(string fileName)
        {
            string f;
            if (File.Exists(f = Path.ChangeExtension(fileName, ".resources"))) {
                return f;
            }
            if (File.Exists(f = Path.ChangeExtension(fileName, ".resx"))) {
                return f;
            }
            return null;
        }

        protected AbstractResourceResolver()
        {
        }
    }
}
