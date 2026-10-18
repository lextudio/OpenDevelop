// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Drawing.Design;
using System.IO;
using System.Linq;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Gui;
using System.Windows;
using System.Windows.Controls;

namespace ICSharpCode.FormsDesigner
{
	/// <summary>Persistent, metadata-only catalogue behind WinForms' "Choose Toolbox Items".
	/// The workbench records only paths and type names; it never loads a selected assembly. The
	/// disposable Forms host receives the selected DLL and its sibling dependencies in the normal
	/// document snapshot, and is consequently the only process that constructs third-party code.</summary>
	internal static class CustomToolboxCatalog
	{
		const string SelectionsKey = "FormsDesigner.Toolbox.CustomSelections";
		const string Scope = "winforms";
		static readonly List<SharedToolboxItem> currentItems = new();
		static bool contextActionRegistered;
		public static event Action SelectionsChanged;

		internal sealed record Selection(string AssemblyPath, string TypeName);

		public static void EnsureRegistered()
		{
			if (contextActionRegistered) return;
			contextActionRegistered = true;
			SharedToolbox.Instance.RegisterContextAction(Scope, "Choose Toolbox Items...", ShowChooser);
			Rebuild();
		}

		public static IReadOnlyList<Selection> LoadSelections() => PropertyService.GetList<string>(SelectionsKey)
			.Select(Parse).Where(selection => selection != null).Cast<Selection>()
			.GroupBy(selection => Key(selection.AssemblyPath, selection.TypeName), StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First()).ToArray();

		public static void SaveSelections(IEnumerable<Selection> selections)
		{
			PropertyService.SetList(SelectionsKey, selections
				.Where(selection => selection != null && File.Exists(selection.AssemblyPath) && !String.IsNullOrWhiteSpace(selection.TypeName))
				.Select(selection => ToolboxSelectionCodec.Encode(selection.AssemblyPath, selection.TypeName))
				.Distinct(StringComparer.OrdinalIgnoreCase));
			Rebuild();
			SelectionsChanged?.Invoke();
		}

		static Selection? Parse(string value)
		{
			try {
				if (!ToolboxSelectionCodec.TryDecode(value, out var savedPath, out var typeName)) return null;
				var path = Path.GetFullPath(savedPath);
				return File.Exists(path) ? new Selection(path, typeName) : null;
			} catch (Exception) when (value != null) { return null; }
		}

		/// <summary>All files the child needs to resolve a chosen third-party type, including
		/// sibling dependencies. This does not inspect or load the assemblies.</summary>
		public static IReadOnlyList<string> ReferencedAssemblyPaths()
		{
			var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var selection in LoadSelections()) {
				paths.Add(selection.AssemblyPath);
				var directory = Path.GetDirectoryName(selection.AssemblyPath);
				if (!String.IsNullOrEmpty(directory) && Directory.Exists(directory)) {
					try { paths.UnionWith(Directory.EnumerateFiles(directory, "*.dll")); }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
				}
			}
			return paths.ToArray();
		}

		public static IReadOnlyList<(Selection Selection, ToolboxMetadataDiscovery.ControlInfo Control)> Discover(IEnumerable<string> assemblyPaths)
		{
			var paths = assemblyPaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			var selected = LoadSelections().ToDictionary(item => Key(item.AssemblyPath, item.TypeName), StringComparer.OrdinalIgnoreCase);
			var result = new List<(Selection, ToolboxMetadataDiscovery.ControlInfo)>();
			foreach (var candidate in DiscoverCandidates(paths))
				if (selected.TryGetValue(Key(candidate.AssemblyPath, candidate.Control.FullName), out var selection))
					result.Add((selection, candidate.Control));
			return result;
		}

		public static IReadOnlyList<(string AssemblyPath, ToolboxMetadataDiscovery.ControlInfo Control)> DiscoverCandidates(IEnumerable<string> assemblyPaths)
		{
			// A Microsoft WinForms project's output does not copy System.Windows.Forms (and a
			// Component's base lives in System.ComponentModel.Primitives). They are metadata
			// probes, not candidates: without them the PE walk cannot follow a third-party
			// control/component's base chain and the chooser would look empty on Windows.
			var paths = assemblyPaths.Append(typeof(System.Windows.Forms.Control).Assembly.Location)
				.Append(typeof(System.ComponentModel.Component).Assembly.Location)
				.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			var result = new List<(string, ToolboxMetadataDiscovery.ControlInfo)>();
			foreach (var assemblyPath in paths.Where(path => !OutOfProcess.WinFormsProjectControlFinder.IsFrameworkAssembly(path))) {
				try {
					var forms = ToolboxMetadataDiscovery.Discover(assemblyPath, paths, "System.Windows.Forms.Form", "System.Windows.Forms.Form")
						.Select(control => control.FullName).ToHashSet(StringComparer.Ordinal);
					var candidates = ToolboxMetadataDiscovery.Discover(assemblyPath, paths,
						ToolboxMetadataDiscovery.WinFormsControlBase, ToolboxMetadataDiscovery.WinFormsUserControlBase)
						.Concat(ToolboxMetadataDiscovery.Discover(assemblyPath, paths,
							ToolboxMetadataDiscovery.ComponentBase, ToolboxMetadataDiscovery.WinFormsUserControlBase, ToolboxMetadataDiscovery.ComponentInterface));
					result.AddRange(candidates.Where(control => control.HasPublicDefaultConstructor && !control.IsHiddenFromToolbox
						&& control.IsDesignTimeVisible && !forms.Contains(control.FullName)).GroupBy(control => control.FullName)
						.Select(group => (assemblyPath, group.First())));
				} catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException) { }
			}
			return result;
		}

		static string Key(string assemblyPath, string typeName) => assemblyPath + "\n" + typeName;

		static void Rebuild()
		{
			var toolbox = SharedToolbox.Instance;
			toolbox.RemoveItems(currentItems.Contains);
			currentItems.Clear();
			var items = Discover(ReferencedAssemblyPaths()).Select(found => CreateItem(found.Selection, found.Control)).ToArray();
			currentItems.AddRange(items);
			toolbox.AddItems(items);
		}

		static SharedToolboxItem CreateItem(Selection selection, ToolboxMetadataDiscovery.ControlInfo control)
		{
			var toolboxItem = new ToolboxItem { TypeName = control.FullName, DisplayName = control.Name };
			return new SharedToolboxItem(Path.GetFileNameWithoutExtension(selection.AssemblyPath), control.Name, Scope,
				DesignerTypeIcons.GetIcon(control.Name, control.IsUserControl ? "UserControl" : DesignerTypeIcons.FallbackIconName), toolboxItem,
				data => data.SetData(typeof(ToolboxItem), toolboxItem),
				() => (SD.Services.GetService(typeof(IToolboxService)) as IToolboxService)?.SetSelectedToolboxItem(toolboxItem), control.Filters);
		}

		static void ShowChooser() => new ChooseToolboxItemsDialog().ShowDialog();
	}

	/// <summary>A compact WPF replacement for the removed XmlForms dialog. It deliberately scans
	/// only PE metadata, so browsing a directory is safe even when a selected control's static
	/// constructor or dependency graph is broken.</summary>
	sealed class ChooseToolboxItemsDialog : Window
	{
		readonly ListBox directories = new ListBox { MinHeight = 54 };
		readonly ListBox candidates = new ListBox { MinHeight = 280 };
		readonly List<string> directoryPaths = new();
		readonly HashSet<string> selected;

		public ChooseToolboxItemsDialog()
		{
			Title = "Choose Toolbox Items";
			Width = 760; Height = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
			selected = CustomToolboxCatalog.LoadSelections().Select(item => item.AssemblyPath + "\n" + item.TypeName)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			directoryPaths.AddRange(CustomToolboxCatalog.LoadSelections().Select(item => Path.GetDirectoryName(item.AssemblyPath))
				.Where(path => !String.IsNullOrEmpty(path)).Distinct(StringComparer.OrdinalIgnoreCase)!);
			var root = new Grid { Margin = new Thickness(12) };
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
			var ok = new Button { Content = "OK", Width = 88, IsDefault = true, Margin = new Thickness(4) };
			ok.Click += (_, _) => { Save(); DialogResult = true; };
			var cancel = new Button { Content = "Cancel", Width = 88, IsCancel = true, Margin = new Thickness(4) };
			buttons.Children.Add(ok); buttons.Children.Add(cancel); Grid.SetRow(buttons, 5); root.Children.Add(buttons);
			var directoryButtons = new StackPanel { Orientation = Orientation.Horizontal };
			var addDirectory = new Button { Content = "Add directory...", Margin = new Thickness(0, 0, 6, 6) };
			addDirectory.Click += (_, _) => AddDirectory();
			var removeDirectory = new Button { Content = "Remove", Margin = new Thickness(0, 0, 0, 6) };
			removeDirectory.Click += (_, _) => { if (directories.SelectedItem is string path) { directoryPaths.Remove(path); selected.RemoveWhere(key => key.StartsWith(path + "\n", StringComparison.OrdinalIgnoreCase)); Refresh(); } };
			directoryButtons.Children.Add(addDirectory); directoryButtons.Children.Add(removeDirectory);
			var directoryLabel = new TextBlock { Text = "Custom component directories", Margin = new Thickness(0, 0, 0, 4) };
			Grid.SetRow(directoryLabel, 0); root.Children.Add(directoryLabel);
			Grid.SetRow(directoryButtons, 1); root.Children.Add(directoryButtons);
			Grid.SetRow(directories, 2); root.Children.Add(directories);
			var candidatesLabel = new TextBlock { Text = "Available WinForms controls and components", Margin = new Thickness(0, 10, 0, 4) };
			Grid.SetRow(candidatesLabel, 3); root.Children.Add(candidatesLabel);
			Grid.SetRow(candidates, 4); root.Children.Add(candidates); Content = root;
			Refresh();
		}

		void AddDirectory()
		{
			var path = SD.FileService.BrowseForFolder("Select a directory containing WinForms component assemblies", directoryPaths.FirstOrDefault());
			if (String.IsNullOrEmpty(path)) return;
			if (!directoryPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) directoryPaths.Add(path);
			Refresh();
		}

		void Refresh()
		{
			directories.ItemsSource = directoryPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
			candidates.Items.Clear();
			var assemblies = directoryPaths.Where(Directory.Exists).SelectMany(GetAssembliesInDirectory).ToArray();
			foreach (var candidate in CustomToolboxCatalog.DiscoverCandidates(assemblies).OrderBy(item => item.Control.Name, StringComparer.OrdinalIgnoreCase)) {
				var key = candidate.AssemblyPath + "\n" + candidate.Control.FullName;
				var check = new CheckBox { Content = candidate.Control.Name + "  (" + Path.GetFileNameWithoutExtension(candidate.AssemblyPath) + ")", IsChecked = selected.Contains(key), Tag = key };
				check.Checked += (_, _) => selected.Add((string)check.Tag);
				check.Unchecked += (_, _) => selected.Remove((string)check.Tag);
				candidates.Items.Add(check);
			}
		}

		static IReadOnlyList<string> GetAssembliesInDirectory(string directory)
		{
			try { return Directory.GetFiles(directory, "*.dll"); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
		}

		void Save()
		{
			var choices = selected.Select(value => {
				var separator = value.IndexOf('\n');
				return new CustomToolboxCatalog.Selection(value.Substring(0, separator), value.Substring(separator + 1));
			}).ToArray();
			CustomToolboxCatalog.SaveSelections(choices);
		}
	}
}
