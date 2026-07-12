// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.WpfDesign.Designer.Services;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// Manages the WpfToolbox: a grouped list of the WPF popular-controls set plus one
	/// group per assembly referenced by the project being designed.
	/// </summary>
	public class WpfToolbox
	{
		const string PopularControlsCategory = "Windows Presentation Foundation";

		static WpfToolbox instance;

		public static WpfToolbox Instance {
			get {
				SD.MainThread.VerifyAccess();
				return instance ?? (instance = new WpfToolbox());
			}
		}

		readonly ListBox toolbox = new ListBox();
		readonly CollectionViewSource itemsView = new CollectionViewSource();
		readonly List<WpfSideTabItem> items = new List<WpfSideTabItem>();
		Point dragStartPoint;

		IToolService toolService;

		public WpfToolbox()
		{
			// Guarantees Metadata.GetPopularControls() is populated before this constructor reads
			// it below, regardless of whether a WpfViewContent (which also calls this) has been
			// constructed yet - WpfToolbox.Instance is a lazily-constructed, process-lifetime
			// singleton, and whichever caller touches it first otherwise permanently freezes
			// "items" as empty if that caller ran before any WpfViewContent existed (e.g. the
			// Tools pad querying the active view's IToolsHost.ToolsContent before the WPF
			// designer's own secondary view for the current file has loaded).
			ICSharpCode.WpfDesign.Designer.BasicMetadata.Register();

			itemsView.Source = items;
			itemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WpfSideTabItem.CategoryName)));

			toolbox.ItemsSource = itemsView.View;
			toolbox.SelectionChanged += OnSelectionChanged;
			toolbox.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
			toolbox.PreviewMouseMove += OnPreviewMouseMove;

			toolbox.ItemTemplate = CreateItemTemplate();
			toolbox.GroupStyle.Add(CreateGroupStyle());

			items.Add(new WpfSideTabItem(PopularControlsCategory));
			foreach (Type t in Metadata.GetPopularControls())
				items.Add(new WpfSideTabItem(PopularControlsCategory, t));

			// "items" is a plain List<T> (not observable), so the CollectionViewSource.View bound
			// as the ListBox's ItemsSource above won't pick up these .Add() calls on its own -
			// AddProjectDlls already knows this and calls Refresh() itself, but the constructor's
			// own initial population needs the same nudge or the toolbox renders empty until
			// something else happens to add project DLLs later.
			itemsView.View.Refresh();

			toolbox.SelectedIndex = 0;
		}

		static DataTemplate CreateItemTemplate()
		{
			var iconImage = new FrameworkElementFactory(typeof(Image));
			iconImage.SetValue(FrameworkElement.WidthProperty, 16d);
			iconImage.SetValue(FrameworkElement.HeightProperty, 16d);
			iconImage.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
			iconImage.SetBinding(Image.SourceProperty, new Binding(nameof(WpfSideTabItem.Icon)));

			var text = new FrameworkElementFactory(typeof(TextBlock));
			text.SetBinding(TextBlock.TextProperty, new Binding(nameof(WpfSideTabItem.DisplayName)));
			text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

			var panel = new FrameworkElementFactory(typeof(StackPanel));
			panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
			panel.AppendChild(iconImage);
			panel.AppendChild(text);

			return new DataTemplate(typeof(WpfSideTabItem)) { VisualTree = panel };
		}

		static GroupStyle CreateGroupStyle()
		{
			var header = new FrameworkElementFactory(typeof(TextBlock));
			header.SetBinding(TextBlock.TextProperty, new Binding("Name"));
			header.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
			header.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 4, 0, 2));

			return new GroupStyle {
				HeaderTemplate = new DataTemplate { VisualTree = header }
			};
		}

		static bool IsControl(Type t)
		{
			return !t.IsAbstract && !t.IsGenericTypeDefinition && t.IsSubclassOf(typeof(FrameworkElement));
		}

		static readonly HashSet<string> addedAssemblies = new HashSet<string>();
		public void AddProjectDlls(OpenedFile file)
		{
			var project = SD.ProjectService.FindProjectContainingFile(file.FileName);
			if (project == null)
				return;

			var typeResolutionService = new TypeResolutionService(file.FileName);

			// Enumerate the project's referenced assemblies from MSBuild's ResolveAssemblyReferences
			// target (the Roslyn-aligned reference source) instead of the old NRefactory
			// ICompilation.ReferencedAssemblies, which is null now that C# projects use Roslyn/LSP.
			foreach (var reference in project.ResolveAssemblyReferences(System.Threading.CancellationToken.None)) {
				string assemblyFileName = reference.FileName;

				if (string.IsNullOrEmpty(assemblyFileName) || !System.IO.File.Exists(assemblyFileName) || addedAssemblies.Contains(assemblyFileName))
					continue;

				try {
					// DO NOT USE Assembly.LoadFrom!!!
					// see http://community.sharpdevelop.net/forums/t/19968.aspx
					Assembly assembly = typeResolutionService.LoadAssembly(assemblyFileName);
					if (assembly == null) continue;

					string categoryName = StringParser.Parse(assembly.FullName.Split(new[] { ',' })[0]);
					var controlTypes = new List<Type>();
					foreach (var t in assembly.GetExportedTypes()) {
						if (IsControl(t))
							controlTypes.Add(t);
					}

					if (controlTypes.Count > 0) {
						items.Add(new WpfSideTabItem(categoryName));
						foreach (var t in controlTypes)
							items.Add(new WpfSideTabItem(categoryName, t));
						itemsView.View.Refresh();
					}

					addedAssemblies.Add(assemblyFileName);
				} catch (Exception ex) {
					WpfViewContent.DllLoadErrors.Add(new SDTask(new BuildError(assemblyFileName, ex.Message)));
				}
			}
		}

		void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (toolService == null)
				return;

			var item = toolbox.SelectedItem as WpfSideTabItem;
			toolService.CurrentTool = item?.Tool ?? toolService.PointerTool;
		}

		void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			dragStartPoint = e.GetPosition(toolbox);
		}

		void OnPreviewMouseMove(object sender, MouseEventArgs e)
		{
			if (e.LeftButton != MouseButtonState.Pressed)
				return;

			Point position = e.GetPosition(toolbox);
			if (Math.Abs(position.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
			    Math.Abs(position.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
				return;

			if (!(toolbox.SelectedItem is WpfSideTabItem item) || item.Tool == null)
				return;

			var data = new DataObject(item.Tool);

			if (item.Tool is CreateComponentTool componentTool)
			{
				data.SetData(typeof(Type), componentTool.ComponentType);
				data.SetData("ComponentTypeName", componentTool.ComponentType.FullName);
			}

			DragDrop.DoDragDrop(toolbox, data, DragDropEffects.Copy);
		}

		public object ToolboxControl {
			get { return toolbox; }
		}

		public IToolService ToolService {
			get { return toolService; }
			set {
				if (toolService != null)
					toolService.CurrentToolChanged -= OnCurrentToolChanged;

				toolService = value;

				if (toolService != null) {
					toolService.CurrentToolChanged += OnCurrentToolChanged;
					OnCurrentToolChanged(null, null);
				}
			}
		}

		void OnCurrentToolChanged(object sender, EventArgs e)
		{
			if (toolService == null)
				return;

			var toolToFind = toolService.CurrentTool == toolService.PointerTool ? null : toolService.CurrentTool;
			foreach (WpfSideTabItem item in items) {
				if (ReferenceEquals(item.Tool, toolToFind)) {
					toolbox.SelectedItem = item;
					return;
				}
			}

			toolbox.SelectedIndex = 0;
		}
	}
}
