#nullable enable
using System;
using ICSharpCode.Core;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Templates;
using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.SharpDevelop.Templates
{
    [CLSCompliant(false)]
    public sealed partial class NewItemWindow : Window
    {
        sealed record TemplateOptionChoice(string Key, string DisplayName);
        const string BundledTextTemplateIdentity = "OpenDevelop.Templates.TextTemplate.Item";
        const string AllCategories = "All item types";
        const string LastCategoryKey = "Dialogs.NewFileDialog.LastSelectedCategory";
        const string LastTemplateKey = "Dialogs.NewFileDialog.LastSelectedTemplate";
        readonly TemplateDiscoveryService _service;
        readonly string _targetDirectory;
        readonly Dictionary<string, string?> _templateOptionValues = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<TemplateSummary> Templates { get; private set; }
            = Array.Empty<TemplateSummary>();

        public TemplateSummary? SelectedTemplate { get; private set; }

        public string ItemName => NameBox.Text.Trim();

        public IReadOnlyDictionary<string, string?> AdditionalParameters
        {
            get
            {
                var parameters = new Dictionary<string, string?>(_templateOptionValues, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in ParseParameters(ParametersBox.Text))
                    parameters[pair.Key] = pair.Value;
                return parameters;
            }
        }

        NewItemWindow(TemplateDiscoveryService service, string targetDirectory)
        {
            InitializeComponent();
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _targetDirectory = targetDirectory ?? throw new ArgumentNullException(nameof(targetDirectory));
            StatusText.Text = ResourceService.GetString("NewProject.Status.LoadingTemplates");
            CreatePathText.Text = ResourceService.GetString("NewProject.CreateIn") + targetDirectory;
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
                AddCategoryTreeItems(dialog.CategoryTree, itemTemplates.SelectMany(t => t.Categories));
                dialog.RestoreLastSelection();
                dialog.ApplyFilter();

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
            UpdateTemplateDescription();
            RenderTemplateOptions();
            UpdateAddButton();
        }

        void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateAddButton();
            CreatePathText.Text = string.IsNullOrWhiteSpace(ItemName)
                ? "Create in: " + _targetDirectory
                : "Create in: " + Path.Combine(_targetDirectory, ItemName);
        }

        void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

        void OnCategoryChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => ApplyFilter();

        void ApplyFilter()
        {
            var query = SearchBox.Text.Trim();
            var category = (CategoryTree.SelectedItem as System.Windows.Controls.TreeViewItem)?.Tag as string;
            var filtered = Templates.Where(t => string.IsNullOrEmpty(category) || category == AllCategories
                || t.Categories.Any(templateCategory => templateCategory.Equals(category, StringComparison.OrdinalIgnoreCase)
                    || templateCategory.StartsWith(category + "/", StringComparison.OrdinalIgnoreCase)));
            var matching = string.IsNullOrEmpty(query) ? filtered.ToArray() : filtered.Where(t =>
                Contains(t.Name, query) || Contains(t.ShortName, query) || Contains(t.Description, query)
                || t.Tags.Any(tag => Contains(tag.Key, query) || Contains(tag.Value, query))).ToArray();
            TemplateList.ItemsSource = matching;
            if (TemplateList.Tag is string lastTemplate && TemplateList.SelectedItem is null)
                TemplateList.SelectedItem = matching.FirstOrDefault(template => string.Equals(template.Identity, lastTemplate, StringComparison.OrdinalIgnoreCase));
        }

        void RestoreLastSelection()
        {
            var lastCategory = SD.PropertyService.Get(LastCategoryKey, AllCategories);
            foreach (var item in CategoryTree.Items.OfType<System.Windows.Controls.TreeViewItem>())
            {
                if (string.Equals(item.Tag as string, lastCategory, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsSelected = true;
                    break;
                }
            }
            TemplateList.Tag = SD.PropertyService.Get(LastTemplateKey, string.Empty);
        }

        static void AddCategoryTreeItems(System.Windows.Controls.TreeView tree, IEnumerable<string> categories)
        {
            tree.Items.Add(new System.Windows.Controls.TreeViewItem { Header = ResourceService.GetString("NewItem.AllTypes"), Tag = AllCategories, IsSelected = true });
            var nodes = new Dictionary<string, System.Windows.Controls.TreeViewItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in categories.Where(category => !string.IsNullOrWhiteSpace(category))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(category => category, StringComparer.OrdinalIgnoreCase))
            {
                System.Windows.Controls.ItemsControl parent = tree;
                var path = string.Empty;
                foreach (var segment in category.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    path = string.IsNullOrEmpty(path) ? segment : path + "/" + segment;
                    if (!nodes.TryGetValue(path, out var node))
                    {
                        node = new System.Windows.Controls.TreeViewItem { Header = segment, Tag = path };
                        parent.Items.Add(node);
                        nodes.Add(path, node);
                    }
                    parent = node;
                }
            }
        }

        void UpdateTemplateDescription()
        {
            if (SelectedTemplate is null)
            {
                TemplateTitleText.Text = ResourceService.GetString("NewItem.TemplateDescriptionHint");
                TemplateDescriptionText.Text = string.Empty;
                TemplateDetailsText.Text = string.Empty;
                return;
            }

            TemplateTitleText.Text = SelectedTemplate.DisplayName;
            TemplateDescriptionText.Text = SelectedTemplate.Description ?? "No description was supplied by this template.";
            TemplateDetailsText.Text = ResourceService.GetString("NewItem.ShortName") + SelectedTemplate.ShortName;
        }

        void RenderTemplateOptions()
        {
            TemplateOptionsPanel.Children.Clear();
            _templateOptionValues.Clear();
            if (SelectedTemplate is null)
                return;

            foreach (var option in SelectedTemplate.TemplateParameters.Where(IsUserVisibleTemplateOption))
            {
                var panel = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 5) };
                panel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(185) });
                panel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                panel.Children.Add(new System.Windows.Controls.Label { Content = option.DisplayName + ":", ToolTip = option.Description, Padding = new Thickness(0, 2, 8, 2), VerticalContentAlignment = VerticalAlignment.Center });

                if (option.Choices is { Count: > 0 })
                {
                    var combo = new System.Windows.Controls.ComboBox { Tag = option.Name, ToolTip = option.Description, DisplayMemberPath = nameof(TemplateOptionChoice.DisplayName), SelectedValuePath = nameof(TemplateOptionChoice.Key), ItemsSource = option.Choices.Select(choice => new TemplateOptionChoice(choice.Key, choice.Value)).OrderBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray() };
                    combo.SelectedValue = option.DefaultValue;
                    if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
                    combo.SelectionChanged += OnTemplateOptionChanged;
                    System.Windows.Controls.Grid.SetColumn(combo, 1);
                    panel.Children.Add(combo);
                    _templateOptionValues[option.Name] = combo.SelectedValue as string;
                }
                else if (option.DataType.Equals("bool", StringComparison.OrdinalIgnoreCase) || option.DataType.Equals("boolean", StringComparison.OrdinalIgnoreCase))
                {
                    var checkBox = new System.Windows.Controls.CheckBox { Tag = option.Name, ToolTip = option.Description, VerticalAlignment = VerticalAlignment.Center, IsChecked = bool.TryParse(option.DefaultValue, out var value) && value };
                    checkBox.Checked += OnTemplateOptionChanged;
                    checkBox.Unchecked += OnTemplateOptionChanged;
                    System.Windows.Controls.Grid.SetColumn(checkBox, 1);
                    panel.Children.Add(checkBox);
                    _templateOptionValues[option.Name] = checkBox.IsChecked == true ? "true" : "false";
                }
                else
                {
                    var textBox = new System.Windows.Controls.TextBox { Tag = option.Name, ToolTip = option.Description, Text = option.DefaultValue ?? string.Empty };
                    textBox.TextChanged += OnTemplateOptionChanged;
                    System.Windows.Controls.Grid.SetColumn(textBox, 1);
                    panel.Children.Add(textBox);
                    _templateOptionValues[option.Name] = textBox.Text;
                }
                TemplateOptionsPanel.Children.Add(panel);
            }
        }

        static bool IsUserVisibleTemplateOption(TemplateParameterSummary parameter) =>
            !parameter.IsName
            && !parameter.Name.Equals("language", StringComparison.OrdinalIgnoreCase)
            && !parameter.Name.Equals("type", StringComparison.OrdinalIgnoreCase);

        void OnTemplateOptionChanged(object sender, RoutedEventArgs e)
        {
            switch (sender)
            {
                case System.Windows.Controls.ComboBox combo when combo.Tag is string name: _templateOptionValues[name] = combo.SelectedValue as string; break;
                case System.Windows.Controls.CheckBox checkBox when checkBox.Tag is string name: _templateOptionValues[name] = checkBox.IsChecked == true ? "true" : "false"; break;
                case System.Windows.Controls.TextBox textBox when textBox.Tag is string name: _templateOptionValues[name] = textBox.Text; break;
            }
        }

        static bool Contains(string? value, string query) =>
            value?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        void OnAddClick(object sender, RoutedEventArgs e)
        {
            if (SelectedTemplate is null || string.IsNullOrWhiteSpace(ItemName))
                return;

            if (ItemName is "." or ".." || ItemName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                StatusText.Text = ResourceService.GetString("NewItem.InvalidItemName");
                return;
            }

            SD.PropertyService.Set(LastCategoryKey,
                (CategoryTree.SelectedItem as System.Windows.Controls.TreeViewItem)?.Tag as string ?? AllCategories);
            SD.PropertyService.Set(LastTemplateKey, SelectedTemplate.Identity);
            this.CloseDialog(true);
        }

        void OnCancelClick(object sender, RoutedEventArgs e)
        {
            this.CloseDialog(false);
        }

        void UpdateAddButton()
        {
            AddButton.IsEnabled = SelectedTemplate is not null
                && !string.IsNullOrWhiteSpace(ItemName);
        }

        static IReadOnlyDictionary<string, string?> ParseParameters(string text)
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

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
