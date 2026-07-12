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
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Xml;
using System.Xml.XPath;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.XmlEditor
{
	/// <summary>
	/// A namespace prefix/URI row shown in the namespaces <see cref="DataGrid"/>.
	/// </summary>
	public class XPathQueryNamespaceRow
	{
		public string Prefix { get; set; } = string.Empty;
		public string Uri { get; set; } = string.Empty;
	}

	/// <summary>
	/// A row shown in the XPath results <see cref="ListView"/> - either a matched node, a
	/// "no results" placeholder, or an error.
	/// </summary>
	public class XPathQueryResultRow
	{
		public string Match { get; set; } = string.Empty;
		public string Line { get; set; } = string.Empty;
		public object Tag { get; set; }
	}

	public class XPathQueryControl : UserControl, IMementoCapable
	{
		const string NamespacesProperty = "Namespaces";
		const string PrefixColumnWidthProperty = "NamespacesDataGridView.PrefixColumn.Width";
		const string MatchColumnWidthProperty = "XPathResultsListView.MatchColumn.Width";
		const string LineColumnWidthProperty = "XPathResultsListView.LineColumn.Width";
		const string XPathComboBoxTextProperty = "XPathQuery.LastQuery";
		const string XPathComboBoxItemsProperty = "XPathQuery.History";

		/// <summary>
		/// The filename that the last query was executed on.
		/// </summary>
		string fileName = string.Empty;

		/// <summary>
		/// The total number of xpath queries to remember.
		/// </summary>
		const int xpathQueryHistoryLimit = 20;

		bool ignoreXPathTextChanges;

		enum MoveCaret {
			ByJumping = 1,
			ByScrolling = 2
		}

		readonly ObservableCollection<XPathQueryNamespaceRow> namespaceRows = new ObservableCollection<XPathQueryNamespaceRow>();
		readonly ObservableCollection<XPathQueryResultRow> resultRows = new ObservableCollection<XPathQueryResultRow>();
		readonly ObservableCollection<string> xpathHistory = new ObservableCollection<string>();

		TextBox xPathLabel;
		ComboBox xpathComboBox;
		Button queryButton;
		TabControl tabControl;
		TabItem xPathResultsTabPage;
		TabItem namespacesTabPage;
		ListView xPathResultsListView;
		GridViewColumn matchColumnHeader;
		GridViewColumn lineColumnHeader;
		DataGrid namespacesDataGrid;
		DataGridTextColumn prefixColumn;
		DataGridTextColumn namespaceColumn;

		public XPathQueryControl()
		{
			InitializeComponent();
			InitStrings();
		}

		/// <summary>
		/// Adds a namespace to the namespace list.
		/// </summary>
		public void AddNamespace(string prefix, string uri)
		{
			namespaceRows.Add(new XPathQueryNamespaceRow { Prefix = prefix, Uri = uri });
		}

		/// <summary>
		/// Gets the list of namespaces in the namespace list.
		/// </summary>
		public XmlNamespaceCollection GetNamespaces()
		{
			var namespaces = new XmlNamespaceCollection();
			foreach (var row in namespaceRows) {
				string prefix = row.Prefix ?? string.Empty;
				string uri = row.Uri ?? string.Empty;
				if (prefix.Length == 0 && uri.Length == 0) {
					// Ignore.
				} else {
					namespaces.Add(new XmlNamespace(prefix, uri));
				}
			}
			return namespaces;
		}

		/// <summary>
		/// Creates a properties object that contains the current state of the
		/// control.
		/// </summary>
		public Properties CreateMemento()
		{
			var properties = new Properties();

			SaveNamespaces(properties);
			SaveNamespaceDataGridColumnWidths(properties);
			SaveXPathResultsListViewColumnWidths(properties);
			SaveXPathQueryHistory(properties);

			return properties;
		}

		void SaveNamespaces(Properties properties)
		{
			properties.SetList(NamespacesProperty, GetNamespaceStringArray());
		}

		void SaveNamespaceDataGridColumnWidths(Properties properties)
		{
			properties.Set<int>(PrefixColumnWidthProperty, (int)prefixColumn.Width.DisplayValue);
		}

		void SaveXPathResultsListViewColumnWidths(Properties properties)
		{
			properties.Set<int>(MatchColumnWidthProperty, (int)matchColumnHeader.Width);
			properties.Set<int>(LineColumnWidthProperty, (int)lineColumnHeader.Width);
		}

		void SaveXPathQueryHistory(Properties properties)
		{
			properties.Set(XPathComboBoxTextProperty, xpathComboBox.Text);
			properties.SetList(XPathComboBoxItemsProperty, GetXPathHistory());
		}

		/// <summary>
		/// Reloads the state of the control.
		/// </summary>
		public void SetMemento(Properties properties)
		{
			ignoreXPathTextChanges = true;

			try {
				LoadNamespaces(properties);
				LoadNamespaceDataGridColumnWidths(properties);
				LoadXPathResultsListViewColumnWidths(properties);
				LoadXPathQueryHistory(properties);
			} finally {
				ignoreXPathTextChanges = false;
			}
		}

		void LoadNamespaces(Properties properties)
		{
			var namespaces = properties.GetList<string>(NamespacesProperty);
			foreach (string ns in namespaces) {
				XmlNamespace xmlNamespace = XmlNamespace.FromString(ns);
				AddNamespace(xmlNamespace.Prefix, xmlNamespace.Name);
			}
		}

		void LoadNamespaceDataGridColumnWidths(Properties properties)
		{
			prefixColumn.Width = new DataGridLength(properties.Get<int>(PrefixColumnWidthProperty, 50));
		}

		void LoadXPathResultsListViewColumnWidths(Properties properties)
		{
			matchColumnHeader.Width = properties.Get<int>(MatchColumnWidthProperty, 432);
			lineColumnHeader.Width = properties.Get<int>(LineColumnWidthProperty, 60);
		}

		void LoadXPathQueryHistory(Properties properties)
		{
			xpathComboBox.Text = properties.Get(XPathComboBoxTextProperty, string.Empty);
			var xpaths = properties.GetList<string>(XPathComboBoxItemsProperty);
			foreach (string xpath in xpaths) {
				xpathHistory.Add(xpath);
			}
		}

		/// <summary>
		/// Called when the active workbench window has changed.
		/// </summary>
		public void ActiveWindowChanged()
		{
			UpdateQueryButtonState();
		}

		void InitializeComponent()
		{
			var root = new DockPanel();

			xPathLabel = new TextBox { Text = "XPath:", IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(3) };
			xpathComboBox = new ComboBox { IsEditable = true, ItemsSource = xpathHistory, Margin = new Thickness(3) };
			xpathComboBox.PreviewKeyDown += XPathComboBoxKeyDown;
			xpathComboBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(XPathComboBoxTextChanged));
			queryButton = new Button { Content = "Query", IsEnabled = false, Width = 70, Margin = new Thickness(3) };
			queryButton.Click += QueryButtonClick;

			var topPanel = new DockPanel();
			DockPanel.SetDock(xPathLabel, Dock.Left);
			DockPanel.SetDock(queryButton, Dock.Right);
			topPanel.Children.Add(xPathLabel);
			topPanel.Children.Add(queryButton);
			topPanel.Children.Add(xpathComboBox);
			DockPanel.SetDock(topPanel, Dock.Top);

			matchColumnHeader = new GridViewColumn { Header = "Match", Width = 432, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(XPathQueryResultRow.Match)) };
			lineColumnHeader = new GridViewColumn { Header = "Line", Width = 60, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(XPathQueryResultRow.Line)) };
			var gridView = new GridView();
			gridView.Columns.Add(matchColumnHeader);
			gridView.Columns.Add(lineColumnHeader);
			xPathResultsListView = new ListView { View = gridView, ItemsSource = resultRows };
			xPathResultsListView.MouseDoubleClick += (s, e) => JumpToResultLocation();
			xPathResultsListView.SelectionChanged += (s, e) => ScrollToResultLocation();

			xPathResultsTabPage = new TabItem { Header = "Results", Content = xPathResultsListView };

			prefixColumn = new DataGridTextColumn { Header = "Prefix", Width = new DataGridLength(50), Binding = new System.Windows.Data.Binding(nameof(XPathQueryNamespaceRow.Prefix)) };
			namespaceColumn = new DataGridTextColumn { Header = "Namespace", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new System.Windows.Data.Binding(nameof(XPathQueryNamespaceRow.Uri)) };
			namespacesDataGrid = new DataGrid {
				AutoGenerateColumns = false,
				CanUserAddRows = true,
				ItemsSource = namespaceRows
			};
			namespacesDataGrid.Columns.Add(prefixColumn);
			namespacesDataGrid.Columns.Add(namespaceColumn);

			namespacesTabPage = new TabItem { Header = "Namespaces", Content = namespacesDataGrid };

			tabControl = new TabControl();
			tabControl.Items.Add(xPathResultsTabPage);
			tabControl.Items.Add(namespacesTabPage);

			root.Children.Add(topPanel);
			root.Children.Add(tabControl);
			Content = root;
		}

		void XPathComboBoxTextChanged(object sender, TextChangedEventArgs e)
		{
			if (!ignoreXPathTextChanges) {
				UpdateQueryButtonState();
			}
		}

		void UpdateQueryButtonState()
		{
			queryButton.IsEnabled = IsXPathQueryEntered && XmlDisplayBinding.XmlViewContentActive;
		}

		bool IsXPathQueryEntered {
			get { return xpathComboBox.Text.Length > 0; }
		}

		void QueryButtonClick(object sender, RoutedEventArgs e)
		{
			RunXPathQuery();
		}

		void RunXPathQuery()
		{
			XmlView xmlView = XmlView.ActiveXmlView;
			if (xmlView == null) {
				return;
			}

			try {
				fileName = xmlView.File.FileName;

				ClearResults();
				XPathNodeTextMarker.RemoveMarkers(xmlView.TextEditor.Document);

				XPathQuery query = new XPathQuery(xmlView.TextEditor, GetNamespaces());
				XPathNodeMatch[] nodes = query.FindNodes(xpathComboBox.Text);
				if (nodes.Length > 0) {
					AddXPathResults(nodes);
					XPathNodeTextMarker marker = new XPathNodeTextMarker(xmlView.TextEditor.Document);
					marker.AddMarkers(nodes);
				} else {
					AddNoXPathResult();
				}
				AddXPathToHistory();
			} catch (XPathException xpathEx) {
				AddErrorResult(xpathEx);
			} catch (XmlException xmlEx) {
				AddErrorResult(xmlEx);
			} finally {
				BringResultsTabToFront();
			}
		}

		void ClearResults()
		{
			resultRows.Clear();
		}

		void BringResultsTabToFront()
		{
			tabControl.SelectedIndex = 0;
		}

		void AddXPathResults(XPathNodeMatch[] nodes)
		{
			foreach (XPathNodeMatch node in nodes) {
				string line = node.HasLineInfo() ? (node.LineNumber + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
				resultRows.Add(new XPathQueryResultRow { Match = node.DisplayValue, Line = line, Tag = node });
			}
		}

		void AddNoXPathResult()
		{
			resultRows.Add(new XPathQueryResultRow { Match = StringParser.Parse("${res:ICSharpCode.XmlEditor.XPathQueryPad.NoXPathResultsMessage}") });
		}

		void AddErrorResult(XmlException ex)
		{
			resultRows.Add(new XPathQueryResultRow { Match = ex.Message, Line = ex.LineNumber.ToString(CultureInfo.InvariantCulture), Tag = ex });
		}

		void AddErrorResult(XPathException ex)
		{
			string message = string.Concat(StringParser.Parse("${res:ICSharpCode.XmlEditor.XPathQueryPad.XPathLabel}"), " ", ex.Message);
			resultRows.Add(new XPathQueryResultRow { Match = message, Tag = ex });
		}

		void InitStrings()
		{
			matchColumnHeader.Header = StringParser.Parse("${res:ICSharpCode.XmlEditor.XPathQueryPad.XPathMatchColumnHeaderTitle}");
			lineColumnHeader.Header = StringParser.Parse("${res:Global.TextLine}");
			prefixColumn.Header = StringParser.Parse("${res:ICSharpCode.XmlEditor.XPathQueryPad.PrefixColumnHeaderTitle}");
			namespaceColumn.Header = StringParser.Parse("${res:ICSharpCode.XmlEditor.XPathQueryPad.NamespaceColumnHeaderTitle}");
			queryButton.Content = StringParser.Parse("${res:ICSharpCode.XmlEditor.XPathQueryPad.QueryButton}");
			xPathLabel.Text = StringParser.Parse("${res:ICSharpCode.XmlEditor.XPathQueryPad.XPathLabel}");
			xPathResultsTabPage.Header = StringParser.Parse("${res:ICSharpCode.XmlEditor.XPathQueryPad.ResultsTab}");
			namespacesTabPage.Header = StringParser.Parse("${res:ICSharpCode.XmlEditor.XPathQueryPad.NamespacesTab}");
		}

		/// <summary>
		/// Switches focus to the location of the XPath query result.
		/// </summary>
		void JumpToResultLocation()
		{
			MoveCaretToResultLocation(MoveCaret.ByJumping);
		}

		/// <summary>
		/// Scrolls the text editor so the location of the XPath query results is visible.
		/// </summary>
		void ScrollToResultLocation()
		{
			MoveCaretToResultLocation(MoveCaret.ByScrolling);
		}

		void MoveCaretToResultLocation(MoveCaret moveCaret)
		{
			if (xPathResultsListView.SelectedItem is XPathQueryResultRow row) {
				XPathNodeMatch xPathNodeMatch = row.Tag as XPathNodeMatch;
				XPathException xpathException = row.Tag as XPathException;
				XmlException xmlException = row.Tag as XmlException;
				if (xPathNodeMatch != null) {
					MoveCaretToXPathNodeMatch(moveCaret, xPathNodeMatch);
				} else if (xmlException != null) {
					MoveCaretToXmlException(moveCaret, xmlException);
				} else if (xpathException != null && moveCaret == MoveCaret.ByJumping) {
					xpathComboBox.Focus();
				}
			}
		}

		void MoveCaretToXPathNodeMatch(MoveCaret moveCaret, XPathNodeMatch node)
		{
			if (moveCaret == MoveCaret.ByJumping) {
				JumpTo(fileName, node.LineNumber, node.LinePosition);
			} else {
				ScrollTo(fileName, node.LineNumber, node.LinePosition, node.Value.Length);
			}
		}

		void MoveCaretToXmlException(MoveCaret moveCaret, XmlException ex)
		{
			int line = ex.LineNumber - 1;
			int column = ex.LinePosition - 1;
			if (moveCaret == MoveCaret.ByJumping) {
				JumpTo(fileName, line, column);
			} else {
				ScrollTo(fileName, line, column);
			}
		}

		static void JumpTo(string fileName, int line, int column)
		{
			FileService.JumpToFilePosition(fileName, line + 1, column + 1);
		}

		/// <summary>
		/// Scrolls to the specified line and column and also selects the given
		/// length of text at this location.
		/// </summary>
		static void ScrollTo(string filename, int line, int column, int length)
		{
			XmlView view = XmlView.ForFileName(filename);
			if (view != null) {
				ITextEditor editor = view.TextEditor;
				if (editor == null) return;
				int corLine = Math.Min(line + 1, editor.Document.LineCount - 1);
				editor.JumpTo(corLine, column + 1);
				if (length > 0 && line < editor.Document.LineCount) {
					int offset = editor.Document.PositionToOffset(line + 1, column + 1);
					editor.Select(offset, length);
				}
			}
		}

		static void ScrollTo(string fileName, int line, int column)
		{
			ScrollTo(fileName, line, column, 0);
		}

		/// <summary>
		/// Gets the namespaces and prefixes as a string array.
		/// </summary>
		string[] GetNamespaceStringArray()
		{
			var namespaces = new List<string>();
			foreach (XmlNamespace ns in GetNamespaces()) {
				namespaces.Add(ns.ToString());
			}
			return namespaces.ToArray();
		}

		/// <summary>
		/// Gets the previously used XPath queries from the combo box drop down list.
		/// </summary>
		string[] GetXPathHistory()
		{
			return new List<string>(xpathHistory).ToArray();
		}

		/// <summary>
		/// Adds the text in the combo box to the combo box drop down list.
		/// </summary>
		void AddXPathToHistory()
		{
			string newXPath = xpathComboBox.Text;
			if (!xpathHistory.Contains(newXPath)) {
				xpathHistory.Insert(0, newXPath);
				if (xpathHistory.Count > xpathQueryHistoryLimit) {
					xpathHistory.RemoveAt(xpathQueryHistoryLimit);
				}
			}
		}

		void XPathComboBoxKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Return) {
				RunXPathQuery();
			}
		}
	}
}
