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
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.AddIn;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;

namespace ICSharpCode.CodeCoverage
{
	/// <summary>
	/// WPF replacement for the old WinForms-hosted-in-WPF UserControl (WindowsFormsIntegration's
	/// ElementHost is gone from this fork - see AltCoverApplication/CodeCoverage.csproj remarks
	/// elsewhere in this migration). This version is a plain WPF UserControl throughout: the tree
	/// (CodeCoverageTreeView, a SharpTreeView), the sequence-point list (a native WPF ListView), and
	/// the source preview (AvalonEdit's TextEditor, which is a WPF control natively - no host needed
	/// even in the old version) are laid out with a Grid + GridSplitters instead of WinForms
	/// SplitContainers.
	///
	/// Deliberate simplification vs. the old control: panel show/hide is done by collapsing grid
	/// rows/columns rather than the old code's dynamic Controls.Add/Remove dance; the custom
	/// column-header click sorting for the sequence-point list (SequencePointListViewSorter, WinForms
	/// ListView-specific) was dropped rather than reimplemented against WPF's ListView/GridView -
	/// this list is small (sequence points for one method/property) and unsorted-by-default should
	/// be an acceptable narrowing of scope for this migration pass.
	/// </summary>
	public class CodeCoverageControl : UserControl
	{
		Grid rootGrid;
		Grid splitGrid;
		ToolBar toolBar;
		CodeCoverageTreeView treeView;
		ListView listView;
		TextEditor textEditor;
		string textEditorFileName;
		bool showSourceCodePanel;
		bool showVisitCountPanel = true;

		public CodeCoverageControl()
		{
			treeView = new CodeCoverageTreeView();
			treeView.SelectionChanged += TreeViewSelectionChanged;

			listView = new ListView();
			listView.View = CreateGridView();
			listView.MouseDoubleClick += ListViewMouseDoubleClick;

			textEditor = AvalonEditTextEditorAdapter.CreateAvalonEditInstance();
			textEditor.IsReadOnly = true;
			textEditor.MouseDoubleClick += TextEditorDoubleClick;

			var adapter = new AvalonEditTextEditorAdapter(textEditor);
			var textMarkerService = new TextMarkerService(adapter.TextEditor.Document);
			adapter.TextEditor.TextArea.TextView.BackgroundRenderers.Add(textMarkerService);
			adapter.TextEditor.TextArea.TextView.LineTransformers.Add(textMarkerService);
			adapter.TextEditor.TextArea.TextView.Services.AddService(typeof(ITextMarkerService), textMarkerService);

			toolBar = ToolBarService.CreateToolBar(treeView, treeView, "/SharpDevelop/Pads/CodeCoveragePad/Toolbar");

			splitGrid = new Grid();
			splitGrid.ColumnDefinitions.Add(new ColumnDefinition());
			splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			splitGrid.ColumnDefinitions.Add(new ColumnDefinition());
			Grid.SetColumn(treeView, 0);
			var treeSplitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
			Grid.SetColumn(treeSplitter, 1);
			var rightPane = new Grid();
			rightPane.RowDefinitions.Add(new RowDefinition());
			rightPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			rightPane.RowDefinitions.Add(new RowDefinition());
			Grid.SetRow(listView, 0);
			var rightSplitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
			Grid.SetRow(rightSplitter, 1);
			Grid.SetRow(textEditor, 2);
			rightPane.Children.Add(listView);
			rightPane.Children.Add(rightSplitter);
			rightPane.Children.Add(textEditor);
			Grid.SetColumn(rightPane, 2);

			splitGrid.Children.Add(treeView);
			splitGrid.Children.Add(treeSplitter);
			splitGrid.Children.Add(rightPane);

			rootGrid = new Grid();
			rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			rootGrid.RowDefinitions.Add(new RowDefinition());
			Grid.SetRow(toolBar, 0);
			Grid.SetRow(splitGrid, 1);
			rootGrid.Children.Add(toolBar);
			rootGrid.Children.Add(splitGrid);
			Content = rootGrid;

			UpdatePanelVisibility();
		}

		static GridView CreateGridView()
		{
			var gridView = new GridView();
			gridView.Columns.Add(new GridViewColumn { Header = StringParser.Parse("${res:ICSharpCode.CodeCoverage.VisitCount}"), DisplayMemberBinding = new System.Windows.Data.Binding("VisitCount") });
			gridView.Columns.Add(new GridViewColumn { Header = StringParser.Parse("${res:Global.TextLine}"), DisplayMemberBinding = new System.Windows.Data.Binding("Line") });
			gridView.Columns.Add(new GridViewColumn { Header = StringParser.Parse("${res:ICSharpCode.CodeCoverage.Column}"), DisplayMemberBinding = new System.Windows.Data.Binding("Column") });
			gridView.Columns.Add(new GridViewColumn { Header = StringParser.Parse("${res:ICSharpCode.CodeCoverage.EndLine}"), DisplayMemberBinding = new System.Windows.Data.Binding("EndLine") });
			gridView.Columns.Add(new GridViewColumn { Header = StringParser.Parse("${res:ICSharpCode.CodeCoverage.EndColumn}"), DisplayMemberBinding = new System.Windows.Data.Binding("EndColumn") });
			gridView.Columns.Add(new GridViewColumn { Header = StringParser.Parse("${res:ICSharpCode.CodeCoverage.Content}"), DisplayMemberBinding = new System.Windows.Data.Binding("Content"), Width = 500 });
			return gridView;
		}

		public void UpdateToolbar()
		{
			// WPF toolbar items built via ToolBarService.CreateToolBar re-evaluate their enabled
			// state automatically (through WPF's command-requery mechanism) - no equivalent of the
			// old WinForms ToolbarService.UpdateToolbar(toolStrip) call exists or is needed here.
			// Kept as a no-op method so CodeCoveragePad's existing calls don't need to change.
		}

		public void AddModules(System.Collections.Generic.List<CodeCoverageModule> modules)
		{
			treeView.AddModules(modules);
		}

		public void Clear()
		{
			treeView.Clear();
			listView.Items.Clear();
			textEditorFileName = null;
			textEditor.Text = String.Empty;
		}

		public bool ShowSourceCodePanel {
			get { return showSourceCodePanel; }
			set {
				if (showSourceCodePanel != value) {
					showSourceCodePanel = value;
					UpdatePanelVisibility();
					DisplaySelectedItem(treeView.SelectedNode);
				}
			}
		}

		public bool ShowVisitCountPanel {
			get { return showVisitCountPanel; }
			set {
				if (showVisitCountPanel != value) {
					showVisitCountPanel = value;
					UpdatePanelVisibility();
					DisplaySelectedItem(treeView.SelectedNode);
				}
			}
		}

		void UpdatePanelVisibility()
		{
			listView.Visibility = showVisitCountPanel ? Visibility.Visible : Visibility.Collapsed;
			textEditor.Visibility = showSourceCodePanel ? Visibility.Visible : Visibility.Collapsed;

			var rightPane = (Grid)listView.Parent;
			rightPane.RowDefinitions[0].Height = showVisitCountPanel ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
			rightPane.RowDefinitions[2].Height = showSourceCodePanel ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
		}

		void TreeViewSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			DisplaySelectedItem(treeView.SelectedNode);
		}

		void DisplaySelectedItem(CodeCoverageTreeNode node)
		{
			if (node == null) {
				return;
			}
			node.EnsureLazyChildren();

			if (showVisitCountPanel) {
				UpdateListView(node);
			}
			if (showSourceCodePanel) {
				UpdateTextEditor(node);
			}
		}

		void UpdateListView(CodeCoverageTreeNode node)
		{
			listView.Items.Clear();
			var classNode = node as CodeCoverageClassTreeNode;
			var methodNode = node as CodeCoverageMethodTreeNode;
			var propertyNode = node as CodeCoveragePropertyTreeNode;
			if (classNode != null) {
				AddClassTreeNode(classNode);
			} else if (methodNode != null) {
				AddSequencePoints(methodNode.Method);
			} else if (propertyNode != null) {
				AddPropertyTreeNode(propertyNode);
			}
		}

		void UpdateTextEditor(CodeCoverageTreeNode node)
		{
			var classNode = node as CodeCoverageClassTreeNode;
			CodeCoverageMethodTreeNode methodNode = node as CodeCoverageMethodTreeNode;
			CodeCoveragePropertyTreeNode propertyNode = node as CodeCoveragePropertyTreeNode;
			if (classNode != null && classNode.Children.Count > 0) {
				propertyNode = classNode.Children[0] as CodeCoveragePropertyTreeNode;
				methodNode = classNode.Children[0] as CodeCoverageMethodTreeNode;
			}

			if (propertyNode != null && propertyNode.Children.Count > 0) {
				methodNode = propertyNode.Children[0] as CodeCoverageMethodTreeNode;
			}

			if (methodNode != null && methodNode.Method.SequencePoints.Count > 0) {
				CodeCoverageSequencePoint sequencePoint = methodNode.Method.SequencePoints[0];
				if (sequencePoint.HasDocument()) {
					if (classNode == null) {
						OpenFile(sequencePoint.Document, sequencePoint.Line, sequencePoint.Column);
					} else {
						OpenFile(sequencePoint.Document, 1, 1);
					}
				}
			}
		}

		void AddClassTreeNode(CodeCoverageClassTreeNode node)
		{
			foreach (CodeCoverageTreeNode childNode in node.Children) {
				var method = childNode as CodeCoverageMethodTreeNode;
				var property = childNode as CodeCoveragePropertyTreeNode;
				if (method != null) {
					AddSequencePoints(method.Method);
				} else if (property != null) {
					AddPropertyTreeNode(property);
				}
			}
		}

		void AddPropertyTreeNode(CodeCoveragePropertyTreeNode node)
		{
			AddMethodIfNotNull(node.Property.Getter);
			AddMethodIfNotNull(node.Property.Setter);
		}

		void AddMethodIfNotNull(CodeCoverageMethod method)
		{
			if (method != null) {
				AddSequencePoints(method);
			}
		}

		void AddSequencePoints(CodeCoverageMethod method)
		{
			foreach (CodeCoverageSequencePoint sequencePoint in method.SequencePoints) {
				if (method.FileID == sequencePoint.FileID) {
					listView.Items.Add(sequencePoint);
				}
			}
		}

		void ListViewMouseDoubleClick(object sender, MouseButtonEventArgs e)
		{
			var sequencePoint = listView.SelectedItem as CodeCoverageSequencePoint;
			if (sequencePoint != null && sequencePoint.Document.Length > 0) {
				FileService.JumpToFilePosition(sequencePoint.Document, sequencePoint.Line, sequencePoint.Column);
			}
		}

		void OpenFile(string fileName, int line, int column)
		{
			if (fileName != textEditorFileName) {
				if (!TryLoadFileIntoTextEditor(fileName)) {
					return;
				}
				textEditor.SyntaxHighlighting = GetSyntaxHighlighting(fileName);
				textEditorFileName = fileName;
			}
			textEditor.ScrollToEnd();
			textEditor.TextArea.Caret.Location = new TextLocation(line, column);
			textEditor.ScrollToLine(line);
			CodeCoverageService.ShowCodeCoverage(new AvalonEditTextEditorAdapter(textEditor), fileName);
		}

		bool TryLoadFileIntoTextEditor(string fileName)
		{
			if (!File.Exists(fileName)) {
				textEditor.Text = String.Format("File does not exist '{0}'.", fileName);
				return false;
			}

			textEditor.Load(fileName);
			return true;
		}

		IHighlightingDefinition GetSyntaxHighlighting(string fileName)
		{
			return HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(fileName));
		}

		void TextEditorDoubleClick(object sender, MouseButtonEventArgs e)
		{
			string fileName = textEditorFileName;
			if (fileName != null) {
				var caret = textEditor.TextArea.Caret;
				FileService.JumpToFilePosition(fileName, caret.Line, caret.Column);
			}
		}
	}
}
