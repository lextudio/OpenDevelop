#nullable enable
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
            await EnsureSdkBundledTemplatesInstalledAsync(cancellationToken);
            var templates = await _bootstrapper.GetTemplatesAsync(cancellationToken);

            // SDK previews and servicing installations can coexist in the template engine's
            // package cache. They may expose the same template identity several times, although
            // creation resolves that identity to one current winner. Do the same at our DTO
            // boundary so a project picker never presents indistinguishable duplicate choices.
            return templates
                .Select(template => new TemplateSummary(
                    template.Identity,
                    template.ShortNameList.FirstOrDefault() ?? template.Identity,
                    template.Name,
                    template.Description,
                    template.TagsCollection ?? new Dictionary<string, string>()))
                .GroupBy(summary => summary.Identity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(summary => summary.ShortName, StringComparer.OrdinalIgnoreCase)
                    .First())
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
            IReadOnlyDictionary<string, string?>? parameters,
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
                parameters ?? new Dictionary<string, string?>(),
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
            IReadOnlyDictionary<string, string?>? parameters,
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
                parameters ?? new Dictionary<string, string?>(),
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

        bool _sdkTemplatesChecked;

        /// <summary>
        /// Registers the template packages bundled with the .NET SDK (console, classlib, webapi,
        /// wpf, winforms, ...) into this host's settings store.
        ///
        /// This is NOT done by <see cref="Bootstrapper"/>, despite what the loadDefaultComponents
        /// flag suggests: that flag registers the *components* that can read template packages, not
        /// any packages. <c>dotnet new</c> gets the built-ins by scanning the SDK's own
        /// <c>templates/&lt;version&gt;</c> folder, and a host that does not do the same sees only
        /// whatever the user explicitly installed. The symptom is badly misleading - discovery
        /// succeeds and returns a plausible list (here: 42 templates, all Avalonia/MAUI/macOS from
        /// past explicit installs) with every Microsoft template silently absent, so a New Project
        /// dialog looks populated while missing its entire default catalogue.
        ///
        /// Idempotent and cheap after the first call: the engine skips packages already present in
        /// the store, and this only re-scans once per instance. Failures are deliberately
        /// swallowed - a missing or unreadable SDK template folder must degrade to "only the
        /// user's own templates", never break discovery outright.
        /// </summary>
        async Task EnsureSdkBundledTemplatesInstalledAsync(CancellationToken cancellationToken)
        {
            if (_sdkTemplatesChecked)
                return;
            _sdkTemplatesChecked = true;

            try
            {
                var packages = EnumerateSdkTemplatePackages().ToArray();
                if (packages.Length == 0)
                    return;

                await _bootstrapper.InstallTemplatePackagesAsync(
                    packages.Select(p => new InstallRequest(p)).ToArray(),
                    InstallationScope.Global,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // See above: discovery must still return the user's own templates.
            }
        }

        /// <summary>
        /// The <c>.nupkg</c>s under the running SDK's <c>templates/&lt;version&gt;</c> folder.
        /// </summary>
        static IEnumerable<string> EnumerateSdkTemplatePackages()
        {
            var root = FindDotnetRoot();
            if (root is null)
                return Array.Empty<string>();

            var templatesRoot = Path.Combine(root, "templates");
            if (!Directory.Exists(templatesRoot))
                return Array.Empty<string>();

            // One folder per SDK feature band; take them all rather than trying to match the
            // running SDK's version string, whose format has changed between releases.
            return Directory.EnumerateDirectories(templatesRoot)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.nupkg"));
        }

        /// <summary>
        /// The directory holding the SDK's shared folders (<c>sdk</c>, <c>templates</c>, ...).
        /// DOTNET_ROOT wins when set; otherwise this walks up from the runtime directory, which
        /// sits at <c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;</c>. Deriving it
        /// from the process path does not work for a host launched by anything other than
        /// <c>dotnet</c>, and on Homebrew the <c>dotnet</c> on PATH is a symlink into
        /// <c>libexec</c>, so the resolved runtime location is the dependable anchor.
        /// </summary>
        static string? FindDotnetRoot()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(fromEnvironment) && Directory.Exists(fromEnvironment))
                return fromEnvironment;

            var directory = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            for (var i = 0; i < 3 && directory is not null; i++)
                directory = directory.Parent;

            return directory?.Exists == true ? directory.FullName : null;
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
