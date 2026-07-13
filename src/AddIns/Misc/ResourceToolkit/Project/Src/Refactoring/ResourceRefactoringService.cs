using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hornung.ResourceToolkit.Resolver;
using Hornung.ResourceToolkit.ResourceFileContent;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Refactoring;
using ICSharpCode.SharpDevelop.Workbench;

namespace Hornung.ResourceToolkit.Refactoring
{
    public static class ResourceRefactoringService
    {
        public static List<Reference> FindReferences(string resourceFileName, string key, IProgressMonitor monitor)
        {
            return FindReferences(new SpecificResourceReferenceFinder(resourceFileName, key), monitor, SearchScope.WholeSolution);
        }

        public static List<Reference> FindReferences(IResourceReferenceFinder finder, IProgressMonitor monitor, SearchScope scope)
        {
            if (finder == null) {
                throw new ArgumentNullException("finder");
            }

            DateTime startTime = DateTime.UtcNow;
            List<Reference> references = new List<Reference>();

            try {
                RoslynAstCacheService.EnableCache();

                ICollection<string> files = GetPossibleFiles(scope);

                if (monitor != null) {
                    monitor.TaskName = StringParser.Parse("${res:SharpDevelop.Refactoring.FindingReferences}");
                }
                double workDone = 0;
                foreach (string fileName in files) {
                    if (monitor != null)
                        monitor.Progress = workDone / files.Count;
                    workDone += 1;
                    if (monitor != null && monitor.CancellationToken.IsCancellationRequested) {
                        return null;
                    }

                    string fileContent;
                    try {
                        fileContent = File.ReadAllText(fileName);
                    } catch {
                        continue;
                    }
                    if (String.IsNullOrEmpty(fileContent)) {
                        continue;
                    }

                    int pos = -1;
                    while ((pos = finder.GetNextPossibleOffset(fileName, fileContent, pos)) >= 0) {
                        ResourceResolveResult rrr = new ResourceResolveResult(null, null);

                        if (rrr != null && rrr.ResourceFileContent != null) {
                            if (finder.IsReferenceToResource(rrr)) {
                                if (rrr.Key != null) {
                                    string keyString = rrr.Key;
                                    int keyPos = fileContent.IndexOf(keyString, pos, StringComparison.OrdinalIgnoreCase);
                                    if (keyPos < pos) {
                                        keyPos = FindStringLiteral(fileName, fileContent, rrr.Key, pos, out keyString);
                                    }
                                    if (keyPos >= pos) {
                                        references.Add(new Reference(fileName, keyPos, keyString.Length, keyString, rrr));
                                    }
                                } else {
                                    references.Add(new Reference(fileName, pos, 0, null, rrr));
                                }
                            }
                        }
                    }
                }

                LoggingService.Info("ResourceToolkit: FindReferences finished in " + (DateTime.UtcNow - startTime).TotalSeconds.ToString(System.Globalization.CultureInfo.CurrentCulture) + "s");

            } finally {
                RoslynAstCacheService.DisableCache();
            }

            return references;
        }

        public static List<Reference> FindAllReferences(IProgressMonitor monitor, SearchScope scope)
        {
            return FindReferences(new AnyResourceReferenceFinder(), monitor, scope);
        }

        public static List<Reference> FindReferencesToMissingKeys(IProgressMonitor monitor, SearchScope scope)
        {
            List<Reference> references = FindAllReferences(monitor, scope);
            if (references == null) {
                return null;
            }
            return references.FindAll(IsReferenceToMissingKey);
        }

        public static bool IsReferenceToMissingKey(Reference reference)
        {
            ResourceResolveResult rrr = reference.ResolveResult as ResourceResolveResult;
            if (rrr == null || rrr.Key == null) {
                return false;
            }
            if (rrr.ResourceFileContent == null) {
                return true;
            }
            return !rrr.ResourceFileContent.ContainsKey(rrr.Key);
        }

        public static ICollection<ResourceItem> FindUnusedKeys(IProgressMonitor monitor)
        {
            List<Reference> references = FindAllReferences(monitor, SearchScope.WholeSolution);
            if (references == null) {
                return null;
            }

            List<ResourceItem> unused = new List<ResourceItem>();

            Dictionary<string, List<string>> referencedKeys = new Dictionary<string, List<string>>();
            Dictionary<string, List<string>> referencedPrefixes = new Dictionary<string, List<string>>();
            foreach (Reference reference in references) {
                ResourceResolveResult rrr = (ResourceResolveResult)reference.ResolveResult;
                if (rrr.ResourceFileContent != null) {
                    string fileName = rrr.FileName;
                    if (!referencedKeys.ContainsKey(fileName)) {
                        referencedKeys.Add(fileName, new List<string>());
                        referencedPrefixes.Add(fileName, new List<string>());
                    }
                    if (rrr.Key != null && !referencedKeys[fileName].Contains(rrr.Key)) {
                        referencedKeys[fileName].Add(rrr.Key);
                    } else {
                        ResourcePrefixResolveResult rprr = rrr as ResourcePrefixResolveResult;
                        if (rprr != null && rprr.Prefix != null && !referencedPrefixes[fileName].Contains(rprr.Prefix)) {
                            referencedPrefixes[fileName].Add(rprr.Prefix);
                        }
                    }
                } else {
                    if (monitor != null) monitor.ShowingDialog = true;
                    MessageService.ShowWarning("Found a resource reference that could not be resolved." + Environment.NewLine + (reference.FileName ?? "<null>") + ":" + reference.Offset + Environment.NewLine + "Expression: " + (reference.Expression ?? "<null>"));
                    if (monitor != null) monitor.ShowingDialog = false;
                }
            }

            foreach (string fileName in referencedKeys.Keys) {
                foreach (KeyValuePair<string, object> entry in ResourceFileContentRegistry.GetResourceFileContent(fileName).Data) {
                    if (!referencedKeys[fileName].Contains(entry.Key) &&
                        !referencedPrefixes[fileName].Any(prefix => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) {
                        unused.Add(new ResourceItem(fileName, entry.Key));
                    }
                }
            }

            return unused.AsReadOnly();
        }

        public static void Rename(ResourceResolveResult rrr)
        {
            string newKey = MessageService.ShowInputBox("${res:SharpDevelop.Refactoring.Rename}", "${res:Hornung.ResourceToolkit.RenameResourceText}", rrr.Key);
            if (!String.IsNullOrEmpty(newKey) && !newKey.Equals(rrr.Key)) {
                Rename(rrr, newKey, null);
            }
        }

        public static void Rename(ResourceResolveResult rrr, string newKey, IProgressMonitor monitor)
        {
            if (rrr.ResourceFileContent.ContainsKey(newKey)) {
                if (monitor != null) monitor.ShowingDialog = true;
                MessageService.ShowWarning("${res:Hornung.ResourceToolkit.EditStringResourceDialog.DuplicateKey}");
                if (monitor != null) monitor.ShowingDialog = false;
                return;
            }

            List<Reference> references = FindReferences(rrr.FileName, rrr.Key, monitor);
            if (references == null) {
                return;
            }

            try {
                if (rrr.ResourceFileContent.ContainsKey(rrr.Key)) {
                    rrr.ResourceFileContent.RenameKey(rrr.Key, newKey);
                } else {
                    if (monitor != null) monitor.ShowingDialog = true;
                    MessageService.ShowWarning("${res:Hornung.ResourceToolkit.RenameKeyDefinitionNotFoundWarning}");
                    if (monitor != null) monitor.ShowingDialog = false;
                }
            } catch (Exception ex) {
                if (monitor != null) monitor.ShowingDialog = true;
                MessageService.ShowWarningFormatted("${res:Hornung.ResourceToolkit.ErrorProcessingResourceFile}" + Environment.NewLine + ex.Message, rrr.ResourceFileContent.FileName);
                if (monitor != null) monitor.ShowingDialog = false;
                return;
            }

            FindReferencesAndRenameHelper.RunFindReferences((ICSharpCode.TypeSystem.IEntity)null);

            foreach (KeyValuePair<string, IResourceFileContent> entry in ResourceFileContentRegistry.GetLocalizedContents(rrr.FileName)) {
                try {
                    if (entry.Value.ContainsKey(rrr.Key)) {
                        entry.Value.RenameKey(rrr.Key, newKey);
                    }
                } catch (Exception ex) {
                    if (monitor != null) monitor.ShowingDialog = true;
                    MessageService.ShowWarningFormatted("${res:Hornung.ResourceToolkit.ErrorProcessingResourceFile}" + Environment.NewLine + ex.Message, entry.Value.FileName);
                    if (monitor != null) monitor.ShowingDialog = false;
                }
            }
        }

        public static ICollection<string> GetPossibleFiles(SearchScope scope)
        {
            List<string> files = new List<string>();

            switch (scope) {
                case SearchScope.WholeSolution:
                    var s = ProjectService.OpenSolution;
                    if (s == null) {
                        throw new InvalidOperationException("Cannot search in whole solution when no solution is open.");
                    }
                    AddFilesFromSolution(files, s);
                    break;

                case SearchScope.CurrentProject:
                    IProject p = ProjectService.CurrentProject;
                    if (p == null) {
                        throw new InvalidOperationException("Cannot search in current project when no project is active.");
                    }
                    AddFilesFromProject(files, p);
                    break;

                case SearchScope.CurrentFile:
                    IViewContent vc = WorkbenchSingleton.Workbench.ActiveViewContent;
                    if (vc == null) {
                        throw new InvalidOperationException("Cannot search in current file when no file is open.");
                    }
                    AddFilesFromViewContent(files, vc);
                    break;

                case SearchScope.OpenFiles:
                    foreach (IViewContent v in WorkbenchSingleton.Workbench.ViewContentCollection) {
                        AddFilesFromViewContent(files, v);
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException("scope", "The scope parameter is not set to one of the SearchScope values.");
            }

            return files.AsReadOnly();
        }

        static void AddFilesFromSolution(IList<string> files, ISolution s)
        {
            foreach (IProject project in s.Projects) {
                AddFilesFromProject(files, project);
            }
        }

        static void AddFilesFromProject(IList<string> files, IProject p)
        {
            foreach (ProjectItem pi in p.Items) {
                if (pi is FileProjectItem) {
                    string name = pi.FileName;
                    if (IsPossibleFile(name)) {
                        files.Add(name);
                        ProjectFileDictionaryService.AddFile(name, p);
                    }
                }
            }
        }

        static void AddFilesFromViewContent(IList<string> files, IViewContent vc)
        {
            files.AddRange(vc.Files
                           .Select(f => f.FileName.ToString())
                           .Where(name => name != null && IsPossibleFile(name))
                          );
        }

        public static bool IsPossibleFile(string name)
        {
            return ResourceResolverService.Resolvers.Any(resolver => resolver.SupportsFile(name));
        }

        public static int FindStringLiteral(string fileName, string fileContent, string literal, int startOffset, out string code)
        {
            code = "\"" + literal + "\"";
            string unquoted = literal;
            int index = fileContent.IndexOf(unquoted, startOffset, StringComparison.OrdinalIgnoreCase);
            return index;
        }
    }

    public enum SearchScope
    {
        WholeSolution,
        CurrentProject,
        CurrentFile,
        OpenFiles
    }

    public class Reference
    {
        public string FileName { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; }
        public string Expression { get; set; }
        public object ResolveResult { get; set; }

        public Reference(string fileName, int offset, int length, string expression, object resolveResult)
        {
            FileName = fileName;
            Offset = offset;
            Length = length;
            Expression = expression;
            ResolveResult = resolveResult;
        }
    }
}
