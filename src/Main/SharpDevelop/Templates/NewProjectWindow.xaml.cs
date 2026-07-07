using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.SharpDevelop.Templates;

namespace ICSharpCode.SharpDevelop.Templates
{
    public sealed partial class NewProjectWindow : Window
    {
        readonly TemplateDiscoveryService _service;
        readonly string _defaultLocation;

        public IReadOnlyList<TemplateSummary> Templates { get; private set; }
            = Array.Empty<TemplateSummary>();

        public TemplateSummary? SelectedTemplate { get; private set; }

        public string ProjectName => NameBox.Text.Trim();

        public string Location => LocationBox.Text.Trim();

        public IReadOnlyDictionary<string, string> AdditionalParameters => ParseParameters(ParametersBox.Text);

        NewProjectWindow(TemplateDiscoveryService service, string defaultLocation)
        {
            InitializeComponent();
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _defaultLocation = defaultLocation ?? throw new ArgumentNullException(nameof(defaultLocation));
            LocationBox.Text = defaultLocation;
            StatusText.Text = "Loading templates...";
        }

        public static async Task<NewProjectWindow?> ShowAsync(
            TemplateDiscoveryService service,
            string defaultLocation,
            Window owner)
        {
            var dialog = new NewProjectWindow(service, defaultLocation);
            dialog.Owner = owner;

            try
            {
                var templates = await service.GetInstalledTemplatesAsync(CancellationToken.None);

                var projectTemplates = templates
                    .Where(t => !t.Tags.TryGetValue("type", out var type)
                        || !type.Equals("item", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                dialog.Templates = projectTemplates;
                dialog.TemplateList.ItemsSource = projectTemplates;

                dialog.StatusText.Text = projectTemplates.Length == 0
                    ? "No project templates found."
                    : $"{projectTemplates.Length} template(s) available.";
            }
            catch (Exception ex)
            {
                dialog.StatusText.Text = $"Failed to load templates: {ex.Message}";
            }

            dialog.ShowDialog();
            return dialog.DialogResult == true ? dialog : null;
        }

        void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            SelectedTemplate = TemplateList.SelectedItem as TemplateSummary;
            UpdateCreateButton();
        }

        void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateCreateButton();
        }

        void OnLocationChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateCreateButton();
        }

        void OnCreateClick(object sender, RoutedEventArgs e)
        {
            if (SelectedTemplate is null || string.IsNullOrWhiteSpace(ProjectName)
                || string.IsNullOrWhiteSpace(Location))
                return;

            DialogResult = true;
        }

        void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        void UpdateCreateButton()
        {
            CreateButton.IsEnabled = SelectedTemplate is not null
                && !string.IsNullOrWhiteSpace(ProjectName)
                && !string.IsNullOrWhiteSpace(Location);
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
