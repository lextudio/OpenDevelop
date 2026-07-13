using System;
using System.Collections.Generic;
using System.IO;
using Hornung.ResourceToolkit.ResourceFileContent;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Project;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Hornung.ResourceToolkit.Resolver
{
    public class RoslynResourceResolver : AbstractResourceResolver
    {
        public const string RoslynResourceResolversAddInTreePath = "/AddIns/ResourceToolkit/NRefactoryResourceResolver/Resolvers";

        static List<IRoslynResourceResolver> resolvers;

        public static IEnumerable<IRoslynResourceResolver> Resolvers {
            get {
                if (resolvers == null) {
                    resolvers = AddInTree.BuildItems<IRoslynResourceResolver>(RoslynResourceResolversAddInTreePath, null, false);
                }
                return resolvers;
            }
        }

        public static void SetResourceResolversListUnitTestOnly(IEnumerable<IRoslynResourceResolver> resolversToSet)
        {
            resolvers = new List<IRoslynResourceResolver>(resolversToSet);
        }

        public RoslynResourceResolver() : base()
        {
        }

        public override bool SupportsFile(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".vb", StringComparison.OrdinalIgnoreCase);
        }

        public override IEnumerable<string> GetPossiblePatternsForFile(string fileName)
        {
            if (this.SupportsFile(fileName)) {
                List<string> patterns = new List<string>();
                foreach (IRoslynResourceResolver resolver in Resolvers) {
                    foreach (string pattern in resolver.GetPossiblePatternsForFile(fileName)) {
                        if (!patterns.Contains(pattern)) {
                            patterns.Add(pattern);
                        }
                    }
                }
                return patterns;
            }
            return new string[0];
        }

        public override ResourceResolveResult Resolve(ITextEditor editor, char? charTyped)
        {
            string fileName = editor.FileName;
            if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) {
                return null;
            }

            string fileContent = editor.Document.Text;
            if (string.IsNullOrEmpty(fileContent)) {
                return null;
            }

            SyntaxTree tree;
            try {
                tree = RoslynAstCacheService.GetSyntaxTree(fileName, fileContent);
            } catch {
                return null;
            }
            if (tree == null) return null;

            var root = tree.GetRoot();
            var position = editor.Caret.Offset;
            var token = root.FindToken(position);
            var node = token.Parent;

            if (node == null) return null;

            foreach (IRoslynResourceResolver resolver in Resolvers) {
                var result = resolver.Resolve(node, null, fileName, fileContent, charTyped);
                if (result != null) {
                    return result;
                }
            }

            return null;
        }

        public static ResourceSetReference GetResourceSetReference(string sourceFileName, string resourceName)
        {
            if (resourceName == null) {
                throw new ArgumentNullException("resourceName");
            }

            IProject p = ProjectFileDictionaryService.GetProjectForFile(sourceFileName);

            if (p != null) {
                string fileName;
                if ((fileName = TryGetResourceFileNameFromProjectDirect(resourceName, p)) != null) {
                    return new ResourceSetReference(resourceName, fileName);
                }
            }

            if (sourceFileName != null) {
                string directory = Path.GetDirectoryName(sourceFileName);
                string resourcePart = resourceName;
                string fileName;

                while (true) {
                    if ((fileName = FindResourceFileName(Path.Combine(directory, resourcePart.Replace('.', Path.DirectorySeparatorChar)))) != null) {
                        return new ResourceSetReference(resourceName, fileName);
                    }
                    if ((fileName = FindResourceFileName(Path.Combine(directory, resourcePart))) != null) {
                        return new ResourceSetReference(resourceName, fileName);
                    }

                    if (resourcePart.Contains(".")) {
                        resourcePart = resourcePart.Substring(resourcePart.IndexOf('.') + 1);
                    } else {
                        break;
                    }
                }
            }

            return new ResourceSetReference(resourceName, null);
        }

        static string TryGetResourceFileNameFromProjectDirect(string resourceName, IProject p)
        {
            if (!resourceName.StartsWith(p.RootNamespace + ".", StringComparison.OrdinalIgnoreCase)) {
                return null;
            }

            resourceName = resourceName.Substring(p.RootNamespace.Length + 1);

            foreach (ProjectItem item in p.Items) {
                FileProjectItem fpi = item as FileProjectItem;
                if (fpi == null) continue;

                string virtualName = fpi.VirtualName;
                if (String.IsNullOrEmpty(virtualName)) continue;

                int lastDotIndex = virtualName.LastIndexOf('.');
                if (lastDotIndex == -1) continue;

                if (virtualName.Substring(0, lastDotIndex).Replace('\\', '.').Equals(resourceName, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(fpi.FileName) &&
                    ResourceFileContentRegistry.GetResourceFileContentFactory(fpi.FileName) != null) {
                    return fpi.FileName;
                }
            }

            return null;
        }
    }
}
