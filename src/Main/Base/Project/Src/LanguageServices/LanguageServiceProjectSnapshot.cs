using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// assemblies from `PackageReference` analyzer assets (docs/language-services.md §2.3).
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
        /// Returns one snapshot per declared TFM for a multi-targeted project (docs/language-services.md
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

            var documents = project.GetItemsOfType(ItemType.Compile)
                .Select(item => item.FileName?.ToString())
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

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

                return new LanguageServiceProjectSnapshot(
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
    }
}
