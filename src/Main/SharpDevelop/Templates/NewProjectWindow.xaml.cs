#nullable enable
using System;
using ICSharpCode.Core;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Templates;
using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.SharpDevelop.Templates
{
    [CLSCompliant(false)]
    public sealed partial class NewProjectWindow : Window
    {
        readonly TemplateDiscoveryService _service;
        readonly string _defaultLocation;
        readonly bool _createNewSolution;
        readonly Dictionary<string, string?> _templateOptionValues = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<TemplateSummary> Templates { get; private set; }
            = Array.Empty<TemplateSummary>();

        public TemplateSummary? SelectedTemplate { get; private set; }
        const string AllCategories = "All project types";
        const string DefaultPathKey = "ICSharpCode.SharpDevelop.Gui.Dialogs.NewProjectDialog.DefaultPath";
        const string LastCategoryKey = "Dialogs.NewProjectDialog.LastSelectedCategory";
        const string LastTemplateKey = "Dialogs.NewProjectDialog.LastSelectedTemplate";
        const string CreateSolutionDirectoryKey = "Dialogs.NewProjectDialog.CreateDirectoryForSolution";

        sealed class TemplateChoice
        {
            public TemplateChoice(IEnumerable<TemplateSummary> variants)
            {
                Variants = variants.OrderBy(PreferredLanguageOrder).ThenBy(t => t.Language, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            public IReadOnlyList<TemplateSummary> Variants { get; }
            public TemplateSummary DefaultTemplate => Variants[0];
            public string DisplayName => DefaultTemplate.Name;
            public string? Description => DefaultTemplate.Description;

            static int PreferredLanguageOrder(TemplateSummary template) =>
                string.Equals(template.Language, "C#", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        sealed record TemplateOptionChoice(string Key, string DisplayName);

        public string ProjectName => NameBox.Text.Trim();

        public string Location => LocationBox.Text.Trim();
        public string SolutionName => SolutionNameBox.Text.Trim();
        public bool CreateSolutionDirectory => CreateSolutionDirectoryBox.IsChecked == true;

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

        NewProjectWindow(TemplateDiscoveryService service, string defaultLocation, bool createNewSolution)
        {
            InitializeComponent();
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _defaultLocation = defaultLocation ?? throw new ArgumentNullException(nameof(defaultLocation));
            _createNewSolution = createNewSolution;
            LocationBox.Text = createNewSolution ? SD.PropertyService.Get(DefaultPathKey, defaultLocation) : defaultLocation;
            SolutionNameBox.Text = "";
            CreateSolutionDirectoryBox.IsChecked = SD.PropertyService.Get(CreateSolutionDirectoryKey, true);
            Title = createNewSolution ? "New Solution" : "Add New Project";
            SolutionNameLabel.Visibility = createNewSolution ? Visibility.Visible : Visibility.Collapsed;
            SolutionNameBox.Visibility = createNewSolution ? Visibility.Visible : Visibility.Collapsed;
            CreateSolutionDirectoryBox.Visibility = createNewSolution ? Visibility.Visible : Visibility.Collapsed;
            UpdateCreatePath();
            StatusText.Text = ResourceService.GetString("NewProject.Status.LoadingTemplates");
        }

        public static async Task<NewProjectWindow?> ShowAsync(
            TemplateDiscoveryService service,
            string defaultLocation,
            bool createNewSolution,
            Window owner)
        {
            var dialog = new NewProjectWindow(service, defaultLocation, createNewSolution);
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
                AddCategoryTreeItems(dialog.CategoryTree, projectTemplates.SelectMany(t => t.Categories));
                dialog.RestoreLastSelection();
                dialog.ApplyFilter();
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
            var choice = TemplateList.SelectedItem as TemplateChoice;
            if (choice is null)
            {
                SelectedTemplate = null;
                LanguageBox.ItemsSource = null;
            }
            else
            {
                LanguageBox.ItemsSource = choice.Variants;
                LanguageBox.DisplayMemberPath = nameof(TemplateSummary.Language);
                LanguageBox.SelectedItem = choice.DefaultTemplate;
                SelectedTemplate = choice.DefaultTemplate;
            }
            UpdateTemplateDescription();
            RenderTemplateOptions();
            UpdateCreateButton();
        }

		void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
		{
			ApplyFilter();
		}

        void OnCategoryChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            ApplyFilter();
        }

        void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (LanguageBox.SelectedItem is TemplateSummary template)
                SelectedTemplate = template;
            UpdateTemplateDescription();
            UpdateCreateButton();
        }

		void ApplyFilter()
		{
			var query = SearchBox.Text.Trim();
			var category = (CategoryTree.SelectedItem as System.Windows.Controls.TreeViewItem)?.Tag as string;
			var filtered = Templates.Where(t =>
                string.IsNullOrEmpty(category) || category == AllCategories
                || t.Categories.Any(templateCategory => templateCategory.Equals(category, StringComparison.OrdinalIgnoreCase)
                    || templateCategory.StartsWith(category + "/", StringComparison.OrdinalIgnoreCase)));
            filtered = string.IsNullOrEmpty(query) ? filtered : filtered.Where(t => Matches(t, query));
			var filteredTemplates = filtered.ToArray();
			var choices = filteredTemplates
                .GroupBy(t => string.IsNullOrWhiteSpace(t.GroupIdentity) ? t.Identity : t.GroupIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => new TemplateChoice(group))
                .OrderBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
			TemplateList.ItemsSource = choices;
			if (TemplateList.Tag is string lastTemplate && TemplateList.SelectedItem is null)
            {
                var savedChoice = choices.FirstOrDefault(choice => choice.Variants.Any(template =>
                    string.Equals(template.Identity, lastTemplate, StringComparison.OrdinalIgnoreCase)));
                if (savedChoice is not null)
                    TemplateList.SelectedItem = savedChoice;
            }
			StatusText.Text = Templates.Count == 0 ? "No project templates found."
				: string.IsNullOrEmpty(query) ? $"{choices.Length} template(s) available."
				: $"{choices.Length} of {Templates.Count} template(s) match.";
		}

        void UpdateTemplateDescription()
        {
            if (SelectedTemplate is null)
            {
                TemplateTitleText.Text = ResourceService.GetString("NewProject.TemplateDescriptionHint");
                TemplateDescriptionText.Text = string.Empty;
                TemplateDetailsText.Text = string.Empty;
                return;
            }

            TemplateTitleText.Text = SelectedTemplate.DisplayName;
            TemplateDescriptionText.Text = SelectedTemplate.Description ?? "No description was supplied by this template.";
            TemplateDetailsText.Text = $"Short name: {SelectedTemplate.ShortName}" +
                (string.IsNullOrWhiteSpace(SelectedTemplate.GroupIdentity) ? string.Empty : $"   Group: {SelectedTemplate.GroupIdentity}");
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
            tree.Items.Add(new System.Windows.Controls.TreeViewItem { Header = ResourceService.GetString("NewProject.AllTypes"), Tag = AllCategories, IsSelected = true });
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
                var label = new System.Windows.Controls.Label
                {
                    Content = option.DisplayName + ":",
                    ToolTip = option.Description,
                    Padding = new Thickness(0, 2, 8, 2),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(label);

                if (option.Choices is { Count: > 0 })
                {
                    var combo = new System.Windows.Controls.ComboBox { Tag = option.Name, ToolTip = option.Description, DisplayMemberPath = nameof(TemplateOptionChoice.DisplayName), SelectedValuePath = nameof(TemplateOptionChoice.Key) };
                    combo.ItemsSource = option.Choices.Select(choice => new TemplateOptionChoice(choice.Key, choice.Value))
                        .OrderBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
                    combo.SelectedValue = option.DefaultValue;
                    if (combo.SelectedIndex < 0)
                        combo.SelectedIndex = 0;
                    combo.SelectionChanged += OnTemplateOptionChanged;
                    System.Windows.Controls.Grid.SetColumn(combo, 1);
                    panel.Children.Add(combo);
                    _templateOptionValues[option.Name] = combo.SelectedValue as string;
                }
                else if (option.DataType.Equals("bool", StringComparison.OrdinalIgnoreCase)
                    || option.DataType.Equals("boolean", StringComparison.OrdinalIgnoreCase))
                {
                    var checkBox = new System.Windows.Controls.CheckBox { Tag = option.Name, ToolTip = option.Description, VerticalAlignment = VerticalAlignment.Center };
                    checkBox.IsChecked = bool.TryParse(option.DefaultValue, out var value) && value;
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
                case System.Windows.Controls.ComboBox combo when combo.Tag is string name:
                    _templateOptionValues[name] = combo.SelectedValue as string;
                    break;
                case System.Windows.Controls.CheckBox checkBox when checkBox.Tag is string name:
                    _templateOptionValues[name] = checkBox.IsChecked == true ? "true" : "false";
                    break;
                case System.Windows.Controls.TextBox textBox when textBox.Tag is string name:
                    _templateOptionValues[name] = textBox.Text;
                    break;
            }
        }

		static bool Matches(TemplateSummary template, string query)
		{
			return Contains(template.Name, query) || Contains(template.ShortName, query)
				|| Contains(template.Description, query)
				|| template.Tags.Any(tag => Contains(tag.Key, query) || Contains(tag.Value, query));
		}

		static bool Contains(string? value, string query) =>
			value?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateCreateButton();
            UpdateCreatePath();
        }

        void OnLocationChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateCreateButton();
            UpdateCreatePath();
        }

        void OnSolutionNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateCreateButton();
            UpdateCreatePath();
        }

        void OnSolutionDirectoryChanged(object sender, RoutedEventArgs e)
        {
            UpdateCreateButton();
            UpdateCreatePath();
        }

        void UpdateCreatePath()
        {
            // XAML can raise Checked/TextChanged while InitializeComponent is still assigning
            // named fields (notably the default IsChecked value of the solution-directory box).
            if (CreatePathText is null)
                return;

            var location = Location;
            var solutionName = SolutionName;
            var projectName = ProjectName;
            if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(projectName))
            {
                CreatePathText.Text = ResourceService.GetString("NewProject.CreatePathHint");
                return;
            }

            try
            {
            var root = _createNewSolution && CreateSolutionDirectory && !string.IsNullOrWhiteSpace(solutionName)
                    ? Path.Combine(location, solutionName)
                    : location;
                CreatePathText.Text = ResourceService.GetString("NewProject.CreateIn") + Path.Combine(root, projectName);
            }
            catch (ArgumentException)
            {
                CreatePathText.Text = ResourceService.GetString("NewProject.InvalidLocation");
            }
        }

        void OnBrowseLocationClick(object sender, RoutedEventArgs e)
        {
            var selectedFolder = FileDialogService.PickFolder();
            if (!string.IsNullOrWhiteSpace(selectedFolder))
                LocationBox.Text = selectedFolder;
        }

        void OnCreateClick(object sender, RoutedEventArgs e)
        {
            if (SelectedTemplate is null || string.IsNullOrWhiteSpace(ProjectName)
                || string.IsNullOrWhiteSpace(Location))
                return;

            if (!TryValidateName(ProjectName, "project"))
                return;
            if (_createNewSolution && !string.IsNullOrWhiteSpace(SolutionName)
                && !TryValidateName(SolutionName, "solution"))
                return;

            SD.PropertyService.Set(DefaultPathKey, Location);
            SD.PropertyService.Set(LastCategoryKey,
                (CategoryTree.SelectedItem as System.Windows.Controls.TreeViewItem)?.Tag as string ?? AllCategories);
            SD.PropertyService.Set(LastTemplateKey, SelectedTemplate.Identity);
            SD.PropertyService.Set(CreateSolutionDirectoryKey, CreateSolutionDirectory);
            this.CloseDialog(true);
        }

        bool TryValidateName(string name, string kind)
        {
            if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                StatusText.Text = $"Enter a valid {kind} name. It cannot contain file-name punctuation.";
                return false;
            }

            try
            {
                _ = Path.GetFullPath(Path.Combine(Location, name));
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                StatusText.Text = ResourceService.GetString("NewProject.InvalidLocation");
                return false;
            }
        }

        void OnCancelClick(object sender, RoutedEventArgs e)
        {
            this.CloseDialog(false);
        }

        void UpdateCreateButton()
        {
            if (CreateButton is null)
                return;

            CreateButton.IsEnabled = SelectedTemplate is not null
                && !string.IsNullOrWhiteSpace(ProjectName)
                && !string.IsNullOrWhiteSpace(Location);
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
