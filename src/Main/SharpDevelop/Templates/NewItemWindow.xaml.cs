using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.SharpDevelop.Templates;

namespace ICSharpCode.SharpDevelop.Templates
{
    public sealed partial class NewItemWindow : Window
    {
        const string BundledTextTemplateIdentity = "OpenDevelop.Templates.TextTemplate.Item";
        readonly TemplateDiscoveryService _service;
        readonly string _targetDirectory;

        public IReadOnlyList<TemplateSummary> Templates { get; private set; }
            = Array.Empty<TemplateSummary>();

        public TemplateSummary? SelectedTemplate { get; private set; }

        public string ItemName => NameBox.Text.Trim();

        public IReadOnlyDictionary<string, string> AdditionalParameters => ParseParameters(ParametersBox.Text);

        NewItemWindow(TemplateDiscoveryService service, string targetDirectory)
        {
            InitializeComponent();
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _targetDirectory = targetDirectory ?? throw new ArgumentNullException(nameof(targetDirectory));
            StatusText.Text = "Loading templates...";
        }

        public static async Task<NewItemWindow?> ShowAsync(
            TemplateDiscoveryService service,
            string targetDirectory,
            Window owner)
        {
            var dialog = new NewItemWindow(service, targetDirectory);
            dialog.Owner = owner;

            try
            {
                var templates = await service.GetInstalledTemplatesAsync(CancellationToken.None);
                templates = await EnsureBundledTextTemplateInstalledAsync(service, templates);

                var itemTemplates = templates
                    .Where(t => t.Tags.TryGetValue("type", out var type)
                        && type.Equals("item", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                dialog.Templates = itemTemplates;
                dialog.TemplateList.ItemsSource = itemTemplates;

                dialog.StatusText.Text = itemTemplates.Length == 0
                    ? "No item templates found."
                    : $"{itemTemplates.Length} template(s) available.";
            }
            catch (Exception ex)
            {
                dialog.StatusText.Text = $"Failed to load templates: {ex.Message}";
            }

            dialog.ShowDialog();
            return dialog.DialogResult == true ? dialog : null;
        }

        static async Task<IReadOnlyList<TemplateSummary>> EnsureBundledTextTemplateInstalledAsync(
            TemplateDiscoveryService service,
            IReadOnlyList<TemplateSummary> templates)
        {
            if (templates.Any(t => string.Equals(t.Identity, BundledTextTemplateIdentity, StringComparison.Ordinal)))
                return templates;

            if (!TryResolveBundledTextTemplatePath(out var packagePath))
                return templates;

            try
            {
                var installed = await service.InstallTemplatePackageAsync(packagePath, CancellationToken.None);
                if (!installed)
                    return templates;

                return await service.GetInstalledTemplatesAsync(CancellationToken.None);
            }
            catch
            {
                return templates;
            }
        }

        static bool TryResolveBundledTextTemplatePath(out string packagePath)
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Templates", "Bundled", "TextTemplate"),
                Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "..", "..", "..", "..",
                    "Main", "SharpDevelop", "Templates", "Bundled", "TextTemplate"))
            };

            foreach (var candidate in candidates)
            {
                if (!Directory.Exists(candidate))
                    continue;

                var configPath = Path.Combine(candidate, ".template.config", "template.json");
                if (!File.Exists(configPath))
                    continue;

                packagePath = candidate;
                return true;
            }

            packagePath = string.Empty;
            return false;
        }

        void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            SelectedTemplate = TemplateList.SelectedItem as TemplateSummary;
            UpdateAddButton();
        }

        void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateAddButton();
        }

        void OnAddClick(object sender, RoutedEventArgs e)
        {
            if (SelectedTemplate is null || string.IsNullOrWhiteSpace(ItemName))
                return;

            DialogResult = true;
        }

        void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        void UpdateAddButton()
        {
            AddButton.IsEnabled = SelectedTemplate is not null
                && !string.IsNullOrWhiteSpace(ItemName);
        }

        static IReadOnlyDictionary<string, string> ParseParameters(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(text))
                return values;

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                    continue;

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim();
                if (key.Length == 0)
                    continue;

                values[key] = value;
            }

            return values;
        }
    }
}
