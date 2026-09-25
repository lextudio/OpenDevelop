#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace ICSharpCode.SharpDevelop.Services
{
    /// <summary>
    /// WPF replacement for the legacy WinForms SelectReferenceDialog. Covers what SDK-style projects
    /// still reference directly - other projects in the solution and assembly files; NuGet packages go
    /// through Manage Packages, and GAC/COM references are not offered.
    /// </summary>
    [CLSCompliant(false)]
    public sealed partial class AddReferenceWindow : Window
    {
        public sealed class ProjectCandidate
        {
            public ProjectCandidate(string name, string path)
            {
                Name = name;
                Path = path;
            }

            public string Name { get; }
            public string Path { get; }
            public bool IsSelected { get; set; }
        }

        readonly ObservableCollection<string> _assemblies = new ObservableCollection<string>();
        readonly IReadOnlyList<ProjectCandidate> _projects;

        public IReadOnlyList<string> SelectedProjectPaths =>
            _projects.Where(p => p.IsSelected).Select(p => p.Path).ToArray();

        public IReadOnlyList<string> SelectedAssemblyPaths => _assemblies.ToArray();

        AddReferenceWindow(string projectName, IReadOnlyList<ProjectCandidate> projects)
        {
            InitializeComponent();
            Title = "Add Reference - " + projectName;
            _projects = projects;
            ProjectList.ItemsSource = projects;
            AssemblyList.ItemsSource = _assemblies;
            if (projects.Count == 0)
            {
                ProjectList.Visibility = Visibility.Collapsed;
                NoProjectsText.Visibility = Visibility.Visible;
            }
            UpdateState();
        }

        public static AddReferenceWindow? Show(string projectName, IReadOnlyList<ProjectCandidate> projects, Window? owner)
        {
            var dialog = new AddReferenceWindow(projectName, projects) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog : null;
        }

        void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateState();

        void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            var files = FileDialogService.PickFiles("Assemblies (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*", this);
            foreach (var file in files)
            {
                if (!_assemblies.Contains(file, StringComparer.OrdinalIgnoreCase))
                    _assemblies.Add(file);
            }
            UpdateState();
        }

        void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            foreach (var file in AssemblyList.SelectedItems.Cast<string>().ToArray())
                _assemblies.Remove(file);
            UpdateState();
        }

        void OnAddClick(object sender, RoutedEventArgs e) => this.CloseDialog(true);

        void OnCancelClick(object sender, RoutedEventArgs e) => this.CloseDialog(false);

        void UpdateState()
        {
            int projectCount = _projects.Count(p => p.IsSelected);
            int assemblyCount = _assemblies.Count;
            AddButton.IsEnabled = projectCount + assemblyCount > 0;
            StatusText.Text = projectCount + assemblyCount == 0
                ? "Select projects or browse for assemblies to reference."
                : $"{projectCount} project(s), {assemblyCount} assembly file(s) selected.";
        }
    }
}
