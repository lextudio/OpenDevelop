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
    public sealed class TemplateDiscoveryService : IDisposable
    {
        readonly Bootstrapper _bootstrapper;

        public TemplateDiscoveryService()
            : this(OpenDevelopTemplateEngineHost.Create())
        {
        }

        public TemplateDiscoveryService(ITemplateEngineHost host)
        {
            if (host is null)
                throw new ArgumentNullException(nameof(host));

            _bootstrapper = new Bootstrapper(host, virtualizeConfiguration: false, loadDefaultComponents: true);
        }

        public void Dispose() => _bootstrapper.Dispose();

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
