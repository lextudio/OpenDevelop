using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
    public sealed class LanguageServiceProjectSnapshot
    {
        public LanguageServiceProjectSnapshot(
            string projectFileName,
            string language,
            IReadOnlyList<string> documentFileNames,
            IReadOnlyList<string> metadataReferenceFileNames,
            IReadOnlyList<string> projectReferenceFileNames,
            IReadOnlyList<string> preprocessorSymbols,
            string? languageVersion,
            string? nullableContext,
            string? targetFramework = null,
            IReadOnlyList<string>? analyzerAssemblyFileNames = null)
        {
            ProjectFileName = projectFileName ?? throw new ArgumentNullException(nameof(projectFileName));
            Language = language ?? throw new ArgumentNullException(nameof(language));
            DocumentFileNames = documentFileNames ?? throw new ArgumentNullException(nameof(documentFileNames));
            MetadataReferenceFileNames = metadataReferenceFileNames ?? throw new ArgumentNullException(nameof(metadataReferenceFileNames));
            ProjectReferenceFileNames = projectReferenceFileNames ?? throw new ArgumentNullException(nameof(projectReferenceFileNames));
            PreprocessorSymbols = preprocessorSymbols ?? throw new ArgumentNullException(nameof(preprocessorSymbols));
            LanguageVersion = languageVersion;
            NullableContext = nullableContext;
            TargetFramework = targetFramework;
            AnalyzerAssemblyFileNames = analyzerAssemblyFileNames ?? Array.Empty<string>();
        }

        public string ProjectFileName { get; }
        public string Language { get; }
        public IReadOnlyList<string> DocumentFileNames { get; }
        public IReadOnlyList<string> MetadataReferenceFileNames { get; }
        public IReadOnlyList<string> ProjectReferenceFileNames { get; }
        public IReadOnlyList<string> PreprocessorSymbols { get; }
        public string? LanguageVersion { get; }
        public string? NullableContext { get; }

        /// <summary>
        /// The TFM this snapshot slice was evaluated for, or <see langword="null"/> for a
        /// single-targeted project (no slicing needed). See
        /// <see cref="FromProjectAllTargetFrameworks"/> for multi-targeted projects.
        /// </summary>
        public string? TargetFramework { get; }

        /// <summary>
        /// Resolved paths of `Analyzer` items — third-party Roslyn analyzer/source-generator
        /// assemblies from `PackageReference` analyzer assets (externals/OpenDevelop/doc/technotes/language-services.md §2.3).
        /// </summary>
        public IReadOnlyList<string> AnalyzerAssemblyFileNames { get; }

        public static IReadOnlyList<LanguageServiceProjectSnapshot> FromSolution(ISolution solution)
        {
            if (solution is null)
                throw new ArgumentNullException(nameof(solution));

            return solution.Projects
                .SelectMany(FromProjectAllTargetFrameworks)
                .ToArray();
        }

        /// <summary>
        /// Returns one snapshot per declared TFM for a multi-targeted project (externals/OpenDevelop/doc/technotes/language-services.md
        /// §4 slice 4), or a single snapshot (<see cref="TargetFramework"/> = <see langword="null"/>)
        /// for a single-targeted (or unrecognized) project.
        /// </summary>
        public static IReadOnlyList<LanguageServiceProjectSnapshot> FromProjectAllTargetFrameworks(IProject project)
        {
            if (project is null)
                throw new ArgumentNullException(nameof(project));

            var targetFrameworks = GetTargetFrameworks(project);
            return targetFrameworks.Count <= 1
                ? new[] { FromProject(project) }
                : targetFrameworks.Select(targetFramework => FromProject(project, targetFramework)).ToArray();
        }

        /// <summary>
        /// All TFMs a project declares (from evaluated <c>TargetFrameworks</c>, or a single-element
        /// list from evaluated <c>TargetFramework</c> for a single-targeted project).
        /// </summary>
        public static IReadOnlyList<string> GetTargetFrameworks(IProject project)
        {
            if (project is null)
                throw new ArgumentNullException(nameof(project));

            var msbuildProject = project as MSBuildBasedProject;
            var multiTargeted = msbuildProject?.GetEvaluatedProperty("TargetFrameworks");
            if (!string.IsNullOrWhiteSpace(multiTargeted))
                return SplitProperty(multiTargeted).ToArray();

            var singleTarget = msbuildProject?.GetEvaluatedProperty("TargetFramework");
            return string.IsNullOrWhiteSpace(singleTarget) ? Array.Empty<string>() : new[] { singleTarget };
        }

        public static LanguageServiceProjectSnapshot FromProject(IProject project) => FromProject(project, targetFramework: null);

        /// <summary>
        /// Builds a snapshot for one TFM slice of a multi-targeted project. When
        /// <paramref name="targetFramework"/> is given, item lists and properties are read from a
        /// dedicated <see cref="Microsoft.Build.Evaluation.Project"/> re-evaluated with the
        /// <c>TargetFramework</c> global property pinned to that value — the project's own
        /// <see cref="MSBuildBasedProject"/> evaluation is TFM-agnostic (it's the project-wide,
        /// "outer build" evaluation), so a real per-TFM slice needs its own evaluation rather than
        /// reusing that one. Falls back to the project-wide (unsliced) snapshot if re-evaluation
        /// fails, so a single bad TFM doesn't take down language services for the others.
        /// </summary>
        public static LanguageServiceProjectSnapshot FromProject(IProject project, string? targetFramework)
        {
            if (project is null)
                throw new ArgumentNullException(nameof(project));

            var projectFileName = project.FileName.ToString();
            var language = string.Equals(Path.GetExtension(projectFileName), ".vbproj", StringComparison.OrdinalIgnoreCase)
                ? "Visual Basic"
                : "C#";

            if (!string.IsNullOrWhiteSpace(targetFramework))
            {
                var sliced = TryEvaluateForTargetFramework(project, projectFileName, language, targetFramework);
                if (sliced is not null)
                    return sliced;
            }

            var msbuildProject = project as MSBuildBasedProject;

            var documents = GetCompileDocumentPaths(project, msbuildProject);

            var references = project.GetItemsOfType(ItemType.Reference)
                .Select(GetReferenceHintPath)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var projectReferences = project.GetItemsOfType(ItemType.ProjectReference)
                .Select(item => item.FileName?.ToString())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var analyzers = project.GetItemsOfType(new ItemType("Analyzer"))
                .Select(item => item.FileName?.ToString())
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new LanguageServiceProjectSnapshot(
                projectFileName,
                language,
                documents,
                references,
                projectReferences,
                SplitProperty(msbuildProject?.GetEvaluatedProperty("DefineConstants")).ToArray(),
                NullIfEmpty(msbuildProject?.GetEvaluatedProperty("LangVersion")),
                NullIfEmpty(msbuildProject?.GetEvaluatedProperty("Nullable")),
                NullIfEmpty(targetFramework),
                analyzers);
        }

        static LanguageServiceProjectSnapshot? TryEvaluateForTargetFramework(IProject project, string projectFileName, string language, string targetFramework)
        {
            var cached = TfmEvaluationCache.TryLoad(projectFileName, targetFramework);
            if (cached is not null)
                return cached;

            MSBuildInternals.InitializeMSBuildEnvironment();

            var collection = new Microsoft.Build.Evaluation.ProjectCollection();
            try
            {
                var evaluated = collection.LoadProject(
                    projectFileName,
                    new Dictionary<string, string> { ["TargetFramework"] = targetFramework },
                    toolsVersion: null);

                var projectDirectory = project.Directory.ToString();

                var documents = evaluated.GetItems("Compile")
                    .Select(item => ResolveFullPath(projectDirectory, item.EvaluatedInclude))
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var references = evaluated.GetItems("Reference")
                    .Select(item => GetReferenceHintPath(projectDirectory, item))
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var projectReferences = evaluated.GetItems("ProjectReference")
                    .Select(item => ResolveFullPath(projectDirectory, item.EvaluatedInclude))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var analyzers = evaluated.GetItems("Analyzer")
                    .Select(item => ResolveFullPath(projectDirectory, item.EvaluatedInclude))
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var snapshot = new LanguageServiceProjectSnapshot(
                    projectFileName,
                    language,
                    documents,
                    references,
                    projectReferences,
                    SplitProperty(evaluated.GetPropertyValue("DefineConstants")).ToArray(),
                    NullIfEmpty(evaluated.GetPropertyValue("LangVersion")),
                    NullIfEmpty(evaluated.GetPropertyValue("Nullable")),
                    targetFramework,
                    analyzers);

                TfmEvaluationCache.Save(projectFileName, targetFramework, snapshot);
                return snapshot;
            }
            catch (Exception ex)
            {
                ICSharpCode.Core.LoggingService.Warn(
                    $"Per-TFM evaluation failed for '{projectFileName}' ({targetFramework}); falling back to the project-wide snapshot: {ex.Message}");
                return null;
            }
            finally
            {
                collection.UnloadAllProjects();
                collection.Dispose();
            }
        }

        static string ResolveFullPath(string projectDirectory, string include)
        {
            return Path.IsPathRooted(include) ? include : Path.GetFullPath(Path.Combine(projectDirectory, include));
        }

        /// <summary>
        /// Resolves the project's actual Compile items. For SDK-style projects, <see cref="IProject.GetItemsOfType"/>
        /// only ever sees literal <c>&lt;Compile Include="..."/&gt;</c> entries in the .csproj/.vbproj
        /// XML - for the common case of a project relying purely on the SDK's implicit glob (no
        /// explicit Compile items at all), that's an empty or incomplete list, silently hiding
        /// whole files from GoToDefinition/Find References/Rename (see doc/opendevelop.md,
        /// ProjectDisplayItems has the same "SDK-style projects don't list their implicitly-globbed
        /// Compile items in p.Items" note for Solution Explorer's own tree). SD.MSBuildBasedProject.GetEvaluatedProjectItems()
        /// runs a real MSBuild evaluation and sees the glob-expanded item list, same as Solution
        /// Explorer already does via ProjectDisplayItems.GetEvaluatedProjectDisplayItems.
        /// </summary>
        static IReadOnlyList<string> GetCompileDocumentPaths(IProject project, MSBuildBasedProject? msbuildProject)
        {
            if (msbuildProject != null && msbuildProject.IsSdkStyleProject)
            {
                var projectDirectory = project.Directory.ToString();
                return msbuildProject.GetEvaluatedProjectItems()
                    .Where(item => string.Equals(item.ItemType, "Compile", StringComparison.OrdinalIgnoreCase))
                    .Select(item => ResolveFullPath(projectDirectory, item.EvaluatedInclude))
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            return project.GetItemsOfType(ItemType.Compile)
                .Select(item => item.FileName?.ToString())
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static string? GetReferenceHintPath(string projectDirectory, Microsoft.Build.Evaluation.ProjectItem item)
        {
            var hintPath = item.GetMetadataValue("HintPath");
            if (!string.IsNullOrWhiteSpace(hintPath))
                return ResolveFullPath(projectDirectory, hintPath);

            return Path.IsPathRooted(item.EvaluatedInclude) ? item.EvaluatedInclude : null;
        }

        static string? GetReferenceHintPath(ProjectItem item)
        {
            var hintPath = item.GetEvaluatedMetadata("HintPath");
            if (!string.IsNullOrWhiteSpace(hintPath))
            {
                return Path.IsPathRooted(hintPath)
                    ? hintPath
                    : Path.GetFullPath(Path.Combine(item.Project.Directory.ToString(), hintPath));
            }

            var include = item.Include;
            return Path.IsPathRooted(include) ? include : null;
        }

        static IEnumerable<string> SplitProperty(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;

            foreach (var part in value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                    yield return trimmed;
            }
        }

        static string? NullIfEmpty(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>
        /// Persists the result of <see cref="TryEvaluateForTargetFramework"/> - a real MSBuild
        /// design-time evaluation via a throwaway <see cref="Microsoft.Build.Evaluation.ProjectCollection"/>
        /// - across process restarts, in a `.od` folder next to the open solution (mirroring what
        /// Visual Studio's `.vs`/ComponentModelCache does for its own design-time build results).
        /// Invalidated by either: an edit to the .csproj/.vbproj itself (PackageReference bump, a
        /// new TargetFrameworks entry, ...), or any file under the project directory being newer
        /// than the cache entry - the latter is what actually matters for a project whose Compile
        /// items come entirely from the SDK's implicit glob (the common case): adding/removing a
        /// .cs file never touches the project file at all, so without this check a stale entry
        /// would silently hide (or dangle a reference to) a file forever, breaking Find
        /// References/Rename for it. Deliberately does not follow imported .props/.targets outside
        /// the project directory (e.g. a shared Directory.Build.props one level up); an edit there
        /// won't be picked up until the project file itself changes or the cache is cleared.
        /// </summary>
        static class TfmEvaluationCache
        {
            sealed class Entry
            {
                public long ProjectFileWriteTimeUtcTicks { get; set; }
                public string[] Documents { get; set; } = Array.Empty<string>();
                public string[] References { get; set; } = Array.Empty<string>();
                public string[] ProjectReferences { get; set; } = Array.Empty<string>();
                public string[] PreprocessorSymbols { get; set; } = Array.Empty<string>();
                public string? LanguageVersion { get; set; }
                public string? NullableContext { get; set; }
                public string[] AnalyzerAssemblyFileNames { get; set; } = Array.Empty<string>();
            }

            public static LanguageServiceProjectSnapshot? TryLoad(string projectFileName, string targetFramework)
            {
                var path = GetCacheFilePath(projectFileName, targetFramework);
                if (path is null || !File.Exists(path))
                    return null;

                try
                {
                    var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
                    if (entry is null)
                        return null;
                    if (File.GetLastWriteTimeUtc(projectFileName).Ticks != entry.ProjectFileWriteTimeUtcTicks)
                        return null;
                    if (ProjectTreeChangedSince(Path.GetDirectoryName(projectFileName)!, File.GetLastWriteTimeUtc(path)))
                        return null;

                    var language = string.Equals(Path.GetExtension(projectFileName), ".vbproj", StringComparison.OrdinalIgnoreCase)
                        ? "Visual Basic"
                        : "C#";
                    return new LanguageServiceProjectSnapshot(
                        projectFileName,
                        language,
                        entry.Documents,
                        entry.References,
                        entry.ProjectReferences,
                        entry.PreprocessorSymbols,
                        entry.LanguageVersion,
                        entry.NullableContext,
                        targetFramework,
                        entry.AnalyzerAssemblyFileNames);
                }
                catch (Exception ex)
                {
                    ICSharpCode.Core.LoggingService.Warn(
                        $"LanguageServiceProjectSnapshot: failed to load .od TFM cache for '{projectFileName}' ({targetFramework}), re-evaluating. {ex.Message}");
                    return null;
                }
            }

            public static void Save(string projectFileName, string targetFramework, LanguageServiceProjectSnapshot snapshot)
            {
                var path = GetCacheFilePath(projectFileName, targetFramework);
                if (path is null)
                    return;

                try
                {
                    var entry = new Entry
                    {
                        ProjectFileWriteTimeUtcTicks = File.GetLastWriteTimeUtc(projectFileName).Ticks,
                        Documents = snapshot.DocumentFileNames.ToArray(),
                        References = snapshot.MetadataReferenceFileNames.ToArray(),
                        ProjectReferences = snapshot.ProjectReferenceFileNames.ToArray(),
                        PreprocessorSymbols = snapshot.PreprocessorSymbols.ToArray(),
                        LanguageVersion = snapshot.LanguageVersion,
                        NullableContext = snapshot.NullableContext,
                        AnalyzerAssemblyFileNames = snapshot.AnalyzerAssemblyFileNames.ToArray(),
                    };
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(entry));
                }
                catch (Exception ex)
                {
                    ICSharpCode.Core.LoggingService.Warn(
                        $"LanguageServiceProjectSnapshot: failed to write .od TFM cache for '{projectFileName}' ({targetFramework}). {ex.Message}");
                }
            }

            static string? GetCacheFilePath(string projectFileName, string targetFramework)
            {
                var solutionFileName = SD.ProjectService.CurrentSolution?.FileName;
                if (solutionFileName is null)
                    return null;

                var cacheDirectory = Path.Combine(solutionFileName.GetParentDirectory().ToString(), ".od", "roslyn-tfm-cache");
                var key = $"{projectFileName}|{targetFramework}";
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
                return Path.Combine(cacheDirectory, hash + ".json");
            }

            /// <summary>
            /// True if any file under <paramref name="projectDirectory"/> (excluding build output/
            /// cache directories, which churn on every build and never affect Compile-item
            /// evaluation) has a later write time than <paramref name="cacheWriteTimeUtc"/> - i.e. a
            /// file was added, removed, or edited since the cache entry was written. A plain
            /// filesystem stat walk, not a full evaluation, so it stays far cheaper than the
            /// MSBuild+Roslyn work it's guarding even though it isn't itself cached.
            /// </summary>
            static bool ProjectTreeChangedSince(string projectDirectory, DateTime cacheWriteTimeUtc)
            {
                try
                {
                    foreach (var directory in EnumerateRelevantDirectories(projectDirectory))
                    {
                        if (Directory.GetLastWriteTimeUtc(directory) > cacheWriteTimeUtc)
                            return true;
                        foreach (var file in Directory.EnumerateFiles(directory))
                        {
                            if (File.GetLastWriteTimeUtc(file) > cacheWriteTimeUtc)
                                return true;
                        }
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    ICSharpCode.Core.LoggingService.Warn(
                        $"LanguageServiceProjectSnapshot: failed to check project tree freshness under '{projectDirectory}', re-evaluating. {ex.Message}");
                    return true;
                }
            }

            static readonly string[] ExcludedDirectoryNames = { "bin", "obj", ".od", ".git", ".vs" };

            /// <summary>
            /// Manually recurses (rather than SearchOption.AllDirectories) so an excluded directory
            /// - most importantly "obj", which NuGet/MSBuild can fill with thousands of restore/
            /// intermediate files - is never descended into at all, not just filtered out afterward.
            /// </summary>
            static IEnumerable<string> EnumerateRelevantDirectories(string root)
            {
                yield return root;
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    foreach (var directory in Directory.EnumerateDirectories(current))
                    {
                        if (ExcludedDirectoryNames.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
                            continue;
                        yield return directory;
                        pending.Push(directory);
                    }
                }
            }
        }
    }
}
