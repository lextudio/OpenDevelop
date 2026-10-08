#nullable enable
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
    public static class LanguageServiceProjectSnapshotFactory
    {

        public static IReadOnlyList<LanguageServiceProjectSnapshot> FromSolution(ISolution solution)
        {
            if (solution is null)
                throw new ArgumentNullException(nameof(solution));

            // Most of the time per project is MSBuild's ResolveReferences in a child dotnet
            // process (ReferenceResolutionCache misses, and every project whose resolution fails).
            // Those children are independent, so run a few projects at once; the in-process
            // evaluation reads still serialize on MSBuildInternals' lock. Ordered, so the push
            // order (and with it which project becomes ready first) is unchanged.
            var projects = solution.Projects.ToArray();
            batchedReferencePaths.Value = ResolveReferencePathsInOneProcess(projects);
            try
            {
                return projects
                .AsParallel()
                .AsOrdered()
                .WithDegreeOfParallelism(Math.Max(1, Math.Min(4, Environment.ProcessorCount)))
                .SelectMany(FromProjectAllTargetFrameworks)
                .Select(snapshot => snapshot.WithSolutionDirectory(solution.Directory.ToString()))
                .ToArray();
            }
            finally
            {
                batchedReferencePaths.Value = null;
            }
        }

        /// <summary>
        /// References resolved ahead of the per-project snapshots by one MSBuild process for every
        /// project that misses the reference cache (IBatchAssemblyReferenceResolver), consumed by
        /// <see cref="ResolveReferencePaths"/>. AsyncLocal so it reaches FromSolution's PLINQ workers
        /// and nothing else.
        /// </summary>
        static readonly System.Threading.AsyncLocal<IReadOnlyDictionary<MSBuildBasedProject, IReadOnlyList<string>>?> batchedReferencePaths = new();

        static IReadOnlyDictionary<MSBuildBasedProject, IReadOnlyList<string>>? ResolveReferencePathsInOneProcess(IReadOnlyList<IProject> projects)
        {
            if (SD.GetService<IMSBuildEngine>() is not IBatchAssemblyReferenceResolver resolver)
                return null;
            // Only what FromProject would resolve itself: single-target MSBuild projects whose
            // resolution is not already cached. Multi-targeted ones evaluate per TFM instead.
            var misses = projects
                .OfType<MSBuildBasedProject>()
                .Where(project => GetTargetFrameworks(project).Count <= 1)
                .Where(project => ReferenceResolutionCache.Fingerprint(project) is not { } fingerprint
                                  || ReferenceResolutionCache.Peek(project, fingerprint) is null)
                .ToList();
            if (misses.Count < 2)
                return null; // one project: a batch is no cheaper than resolving it directly
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var resolved = resolver.ResolveAssemblyReferencePaths(misses);
            ICSharpCode.Core.LoggingService.Info($"LanguageServiceProjectSnapshot: resolved references of {resolved.Count}/{misses.Count} projects in one MSBuild process in {watch.ElapsedMilliseconds} ms.");
            return resolved;
        }

        /// <summary>Identifies one snapshot (a project, or one TFM of a multi-targeted project).</summary>
        public static string SnapshotKey(LanguageServiceProjectSnapshot snapshot) =>
            SnapshotKey(snapshot.ProjectFileName, snapshot.TargetFramework);

        static string SnapshotKey(string projectFileName, string? targetFramework) =>
            projectFileName.ToUpperInvariant() + "|" + targetFramework;

        /// <summary>The keys <see cref="FromSolution"/> would produce, read from the projects'
        /// existing evaluation without building any snapshot.</summary>
        public static ISet<string> CurrentSnapshotKeys(ISolution solution)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in solution.Projects)
            {
                var targetFrameworks = GetTargetFrameworks(project);
                if (targetFrameworks.Count <= 1)
                    keys.Add(SnapshotKey(project.FileName.ToString(), null));
                else
                    foreach (var targetFramework in targetFrameworks)
                        keys.Add(SnapshotKey(project.FileName.ToString(), targetFramework));
            }
            return keys;
        }

        /// <summary>
        /// The snapshots <see cref="FromSolution"/> produced the last time this solution was opened,
        /// or null. Opening pushes these to the Roslyn host at once, so the language service is
        /// usable before the fresh snapshots are built; the fresh ones then replace whichever
        /// differ (C# Dev Kit's "every load after the first is served from the cache",
        /// doc/technotes/fast-mode.md). They can be stale for those seconds, never afterwards.
        /// </summary>
        public static IReadOnlyList<LanguageServiceProjectSnapshot>? TryLoadSolutionSnapshots(ISolution solution)
        {
            var path = SolutionSnapshotsPath(solution);
            try
            {
                if (path is null || !File.Exists(path))
                    return null;
                var snapshots = JsonSerializer.Deserialize<LanguageServiceProjectSnapshot[]>(File.ReadAllText(path));
                // Pushing a snapshot that names a deleted document breaks that project's outline
                // until the fresh one replaces it; leave such a project to the fresh snapshot.
                return snapshots?.Where(snapshot => snapshot.DocumentFileNames.All(File.Exists)).ToArray();
            }
            catch (Exception ex)
            {
                ICSharpCode.Core.LoggingService.Warn($"LanguageServiceProjectSnapshot: ignoring unreadable solution snapshot cache '{path}'. {ex.Message}");
                return null;
            }
        }

        public static void SaveSolutionSnapshots(ISolution solution, IReadOnlyList<LanguageServiceProjectSnapshot> snapshots)
        {
            var path = SolutionSnapshotsPath(solution);
            if (path is null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshots));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex)
            {
                ICSharpCode.Core.LoggingService.Warn($"LanguageServiceProjectSnapshot: failed to write the solution snapshot cache '{path}'. {ex.Message}");
            }
        }

        static string? SolutionSnapshotsPath(ISolution solution)
        {
            var solutionFile = solution?.FileName?.ToString();
            if (solutionFile is null)
                return null;
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(solutionFile).ToUpperInvariant())));
            return Path.Combine(Path.GetDirectoryName(solutionFile)!, ".od", "roslyn-solution-snapshots", "v1-" + name + ".json");
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
            var snapshots = targetFrameworks.Count <= 1
                ? new[] { FromProject(project) }
                : targetFrameworks.Select(targetFramework => FromProject(project, targetFramework)).ToArray();
            return snapshots.Select(snapshot => snapshot.WithSolutionDirectory(project.ParentSolution?.Directory.ToString())).ToArray();
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

            // Declared <Reference> items PLUS whatever MSBuild's ResolveReferences target resolves.
            //
            // The declared items alone are close to nothing for an SDK-style project: a
            // <PackageReference> only becomes a concrete assembly path after RAR runs, so a project
            // whose dependencies all come from NuGet contributed no references at all here. Roslyn
            // then fell back to the host runtime's trusted platform assemblies
            // (RoslynWorkspaceHelper.GetMetadataReferences) and compiled against ~3 references -
            // measured on tests/fixtures/SampleTestProject, where every xunit type was reported as
            // CS0246 and every symbol query over the project answered "found nothing" rather than
            // failing. Both sources are unioned rather than one replacing the other: a hand-written
            // <Reference HintPath="..."/> to a loose assembly is still legitimate, and RAR does not
            // always run (an unrestored project resolves nothing and must degrade to the old
            // behaviour, not to an exception).
            var references = project.GetItemsOfType(ItemType.Reference)
                .Select(GetReferenceHintPath)
                .Concat(ResolveReferencePaths(project))
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

        /// <summary>
        /// Assembly paths from MSBuild's own reference resolution, or nothing when it is
        /// unavailable. Never throws: an unrestored or unresolvable project must degrade to the
        /// declared references rather than break project loading for the whole solution.
        /// </summary>
        static IEnumerable<string?> ResolveReferencePaths(IProject project)
        {
            if (project is not MSBuildBasedProject msbuildProject)
                return Array.Empty<string?>();
            var fingerprint = ReferenceResolutionCache.Fingerprint(msbuildProject);
            if (fingerprint is not null && ReferenceResolutionCache.TryLoad(msbuildProject, fingerprint) is { } cached)
                return cached;
            if (batchedReferencePaths.Value is { } batch && batch.TryGetValue(msbuildProject, out var batched))
            {
                var paths = batched.Select(path => (string?)path).ToArray();
                if (fingerprint is not null)
                    ReferenceResolutionCache.Save(msbuildProject, fingerprint, paths);
                return paths;
            }
            try
            {
                var engine = SD.GetService<IMSBuildEngine>();
                if (engine == null)
                    return Array.Empty<string?>();
                var resolved = engine.ResolveAssemblyReferences(msbuildProject)
                    .Select(GetReferenceHintPath)
                    .ToArray();
                if (fingerprint is not null)
                    ReferenceResolutionCache.Save(msbuildProject, fingerprint, resolved);
                return resolved;
            }
            catch (Exception ex)
            {
                ICSharpCode.Core.LoggingService.Warn(
                    $"LanguageServiceProjectSnapshot: reference resolution failed for '{project.FileName}'. {ex.Message}");
                return Array.Empty<string?>();
            }
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

        /// <summary>Reference-resolution cache hits and misses since the IDE started, for
        /// od.perf.timeline.</summary>
        public static string ReferenceCacheStatistics =>
            $"reference cache {ReferenceResolutionCache.Hits} hit(s), {ReferenceResolutionCache.Misses} miss(es)";

        /// <summary>
        /// Persists MSBuild's ResolveReferences result per project. Resolving it costs a child
        /// `dotnet msbuild` process (~0.7 s per project, the bulk of opening a solution: 62 s of 138 s
        /// for 87 projects, doc/technotes/fast-mode.md), and its answer can only change when an input
        /// of that evaluation does. So the key is those inputs: the SDK the child runs under, the
        /// project file, every import its evaluation read (Directory.Build.*, Directory.Packages.props,
        /// NuGet's generated nuget.g.props/targets, the SDK's own targets) and the restore output
        /// project.assets.json. Files inside the solution directory are keyed by content, so a touched
        /// but unchanged file is still a hit; files outside it (SDK installs, the NuGet package cache)
        /// are immutable once installed and are keyed by path, size and write time, so they are not
        /// read on every open. Nothing here is a timestamp of the cache entry itself.
        /// </summary>
        static class ReferenceResolutionCache
        {
            internal static int Hits, Misses;

            sealed class Entry
            {
                public string Fingerprint { get; set; } = "";
                public string?[] ReferencePaths { get; set; } = Array.Empty<string?>();
            }

            public static string? Fingerprint(MSBuildBasedProject project)
            {
                try
                {
                    var solutionDirectory = project.ParentSolution?.Directory.ToString();
                    var inputs = project.GetEvaluationInputFiles().ToList();
                    var assetsFile = project.GetEvaluatedProperty("ProjectAssetsFile");
                    if (!string.IsNullOrWhiteSpace(assetsFile))
                        inputs.Add(assetsFile);

                    var sdk = Project.Sdk.DotNetSdkService.ResolveEffectiveSdk();
                    var text = new StringBuilder();
                    text.Append("v1|").Append(sdk.DotnetExecutablePath).Append('|').Append(sdk.HighestSdkVersion).Append('\n');
                    foreach (var input in inputs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        text.Append(input).Append('|');
                        var file = new FileInfo(input);
                        if (!file.Exists)
                            text.Append("absent");
                        else if (solutionDirectory is not null && IsUnder(input, solutionDirectory))
                            text.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))));
                        else
                            text.Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks);
                        text.Append('\n');
                    }
                    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
                }
                catch (Exception ex)
                {
                    ICSharpCode.Core.LoggingService.Warn(
                        $"LanguageServiceProjectSnapshot: cannot fingerprint '{project.FileName}', resolving references without the cache. {ex.Message}");
                    return null;
                }
            }

            /// <summary>Like <see cref="TryLoad"/>, without counting a hit or a miss.</summary>
            public static string?[]? Peek(MSBuildBasedProject project, string fingerprint)
            {
                var path = GetCacheFilePath(project);
                try
                {
                    if (path is null || !File.Exists(path))
                        return null;
                    var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
                    return entry is not null && entry.Fingerprint == fingerprint ? entry.ReferencePaths : null;
                }
                catch
                {
                    return null;
                }
            }

            public static string?[]? TryLoad(MSBuildBasedProject project, string fingerprint)
            {
                var path = GetCacheFilePath(project);
                try
                {
                    if (path is not null && File.Exists(path))
                    {
                        var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
                        if (entry is not null && entry.Fingerprint == fingerprint)
                        {
                            System.Threading.Interlocked.Increment(ref Hits);
                            return entry.ReferencePaths;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ICSharpCode.Core.LoggingService.Warn(
                        $"LanguageServiceProjectSnapshot: unreadable reference cache for '{project.FileName}', resolving again. {ex.Message}");
                }
                System.Threading.Interlocked.Increment(ref Misses);
                return null;
            }

            public static void Save(MSBuildBasedProject project, string fingerprint, string?[] referencePaths)
            {
                // An empty resolution is what an unrestored (or mid-restore) project produces. Caching
                // it would pin "no references" until an input changed; resolve again next time instead.
                // (Same rule, and the same measured failure, as TfmEvaluationCache.TryLoad.)
                if (!referencePaths.Any(path => !string.IsNullOrWhiteSpace(path)))
                    return;
                var path = GetCacheFilePath(project);
                if (path is null)
                    return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temporary = path + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new Entry { Fingerprint = fingerprint, ReferencePaths = referencePaths }));
                    File.Move(temporary, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    ICSharpCode.Core.LoggingService.Warn(
                        $"LanguageServiceProjectSnapshot: failed to write the reference cache for '{project.FileName}'. {ex.Message}");
                }
            }

            static string? GetCacheFilePath(MSBuildBasedProject project)
            {
                var solutionDirectory = project.ParentSolution?.Directory.ToString();
                if (solutionDirectory is null)
                    return null;
                // Named by the project's path relative to the solution, so the entry survives the
                // solution folder being moved or checked out elsewhere (the fingerprint decides
                // whether it still applies there).
                var relative = Path.GetRelativePath(solutionDirectory, project.FileName.ToString()).Replace('\\', '/');
                var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative.ToUpperInvariant())));
                return Path.Combine(solutionDirectory, ".od", "roslyn-reference-cache", name + ".json");
            }

            static bool IsUnder(string path, string directory)
            {
                var relative = Path.GetRelativePath(directory, path);
                return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
            }
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
                    // An entry with no metadata references at all is never a legitimate evaluation
                    // result - every C#/VB project resolves at least the framework reference set -
                    // so it can only have been written from an evaluation that ran before MSBuild
                    // could resolve anything (a project opened while a restore/build was still in
                    // flight, typically). Serving it is worse than re-evaluating: the Roslyn project
                    // it produces compiles nothing, so every symbol query over it answers "found
                    // nothing" instead of failing, and the cache key (project-file and project-tree
                    // write times) does not change afterwards, so the empty answer sticks for as
                    // long as the file sits on disk. Measured: OpenLens rendered "0 references" for
                    // symbols with obvious callers because SampleTestProject's cached entry held
                    // References: [] and 5 documents.
                    if (entry.References.Length == 0)
                    {
                        ICSharpCode.Core.LoggingService.Warn(
                            $"LanguageServiceProjectSnapshot: discarding .od TFM cache for '{projectFileName}' ({targetFramework}) - it holds no metadata references, which cannot be a real evaluation. Re-evaluating.");
                        return null;
                    }

                    // A document that no longer exists means the entry was written from an
                    // evaluation that still listed it (a file created and deleted around the write;
                    // the directory-time check above cannot tell). Roslyn then fails reading it and
                    // the whole project loses outline/folding (VSEditor tests' scratch files did).
                    if (entry.Documents.Any(document => !File.Exists(document)))
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
