using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Project;

namespace Hornung.ResourceToolkit.Resolver
{
    public class ICSharpCodeCoreResourceResolver : AbstractResourceResolver
    {
        public ICSharpCodeCoreResourceResolver() : base()
        {
        }

        public override bool SupportsFile(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext == ".cs" || ext == ".vb" || ext == ".addin" || ext == ".xfrm" || ext == ".xml";
        }

        static readonly string[] possiblePatterns = new string[] {
            "${res:"
        };

        public override IEnumerable<string> GetPossiblePatternsForFile(string fileName)
        {
            if (this.SupportsFile(fileName)) {
                return possiblePatterns;
            }
            return new string[0];
        }

        public const string ResourceReferenceToken = @"${res:";

        public override ResourceResolveResult Resolve(ITextEditor editor, char? charTyped)
        {
            if (charTyped != null && charTyped != ':') {
                return null;
            }

            string text = editor.Document.Text;
            int offset = editor.Caret.Offset;

            if (offset <= 0 || offset > text.Length) return null;

            char ch = text[offset - 1];
            if (ch == '}' && offset > 1) {
                ch = text[--offset - 1];
            }
            while (offset > 1 && !Char.IsWhiteSpace(ch) && ch != '$' && ch != '}') {
                ch = text[--offset - 1];
            }

            if (offset + 5 >= text.Length || text.Substring(offset - 1, 6) != ResourceReferenceToken) {
                return null;
            }
            offset += 5;

            StringBuilder key = new StringBuilder();
            while (offset < text.Length && !Char.IsWhiteSpace(ch = text[offset++]) && ch != '}') {
                key.Append(ch);
            }
            if (ch != '}') {
                key = null;
            }

            ResourceSetReference resource = ResolveICSharpCodeCoreResourceSet(key == null ? null : key.ToString(), editor.FileName);

            return new ResourceResolveResult(resource, key == null ? null : key.ToString());
        }

        public static ResourceSetReference ResolveICSharpCodeCoreResourceSet(string key, string sourceFileName)
        {
            ResourceSetReference local = GetICSharpCodeCoreLocalResourceSet(sourceFileName);

            if (local.ResourceFileContent != null) {
                if (key != null) {
                    if (local.ResourceFileContent.ContainsKey(key)) {
                        return local;
                    }
                } else {
                    return local;
                }
            }

            ResourceSetReference host = GetICSharpCodeCoreHostResourceSet(sourceFileName);
            if (key != null) {
                if (host.ResourceFileContent != null) {
                    if (host.ResourceFileContent.ContainsKey(key)) {
                        return host;
                    }
                }
            }

            return local.ResourceFileContent == null ? host : local;
        }

        static string FindICSharpCodeCoreResourceFile(string path)
        {
            string file;
            foreach (string fileName in AddInTree.BuildItems<string>("/AddIns/ResourceToolkit/ICSharpCodeCoreResourceResolver/ResourceFileNames", null, false)) {
                if ((file = FindResourceFileName(Path.Combine(path, fileName))) != null) {
                    return file;
                }
            }
            return null;
        }

        public const string ICSharpCodeCoreLocalResourceSetName = "[ICSharpCodeCoreLocalResourceSet]";
        public const string ICSharpCodeCoreHostResourceSetName = "[ICSharpCodeCoreHostResourceSet]";

        static readonly ResourceSetReference EmptyLocalResourceSetReference = new ResourceSetReference(ICSharpCodeCoreLocalResourceSetName, null);
        static readonly ResourceSetReference EmptyHostResourceSetReference = new ResourceSetReference(ICSharpCodeCoreHostResourceSetName, null);

        public static ResourceSetReference GetICSharpCodeCoreLocalResourceSet(string sourceFileName)
        {
            IProject project = ProjectFileDictionaryService.GetProjectForFile(sourceFileName);
            if (project == null || String.IsNullOrEmpty(project.Directory)) {
                return EmptyLocalResourceSetReference;
            }

            string localFile;
            ResourceSetReference local = null;

            if (!RoslynAstCacheService.CacheEnabled || !cachedLocalResourceSets.TryGetValue(project, out local)) {
                foreach (string relativePath in AddInTree.BuildItems<string>("/AddIns/ResourceToolkit/ICSharpCodeCoreResourceResolver/LocalResourcesLocations", null, false)) {
                    if ((localFile = FindICSharpCodeCoreResourceFile(Path.GetFullPath(Path.Combine(project.Directory, relativePath)))) != null) {
                        local = new ResourceSetReference(ICSharpCodeCoreLocalResourceSetName, localFile);
                        if (RoslynAstCacheService.CacheEnabled) {
                            cachedLocalResourceSets.Add(project, local);
                        }
                        break;
                    }
                }
            }

            return local ?? EmptyLocalResourceSetReference;
        }

        public static ResourceSetReference GetICSharpCodeCoreHostResourceSet(string sourceFileName)
        {
            IProject project = ProjectFileDictionaryService.GetProjectForFile(sourceFileName);
            ResourceSetReference host = null;
            string hostFile;

            if (project == null ||
                !RoslynAstCacheService.CacheEnabled || !cachedHostResourceSets.TryGetValue(project, out host)) {

                string coreAssemblyFullPath = GetICSharpCodeCoreFullPath(project);

                if (coreAssemblyFullPath == null) {
                    if (ProjectService.OpenSolution != null) {
                        foreach (IProject p in ProjectService.OpenSolution.Projects) {
                            if ((coreAssemblyFullPath = GetICSharpCodeCoreFullPath(p)) != null) {
                                break;
                            }
                        }
                    }
                }

                if (coreAssemblyFullPath == null) {
                    return EmptyHostResourceSetReference;
                }

                foreach (string relativePath in AddInTree.BuildItems<string>("/AddIns/ResourceToolkit/ICSharpCodeCoreResourceResolver/HostResourcesLocations", null, false)) {
                    if ((hostFile = FindICSharpCodeCoreResourceFile(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(coreAssemblyFullPath), relativePath)))) != null) {
                        host = new ResourceSetReference(ICSharpCodeCoreHostResourceSetName, hostFile);
                        if (RoslynAstCacheService.CacheEnabled && project != null) {
                            cachedHostResourceSets.Add(project, host);
                        }
                        break;
                    }
                }
            }

            return host ?? EmptyHostResourceSetReference;
        }

        static string GetICSharpCodeCoreFullPath(IProject sourceProject)
        {
            if (sourceProject == null) {
                return null;
            }

            string coreAssemblyFullPath = null;

            if (sourceProject.Name.Equals("ICSharpCode.Core", StringComparison.OrdinalIgnoreCase)) {
                coreAssemblyFullPath = sourceProject.OutputAssemblyFullPath;
            } else {
                foreach (ProjectItem item in sourceProject.Items) {
                    ProjectReferenceProjectItem prpi = item as ProjectReferenceProjectItem;
                    if (prpi != null) {
                        if (prpi.ReferencedProject != null) {
                            if (prpi.ReferencedProject.Name.Equals("ICSharpCode.Core", StringComparison.OrdinalIgnoreCase) && prpi.ReferencedProject.OutputAssemblyFullPath != null) {
                                coreAssemblyFullPath = prpi.ReferencedProject.OutputAssemblyFullPath;
                                break;
                            }
                        }
                    }
                    ReferenceProjectItem rpi = item as ReferenceProjectItem;
                    if (rpi != null) {
                        if (rpi.Name.Equals("ICSharpCode.Core", StringComparison.OrdinalIgnoreCase) && rpi.FileName != null) {
                            coreAssemblyFullPath = rpi.FileName;
                            break;
                        }
                    }
                }
            }

            return coreAssemblyFullPath;
        }

        static Dictionary<IProject, ResourceSetReference> cachedLocalResourceSets;
        static Dictionary<IProject, ResourceSetReference> cachedHostResourceSets;

        static ICSharpCodeCoreResourceResolver()
        {
            cachedLocalResourceSets = new Dictionary<IProject, ResourceSetReference>();
            cachedHostResourceSets = new Dictionary<IProject, ResourceSetReference>();
            RoslynAstCacheService.CacheEnabledChanged += NRefactoryCacheEnabledChanged;
        }

        static void NRefactoryCacheEnabledChanged(object sender, EventArgs e)
        {
            if (!RoslynAstCacheService.CacheEnabled) {
                cachedLocalResourceSets.Clear();
                cachedHostResourceSets.Clear();
            }
        }
    }
}
