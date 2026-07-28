using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.Installer;
using Microsoft.TemplateEngine.Edge.Settings;
using Microsoft.TemplateEngine.Edge.Template;
using Microsoft.TemplateEngine.IDE;

namespace ICSharpCode.SharpDevelop.Templates
{
    /// <summary>
    /// Discovers installed file/project templates (externals/OpenDevelop/doc/technotes/template-system.md slice 1) via
    /// <c>Microsoft.TemplateEngine.IDE</c>'s <see cref="Bootstrapper"/> — the same high-level
    /// entry point real IDE hosts use (it wraps <c>EngineEnvironmentSettings</c> and registers
    /// the default generator/provider components itself, which is what actually makes the
    /// built-in .NET SDK templates show up — hand-constructing
    /// <c>EngineEnvironmentSettings</c> directly finds nothing without also replicating that
    /// component registration).
    /// </summary>
    public sealed class TemplateDiscoveryService : IDisposable
    {
        readonly Bootstrapper _bootstrapper;

        // "unodevelop"/"opendevelop": purely informational (identifies the calling host to
        // Microsoft.TemplateEngine, e.g. in its own logs) - previously two near-identical
        // *TemplateEngineHost classes differing only in this string, now just an #if branch here.
        public TemplateDiscoveryService()
            : this(TemplateEngineHost.Create(
#if HAS_UNO
                "unodevelop"
#else
                "opendevelop"
#endif
            ))
        {
        }

        public TemplateDiscoveryService(ITemplateEngineHost host)
        {
            if (host is null)
                throw new ArgumentNullException(nameof(host));

            _bootstrapper = new Bootstrapper(host, virtualizeConfiguration: false, loadDefaultComponents: true);
        }

        public void Dispose() => _bootstrapper.Dispose();

        /// <summary>
        /// Discovers all installed templates (slice 1).
        /// </summary>
        public async Task<IReadOnlyList<TemplateSummary>> GetInstalledTemplatesAsync(CancellationToken cancellationToken)
        {
            var templates = await _bootstrapper.GetTemplatesAsync(cancellationToken);

            return templates
                .Select(template => new TemplateSummary(
                    template.Identity,
                    template.ShortNameList.FirstOrDefault() ?? template.Identity,
                    template.Name,
                    template.Description,
                    template.TagsCollection ?? new Dictionary<string, string>()))
                .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Instantiates a template into the specified output directory (slice 2).
        /// </summary>
        /// <param name="template">The template to instantiate (from a previous discovery call).</param>
        /// <param name="name">The name for the template (equivalent to <c>dotnet new &lt;template&gt; --name &lt;name&gt;</c>).</param>
        /// <param name="outputPath">The directory to generate files into.</param>
        /// <param name="parameters">Optional template parameter overrides (key = parameter name, value = parameter value).</param>
        /// <param name="cancellationToken">A cancellation token to cancel the asynchronous operation.</param>
        /// <returns>A <see cref="TemplateInstantiationResult"/> describing success/failure and the generated files.</returns>
        public async Task<TemplateInstantiationResult> InstantiateAsync(
            TemplateSummary template,
            string name,
            string outputPath,
            IReadOnlyDictionary<string, string>? parameters,
            CancellationToken cancellationToken)
        {
            if (template is null)
                throw new ArgumentNullException(nameof(template));
            if (name is null)
                throw new ArgumentNullException(nameof(name));
            if (outputPath is null)
                throw new ArgumentNullException(nameof(outputPath));

            var info = await FindTemplateAsync(template.Identity, cancellationToken);
            if (info is null)
            {
                return new TemplateInstantiationResult(
                    Success: false,
                    ErrorMessage: $"Template '{template.Identity}' not found.",
                    OutputDirectory: outputPath,
                    PrimaryOutputPaths: Array.Empty<string>());
            }

            var result = await _bootstrapper.CreateAsync(
                info,
                name,
                outputPath,
                parameters ?? new Dictionary<string, string>(),
                baselineName: null,
                cancellationToken);

            return MapResult(result, outputPath);
        }

        /// <summary>
        /// Dry-runs a template instantiation — returns the same result shape as
        /// <see cref="InstantiateAsync"/> but does not generate any files (slice 2).
        /// </summary>
        public async Task<TemplateInstantiationResult> GetCreationEffectsAsync(
            TemplateSummary template,
            string name,
            string outputPath,
            IReadOnlyDictionary<string, string>? parameters,
            CancellationToken cancellationToken)
        {
            if (template is null)
                throw new ArgumentNullException(nameof(template));
            if (name is null)
                throw new ArgumentNullException(nameof(name));
            if (outputPath is null)
                throw new ArgumentNullException(nameof(outputPath));

            var info = await FindTemplateAsync(template.Identity, cancellationToken);
            if (info is null)
            {
                return new TemplateInstantiationResult(
                    Success: false,
                    ErrorMessage: $"Template '{template.Identity}' not found.",
                    OutputDirectory: outputPath,
                    PrimaryOutputPaths: Array.Empty<string>());
            }

            var result = await _bootstrapper.GetCreationEffectsAsync(
                info,
                name,
                outputPath,
                parameters ?? new Dictionary<string, string>(),
                baselineName: null,
                cancellationToken);

            return MapResult(result, outputPath);
        }

        /// <summary>
        /// Installs a template package from a folder, NuGet package, or NuGet feed (slice 2).
        /// </summary>
        /// <param name="packageIdentifier">
        /// The template package to install. Supported formats:
        /// <list type="bullet">
        ///   <item><description>Path to a folder containing <c>.template.config/template.json</c></description></item>
        ///   <item><description>Path to a <c>.nupkg</c> file</description></item>
        ///   <item><description>NuGet package ID (e.g. <c>"Microsoft.Maui.Templates"</c>)</description></item>
        /// </list>
        /// </param>
        /// <param name="cancellationToken">A cancellation token to cancel the asynchronous operation.</param>
        /// <returns>True if the package was installed successfully.</returns>
        public async Task<bool> InstallTemplatePackageAsync(string packageIdentifier, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(packageIdentifier))
                throw new ArgumentException("Package identifier is required.", nameof(packageIdentifier));

            var request = new InstallRequest(packageIdentifier);
            var results = await _bootstrapper.InstallTemplatePackagesAsync(
                new[] { request },
                InstallationScope.Global,
                cancellationToken);

            return results.Count > 0 && results[0].Success;
        }

        async Task<ITemplateInfo?> FindTemplateAsync(string identity, CancellationToken cancellationToken)
        {
            var templates = await _bootstrapper.GetTemplatesAsync(cancellationToken);
            return templates.FirstOrDefault(t => t.Identity == identity);
        }

        static TemplateInstantiationResult MapResult(ITemplateCreationResult result, string fallbackOutputPath)
        {
            var outputDir = result.OutputBaseDirectory ?? fallbackOutputPath;

            // Primary outputs come from the actual creation result, or from the dry-run
            // effects (which are created prior to instantiation and preserved in the result).
            var primaryOutputs = result.CreationResult?.PrimaryOutputs
                ?? result.CreationEffects?.CreationResult?.PrimaryOutputs;

            var paths = primaryOutputs?
                .Select(p => Path.GetFullPath(Path.Combine(outputDir, p.Path)))
                .ToArray() ?? Array.Empty<string>();

            return new TemplateInstantiationResult(
                Success: result.Status == CreationResultStatus.Success,
                ErrorMessage: result.ErrorMessage,
                OutputDirectory: outputDir,
                PrimaryOutputPaths: paths);
        }
    }
}
