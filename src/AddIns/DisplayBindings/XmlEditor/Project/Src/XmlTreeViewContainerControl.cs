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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.WinForms;

using XceedPropertyGrid = Xceed.Wpf.Toolkit.PropertyGrid.PropertyGrid;
using XceedPropertyItem = Xceed.Wpf.Toolkit.PropertyGrid.PropertyItem;
using XceedPropertyValueChangedEventArgs = Xceed.Wpf.Toolkit.PropertyGrid.PropertyValueChangedEventArgs;

namespace ICSharpCode.XmlEditor
{
	/// <summary>
	/// This user control holds both the XmlTreeViewControl and the
	/// attributes property grid in a split container.
	/// </summary>
	public class XmlTreeViewContainerControl : UserControl, IXmlTreeView, IOwnerState, IClipboardHandler
	{
		XmlTreeEditor editor;
		bool dirty;
		bool errorMessageTextBoxVisible;
		bool attributesGridVisible = true;

		XmlTreeViewControl xmlElementTreeView;
		XceedPropertyGrid attributesGrid;
		TextBox errorMessageTextBox;
		TextBox textBox;

		[Flags]
		internal enum XmlTreeViewContainerControlStates {
			None                = 0,
			ElementSelected     = 1,
			RootElementSelected = 2,
			AttributeSelected   = 4,
			TextNodeSelected    = 8,
			CommentSelected     = 16
		}

		public event EventHandler DirtyChanged;

		public XmlTreeViewContainerControl()
			: this(new XmlSchemaCompletionCollection(), null)
		{
		}

		public XmlTreeViewContainerControl(XmlSchemaCompletionCollection schemas, XmlSchemaCompletion defaultSchema)
		{
			InitializeComponent();
			editor = new XmlTreeEditor(this, schemas, defaultSchema);
		}

		/// <summary>
		/// Gets the current XmlTreeViewContainerControlState.
		/// </summary>
		public Enum InternalState {
			get {
				XmlTreeViewContainerControlStates state = XmlTreeViewContainerControlStates.None;
				if (SelectedElement != null) {
					state |= XmlTreeViewContainerControlStates.ElementSelected;
					if (SelectedElement == Document.DocumentElement) {
						state |= XmlTreeViewContainerControlStates.RootElementSelected;
					}
				}
				if (SelectedAttribute != null) {
					state |= XmlTreeViewContainerControlStates.AttributeSelected;
				}
				if (SelectedTextNode != null) {
					state = XmlTreeViewContainerControlStates.TextNodeSelected;
				}
				if (SelectedComment != null) {
					state = XmlTreeViewContainerControlStates.CommentSelected;
				}
				return state;
			}
		}

		/// <summary>
		/// Gets the property grid that displays attributes for the selected xml element.
		/// </summary>
		public XceedPropertyGrid AttributesGrid {
			get { return attributesGrid; }
		}

		/// <summary>
		/// Gets or sets whether the xml document needs saving.
		/// </summary>
		public bool IsDirty {
			get { return dirty; }
			set {
				bool previousDirty = dirty;
				dirty = value;
				OnXmlChanged(previousDirty);
			}
		}

		/// <summary>
		/// Gets or sets the error message to display.
		/// </summary>
		public string ErrorMessage {
			get { return errorMessageTextBox.Text; }
			set { errorMessageTextBox.Text = value; }
		}

		/// <summary>
		/// Gets or sets whether the error message is visible. When visible the
		/// error message text box replaces the property grid.
		/// </summary>
		public bool IsErrorMessageTextBoxVisible {
			get { return errorMessageTextBoxVisible; }
			set {
				errorMessageTextBoxVisible = value;
				if (value) {
					errorMessageTextBox.Visibility = Visibility.Visible;
					IsAttributesGridVisible = false;
					IsTextBoxVisible = false;
				} else {
					errorMessageTextBox.Visibility = Visibility.Collapsed;
				}
			}
		}

		/// <summary>
		/// Gets the XmlTreeView in the container.
		/// </summary>
		public XmlTreeViewControl TreeView {
			get { return xmlElementTreeView; }
		}

		public void ShowXmlIsNotWellFormedMessage(XmlException ex)
		{
			ShowErrorMessage(ex.Message);
		}

		public void ShowErrorMessage(string message)
		{
			xmlElementTreeView.Nodes.Clear();
			ErrorMessage = message;
			IsErrorMessageTextBoxVisible = true;
		}

		public void LoadXml(string xml)
		{
			textBox.Clear();
			IsAttributesGridVisible = true;
			ClearAttributes();

			editor.LoadXml(xml);

			ExpandRootDocumentElementNode();
		}

		void ExpandRootDocumentElementNode()
		{
			if (xmlElementTreeView.Nodes.Count > 0) {
				xmlElementTreeView.Nodes[0].Expand();
			}
		}

		public void ExpandAll()
		{
			ExpandAll(xmlElementTreeView.SelectedNode);
		}

		public void CollapseAll()
		{
			CollapseAll(xmlElementTreeView.SelectedNode);
		}

		static void ExpandAll(XmlTreeNode node)
		{
			if (node == null) return;
			node.IsExpanded = true;
			foreach (var child in node.Nodes) {
				ExpandAll(child);
			}
		}

		static void CollapseAll(XmlTreeNode node)
		{
			if (node == null) return;
			node.IsExpanded = false;
		}

		/// <summary>
		/// Gets or sets the xml document to be shown in this container control.
		/// </summary>
		public XmlDocument Document {
			get { return editor.Document; }
			set { xmlElementTreeView.Document = value; }
		}

		/// <summary>
		/// Shows the attributes.
		/// </summary>
		public void ShowAttributes(XmlAttributeCollection attributes)
		{
			IsAttributesGridVisible = true;
			attributesGrid.SelectedObject = new XmlAttributeTypeDescriptor(attributes);
		}

		/// <summary>
		/// Clears all the attributes currently on display.
		/// </summary>
		public void ClearAttributes()
		{
			attributesGrid.SelectedObject = null;
		}

		/// <summary>
		/// Shows the xml element's text content after the user has selected the text node.
		/// </summary>
		public void ShowTextContent(string text)
		{
			IsTextBoxVisible = true;
			textBox.Text = text;
		}

		/// <summary>
		/// Gets or sets the text of the text node or comment node currently on display.
		/// </summary>
		public string TextContent {
			get { return textBox.Text.Replace("\n", "\r\n"); }
			set { textBox.Text = value; }
		}

		/// <summary>
		/// Gets the currently selected node based on what is selected in
		/// the tree. This does not return the selected attribute.
		/// </summary>
		public XmlNode SelectedNode {
			get {
				XmlElement selectedElement = SelectedElement;
				if (selectedElement != null) {
					return selectedElement;
				}

				XmlText selectedTextNode = SelectedTextNode;
				if (selectedTextNode != null) {
					return selectedTextNode;
				}

				return SelectedComment;
			}
		}

		public XmlElement SelectedElement {
			get { return xmlElementTreeView.SelectedElement; }
		}

		public XmlText SelectedTextNode {
			get { return xmlElementTreeView.SelectedTextNode; }
		}

		public XmlComment SelectedComment {
			get { return xmlElementTreeView.SelectedComment; }
		}

		/// <summary>
		/// Gets the name of the attribute currently selected.
		/// </summary>
		public string SelectedAttribute {
			get {
				if (IsAttributesGridVisible && attributesGrid.SelectedPropertyItem is XceedPropertyItem item) {
					return item.PropertyDescriptor?.Name;
				}
				return null;
			}
		}

		/// <summary>
		/// Shows the add attribute dialog so the user can add a new attribute to the XML tree.
		/// </summary>
		public void AddAttribute()
		{
			editor.AddAttribute();
		}

		public string[] SelectNewAttributes(string[] attributes)
		{
			using (IAddXmlNodeDialog addAttributeDialog = CreateAddAttributeDialog(attributes)) {
				if (addAttributeDialog.ShowDialog() == AddXmlNodeDialogResult.OK) {
					return addAttributeDialog.GetNames();
				}
				return new string[0];
			}
		}

		public void RemoveAttribute()
		{
			editor.RemoveAttribute();
		}

		public string[] SelectNewElements(string[] elements)
		{
			using (IAddXmlNodeDialog addElementDialog = CreateAddElementDialog(elements)) {
				if (addElementDialog.ShowDialog() == AddXmlNodeDialogResult.OK) {
					return addElementDialog.GetNames();
				}
				return new string[0];
			}
		}

		public void AppendChildElement(XmlElement element)
		{
			xmlElementTreeView.AppendChildElement(element);
		}

		public void AddChildElement()
		{
			editor.AppendChildElement();
		}

		public void InsertElementBefore()
		{
			editor.InsertElementBefore();
		}

		public void InsertElementBefore(XmlElement element)
		{
			xmlElementTreeView.InsertElementBefore(element);
		}

		public void InsertElementAfter()
		{
			editor.InsertElementAfter();
		}

		public void InsertElementAfter(XmlElement element)
		{
			xmlElementTreeView.InsertElementAfter(element);
		}

		public void RemoveElement(XmlElement element)
		{
			xmlElementTreeView.RemoveElement(element);
		}

		public void AppendChildTextNode(XmlText textNode)
		{
			xmlElementTreeView.AppendChildTextNode(textNode);
		}

		public void AppendChildTextNode()
		{
			editor.AppendChildTextNode();
		}

		public void InsertTextNodeBefore()
		{
			editor.InsertTextNodeBefore();
		}

		public void InsertTextNodeBefore(XmlText textNode)
		{
			xmlElementTreeView.InsertTextNodeBefore(textNode);
		}

		public void InsertTextNodeAfter()
		{
			editor.InsertTextNodeAfter();
		}

		public void InsertTextNodeAfter(XmlText textNode)
		{
			xmlElementTreeView.InsertTextNodeAfter(textNode);
		}

		public void RemoveTextNode(XmlText textNode)
		{
			xmlElementTreeView.RemoveTextNode(textNode);
		}

		public void UpdateTextNode(XmlText textNode)
		{
			xmlElementTreeView.UpdateTextNode(textNode);
		}

		public void UpdateComment(XmlComment comment)
		{
			xmlElementTreeView.UpdateComment(comment);
		}

		public void AppendChildComment(XmlComment comment)
		{
			xmlElementTreeView.AppendChildComment(comment);
		}

		public void AppendChildComment()
		{
			editor.AppendChildComment();
		}

		public void RemoveComment(XmlComment comment)
		{
			xmlElementTreeView.RemoveComment(comment);
		}

		public void InsertCommentBefore(XmlComment comment)
		{
			xmlElementTreeView.InsertCommentBefore(comment);
		}

		public void InsertCommentBefore()
		{
			editor.InsertCommentBefore();
		}

		public void InsertCommentAfter(XmlComment comment)
		{
			xmlElementTreeView.InsertCommentAfter(comment);
		}

		public void InsertCommentAfter()
		{
			editor.InsertCommentAfter();
		}

		public void ShowCut(XmlNode node)
		{
			xmlElementTreeView.ShowCut(node);
		}

		public void HideCut(XmlNode node)
		{
			xmlElementTreeView.HideCut(node);
		}

		#region IClipboardHandler implementation

		public bool EnableCut {
			get { return editor.IsCutEnabled; }
		}

		public bool EnableCopy {
			get { return editor.IsCopyEnabled; }
		}

		public bool EnablePaste {
			get { return editor.IsPasteEnabled; }
		}

		public bool EnableDelete {
			get { return editor.IsDeleteEnabled; }
		}

		/// <summary>
		/// Currently not possible to select all tree nodes so this always returns false.
		/// </summary>
		public bool EnableSelectAll {
			get { return false; }
		}

		public void Cut()
		{
			editor.Cut();
		}

		public void Copy()
		{
			editor.Copy();
		}

		public void Paste()
		{
			editor.Paste();
		}

		public void Delete()
		{
			editor.Delete();
		}

		/// <summary>
		/// Selects all tree nodes. Currently not supported.
		/// </summary>
		public void SelectAll()
		{
		}

		#endregion

		/// <summary>
		/// Creates a new AddElementDialog.
		/// </summary>
		protected virtual IAddXmlNodeDialog CreateAddElementDialog(string[] elementNames)
		{
			AddXmlNodeDialog dialog = new AddXmlNodeDialog(elementNames);
			dialog.Title = StringParser.Parse("${res:ICSharpCode.XmlEditor.AddElementDialog.Title}");
			dialog.CustomNameLabelText = StringParser.Parse("${res:ICSharpCode.XmlEditor.AddElementDialog.CustomElementLabel}");
			return dialog;
		}

		/// <summary>
		/// Creates a new AddAttributeDialog.
		/// </summary>
		protected virtual IAddXmlNodeDialog CreateAddAttributeDialog(string[] attributeNames)
		{
			AddXmlNodeDialog dialog = new AddXmlNodeDialog(attributeNames);
			dialog.Title = StringParser.Parse("${res:ICSharpCode.XmlEditor.AddAttributeDialog.Title}");
			dialog.CustomNameLabelText = StringParser.Parse("${res:ICSharpCode.XmlEditor.AddAttributeDialog.CustomAttributeLabel}");
			return dialog;
		}

		/// <summary>
		/// Handles a keyboard press event in tree view.
		/// </summary>
		protected void XmlElementTreeViewKeyPressed(object source, XmlTreeViewKeyPressedEventArgs e)
		{
			bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
			if (e.KeyData == Key.Delete)
				Delete();
			else if (ctrl && e.KeyData == Key.C)
				Copy();
			else if (ctrl && e.KeyData == Key.X)
				Cut();
			else if (ctrl && e.KeyData == Key.V)
				Paste();
			else if (ctrl && e.KeyData == Key.A)
				SelectAll();
		}

		void InitializeComponent()
		{
			xmlElementTreeView = new XmlTreeViewControl();
			xmlElementTreeView.SelectedItemChanged += (s, e) => XmlElementTreeViewAfterSelect();
			xmlElementTreeView.TreeViewKeyPressed += XmlElementTreeViewKeyPressed;

			attributesGrid = new XceedPropertyGrid { IsCategorized = true };
			attributesGrid.PropertyValueChanged += AttributesGridPropertyValueChanged;

			errorMessageTextBox = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Visibility = Visibility.Collapsed };
			textBox = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
			textBox.TextChanged += TextBoxTextChanged;

			var rightPanel = new Grid();
			rightPanel.Children.Add(attributesGrid);
			rightPanel.Children.Add(errorMessageTextBox);
			rightPanel.Children.Add(textBox);

			var grid = new Grid();
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });

			var splitter = new GridSplitter { Width = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
			Grid.SetColumn(xmlElementTreeView, 0);
			Grid.SetColumn(splitter, 1);
			Grid.SetColumn(rightPanel, 2);

			grid.Children.Add(xmlElementTreeView);
			grid.Children.Add(splitter);
			grid.Children.Add(rightPanel);

			Content = grid;

			IsAttributesGridVisible = true;
		}

		/// <summary>
		/// This method is protected only so we can easily test what happens when this method is
		/// called. Triggering a TextChanged event is difficult to do from unit tests.
		/// </summary>
		protected void TextBoxTextChanged(object sender, TextChangedEventArgs e)
		{
			if (editor != null) {
				bool previousIsDirty = dirty;
				editor.TextContentChanged();
				OnXmlChanged(previousIsDirty);
			}
		}

		protected void XmlElementTreeViewAfterSelect()
		{
			editor.SelectedNodeChanged();
		}

		protected void AttributesGridPropertyValueChanged(object sender, XceedPropertyValueChangedEventArgs e)
		{
			bool previousIsDirty = dirty;
			editor.AttributeValueChanged();
			OnXmlChanged(previousIsDirty);
		}

		/// <summary>
		/// Raises the dirty changed event if the dirty flag has changed.
		/// </summary>
		void OnXmlChanged(bool previousIsDirty)
		{
			if (previousIsDirty != dirty) {
				OnDirtyChanged();
			}
		}

		void OnDirtyChanged()
		{
			DirtyChanged?.Invoke(this, new EventArgs());
		}

		/// <summary>
		/// Gets or sets whether the attributes grid is visible.
		/// </summary>
		bool IsAttributesGridVisible {
			get { return attributesGridVisible; }
			set {
				attributesGridVisible = value;
				if (value) {
					attributesGrid.Visibility = Visibility.Visible;
					IsTextBoxVisible = false;
					IsErrorMessageTextBoxVisible = false;
				} else {
					attributesGrid.Visibility = Visibility.Collapsed;
				}
			}
		}

		/// <summary>
		/// Gets or sets whether the text node text box is visible.
		/// </summary>
		bool IsTextBoxVisible {
			set {
				if (value) {
					textBox.Visibility = Visibility.Visible;
					IsAttributesGridVisible = false;
					IsErrorMessageTextBoxVisible = false;
				} else {
					textBox.Visibility = Visibility.Collapsed;
				}
			}
		}
	}
}
