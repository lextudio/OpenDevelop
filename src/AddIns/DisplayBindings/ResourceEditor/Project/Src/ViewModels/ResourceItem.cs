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
using System.Windows;
using System.Windows.Documents;
using ICSharpCode.SharpDevelop;

namespace ResourceEditor.ViewModels
{
	/// <summary>
	/// Defines the type of resource item supported by editor. Bitmap/Icon/Cursor entries are kept
	/// as distinct, typed kinds (matching the original WinForms-era model) but no longer backed
	/// by System.Drawing.Bitmap/Icon or System.Windows.Forms.Cursor - ResourceValue holds the raw
	/// image bytes instead (see ResourceEditor.csproj's header comment), and views decode them
	/// with WPF's own imaging APIs, the same approach UnoDevelop's IconCursorViewerViewContent
	/// uses for standalone .ico/.cur files.
	/// </summary>
	public enum ResourceItemEditorType
	{
		Unknown,
		String,
		Boolean,
		Bitmap,
		Icon,
		Cursor,
		Binary
	}

	public class ResourceItem : DependencyObject, INotifyPropertyChanged
	{
		ResourceItemEditorType resourceType;
		ResourceEditorViewModel resourceEditor;
		string nameBeforeEditing;
		string highlightText;

		public ResourceItem(ResourceEditorViewModel resourceEditor, string name, object resourceValue)
			: this(resourceEditor, name, resourceValue, null, ResourceItemEditorType.Unknown)
		{
		}

		public ResourceItem(ResourceEditorViewModel resourceEditor, string name, object resourceValue, string comment)
			: this(resourceEditor, name, resourceValue, comment, ResourceItemEditorType.Unknown)
		{
		}

		/// <param name="resourceType">
		/// Explicit kind for the value, needed because a raw byte[] alone can't tell Bitmap/Icon/
		/// Cursor/Binary apart the way a CLR Bitmap/Icon/Cursor object used to. Pass
		/// <see cref="ResourceItemEditorType.Unknown"/> to infer from the CLR type of
		/// <paramref name="resourceValue"/> instead (string/bool -&gt; String/Boolean, byte[] -&gt;
		/// Binary).
		/// </param>
		public ResourceItem(ResourceEditorViewModel resourceEditor, string name, object resourceValue, string comment, ResourceItemEditorType resourceType)
		{
			this.resourceEditor = resourceEditor;
			this.Name = name;
			this.SortingCriteria = name;
			this.ResourceValue = resourceValue;
			this.resourceType = resourceType != ResourceItemEditorType.Unknown ? resourceType : GetResourceTypeFromValue(resourceValue);
			this.Comment = comment;
			this.RichComment = comment;
		}

		/// <summary>
		/// The literal .resx "type" attribute this entry was read with (e.g.
		/// "System.Drawing.Bitmap, System.Drawing"), preserved so SaveFile can write the same
		/// type attribute back. Null for plain string/boolean entries.
		/// </summary>
		public string OriginalResXType { get; set; }

		#region INotifyPropertyChanged implementation

		public event PropertyChangedEventHandler PropertyChanged;

		void RaisePropertyChanged(string name)
		{
			if (PropertyChanged != null) {
				PropertyChanged(this, new PropertyChangedEventArgs(name));
			}
		}

		#endregion

		public static readonly DependencyProperty NameProperty =
			DependencyProperty.Register("Name", typeof(string), typeof(ResourceItem),
				new FrameworkPropertyMetadata(NamePropertyChanged));

		static void NamePropertyChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
		{
			((ResourceItem)obj).Highlight(((ResourceItem)obj).highlightText);
		}

		public string Name {
			get { return (string)GetValue(NameProperty); }
			set { SetValue(NameProperty, value); }
		}

		public static readonly DependencyProperty DisplayNameProperty =
			DependencyProperty.Register("DisplayName", typeof(object), typeof(ResourceItem),
			                            new FrameworkPropertyMetadata());

		public object DisplayName {
			get { return (object)GetValue(DisplayNameProperty); }
			set { SetValue(DisplayNameProperty, value); }
		}

		public static readonly DependencyProperty SortingCriteriaProperty =
			DependencyProperty.Register("SortingCriteria", typeof(string), typeof(ResourceItem),
				new FrameworkPropertyMetadata());

		public string SortingCriteria {
			get { return (string)GetValue(SortingCriteriaProperty); }
			set { SetValue(SortingCriteriaProperty, value); }
		}

		public static readonly DependencyProperty ResourceValueProperty =
			DependencyProperty.Register("ResourceValue", typeof(object), typeof(ResourceItem),
				new FrameworkPropertyMetadata());

		public object ResourceValue {
			get { return (object)GetValue(ResourceValueProperty); }
			set { SetValue(ResourceValueProperty, value); Highlight(highlightText); }
		}

		public string DisplayedResourceType {
			get {
				return ResourceValue == null ? "(Nothing/null)" : (OriginalResXType ?? ResourceValue.GetType().FullName);
			}
		}

		public ResourceItemEditorType ResourceType {
			get {
				return resourceType;
			}
		}

		public static readonly DependencyProperty IsEditingProperty =
			DependencyProperty.Register("IsEditing", typeof(bool), typeof(ResourceItem),
				new FrameworkPropertyMetadata());

		public bool IsEditing {
			get { return (bool)GetValue(IsEditingProperty); }
			set { SetValue(IsEditingProperty, value); }
		}

		public bool IsNew {
			get;
			set;
		}

		protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
		{
			base.OnPropertyChanged(e);

			if (e.Property.Name == DisplayNameProperty.Name
			    || e.Property.Name == RichContentProperty.Name
			    || e.Property.Name == RichCommentProperty.Name) {
				return;
			}

			if (e.Property.Name == ResourceValueProperty.Name) {
				// Update content property as well
				RaisePropertyChanged("Content");
				Highlight(highlightText);
			}
			if (e.Property.Name == IsEditingProperty.Name) {
				bool previouslyEditing = (bool)e.OldValue;
				bool isEditing = (bool)e.NewValue;
				if (!previouslyEditing && isEditing) {
					// Save initial name to compare it later on cancellation
					nameBeforeEditing = Name;
				} else if (previouslyEditing && !isEditing) {
					// Make dirty, if name has changed after finishing edit
					if (nameBeforeEditing != Name) {
						// New name
						if (String.IsNullOrEmpty(Name)) {
							// Empty name is not valid -> revert silently
							Name = nameBeforeEditing;
						} else if (resourceEditor.ContainsResourceName(Name)) {
							// Resource names must be unique -> revert and show message
							SD.MessageService.ShowWarning("${res:ResourceEditor.ResourceList.KeyAlreadyDefinedWarning}");
							Name = nameBeforeEditing;
						} else {
							// New name seems to be valid
							SortingCriteria = Name;
							resourceEditor.MakeDirty();
						}
					}
					IsNew = false;
				}
			} else {
				if (e.Property.Name == NameProperty.Name)
					SortingCriteria = Name;
				resourceEditor.MakeDirty();
			}
		}

		static ResourceItemEditorType GetResourceTypeFromValue(object val)
		{
			switch (val) {
				case null:
					return ResourceItemEditorType.Unknown;
				case string:
					return ResourceItemEditorType.String;
				case bool:
					return ResourceItemEditorType.Boolean;
				case byte[]:
					return ResourceItemEditorType.Binary;
				default:
					return ResourceItemEditorType.Unknown;
			}
		}

		public string Content {
			get {
				return ToString();
			}
		}

		public static readonly DependencyProperty RichContentProperty =
			DependencyProperty.Register("RichContent", typeof(object), typeof(ResourceItem),
			                            new FrameworkPropertyMetadata());

		public object RichContent {
			get { return (object)GetValue(RichContentProperty); }
			set { SetValue(RichContentProperty, value); }
		}

		public static readonly DependencyProperty CommentProperty =
			DependencyProperty.Register("Comment", typeof(string), typeof(ResourceItem),
				new FrameworkPropertyMetadata(""));

		public string Comment {
			get { return (string)GetValue(CommentProperty); }
			set { SetValue(CommentProperty, value); }
		}

		public static readonly DependencyProperty RichCommentProperty =
			DependencyProperty.Register("RichComment", typeof(object), typeof(ResourceItem),
			                            new FrameworkPropertyMetadata());

		public object RichComment {
			get { return (object)GetValue(RichCommentProperty); }
			set { SetValue(RichCommentProperty, value); }
		}

		public override string ToString()
		{
			if (ResourceValue == null) {
				return "(Nothing/null)";
			}

			switch (resourceType) {
				case ResourceItemEditorType.String:
					return (string)ResourceValue;
				case ResourceItemEditorType.Boolean:
					return ResourceValue.ToString();
				case ResourceItemEditorType.Bitmap:
				case ResourceItemEditorType.Icon:
				case ResourceItemEditorType.Cursor:
				case ResourceItemEditorType.Binary:
					return "[Size = " + ((byte[])ResourceValue).Length + "]";
				default:
					return ResourceValue.ToString();
			}
		}

		/// <summary>
		/// Replaces this item's value with the raw bytes of a file the user picks (used by the
		/// Bitmap/Icon/Cursor item views' "update from file" link). Reads raw bytes only - no
		/// System.Drawing/System.Windows.Forms constructors, matching how the value is stored.
		/// </summary>
		public bool UpdateFromFile()
		{
			var fileDialog = new Microsoft.Win32.OpenFileDialog {
				AddExtension = true,
				Filter = "All files (*.*)|*.*",
				CheckFileExists = true,
			};

			if (fileDialog.ShowDialog() != true)
				return false;

			try {
				ResourceValue = System.IO.File.ReadAllBytes(fileDialog.FileName);
				return true;
			} catch (Exception ex) {
				SD.MessageService.ShowWarningFormatted("${res:ResourceEditor.Messages.CantLoadResourceFromFile}", ex.Message);
				return false;
			}
		}

		public void Highlight(string text)
		{
			this.highlightText = text;
			if (string.IsNullOrEmpty(text)) {
				DisplayName = Name;
				RichContent = Content;
				RichComment = Comment;
			} else {
				DisplayName = CreateSpan(Name, text);
				RichContent = CreateSpan(Content ?? "", text);
				RichComment = CreateSpan(Comment ?? "", text);
			}
			RaisePropertyChanged("DisplayName");
			RaisePropertyChanged("RichContent");
			RaisePropertyChanged("RichComment");
		}

		Span CreateSpan(string text, string matchText)
		{
			int startIndex = 0;
			int match;
			var span = new Span();
			do {
				match = text.IndexOf(matchText, startIndex, StringComparison.OrdinalIgnoreCase);
				if (match > -1) {
					span.Inlines.Add(new Run(text.Substring(startIndex, match - startIndex)));
					span.Inlines.Add(new Span(new Run(text.Substring(match, matchText.Length))) {
						Background = System.Windows.Media.Brushes.Yellow
					});
				} else {
					span.Inlines.Add(new Run(text.Substring(startIndex, text.Length - startIndex)));
				}
				startIndex = match + matchText.Length;
			} while (match > -1);
			return span;
		}
	}
}
