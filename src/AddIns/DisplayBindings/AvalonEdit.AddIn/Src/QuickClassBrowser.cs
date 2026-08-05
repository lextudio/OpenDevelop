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

// Backend-neutral class/member navigation using ILanguageService.GetDocumentOutlineAsync.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows;

using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Project;
using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// Panel with two combo boxes. Used to quickly navigate to entities in the current file.
	/// </summary>
	public partial class QuickClassBrowser : UserControl
	{
		/// <summary>
		/// ViewModel used for combobox items.
		/// </summary>
		class EntityItem : IComparable<EntityItem>, System.ComponentModel.INotifyPropertyChanged
		{
			readonly DocumentOutlineNode entity;
			ImageSource image;
			string text;

			public DocumentOutlineNode Entity {
				get { return entity; }
			}

			public EntityItem(DocumentOutlineNode node)
			{
				this.IsInSamePart = true;
				this.entity = node;
				this.text = node.Name;
				this.image = GetImage(node);
			}

			static ImageSource GetImage(DocumentOutlineNode node)
			{
				var image = node.Kind switch {
					"Interface" => CompletionImage.Interface,
					"Struct" or "Structure" => CompletionImage.Struct,
					"Enum" => CompletionImage.Enum,
					"Delegate" => CompletionImage.Delegate,
					"Method" or "Function" or "Constructor" => CompletionImage.Method,
					"Field" => CompletionImage.Field,
					"Property" => CompletionImage.Property,
					"Event" => CompletionImage.Event,
					_ => CompletionImage.Class
				};
				return image.GetImage(ICSharpCode.TypeSystem.Accessibility.Public, false);
			}

			/// <summary>
			/// Text to display in combo box.
			/// </summary>
			public string Text {
				get { return text; }
			}

			/// <summary>
			/// Image to use in combox box
			/// </summary>
			public ImageSource Image {
				get {
					return image;
				}
			}

			/// <summary>
			/// Gets/Sets whether the item is in the current file.
			/// </summary>
			/// <returns>
			/// <c>true</c>: item is in current file;
			/// <c>false</c>: item is in another part of the partial class
			/// </returns>
			public bool IsInSamePart { get; set; }

			public int CompareTo(EntityItem other)
			{
				int r = string.Compare(this.Entity.Kind, other.Entity.Kind, StringComparison.Ordinal);
				if (r != 0)
					return r;
				r = string.Compare(text, other.text, StringComparison.OrdinalIgnoreCase);
				if (r != 0)
					return r;
				return string.Compare(text, other.text, StringComparison.Ordinal);
			}

			/// <summary>
			/// ToString override is necessary to support keyboard navigation in WPF
			/// </summary>
			public override string ToString()
			{
				return text;
			}

			// I'm not sure if it actually was a leak or caused by something else, but I saw QCB.EntityItem being alive for longer
			// than it should when looking at the heap with WinDbg.
			// Maybe this was caused by http://support.microsoft.com/kb/938416/en-us, so I'm adding INotifyPropertyChanged to be sure.
			event System.ComponentModel.PropertyChangedEventHandler System.ComponentModel.INotifyPropertyChanged.PropertyChanged {
				add { }
				remove { }
			}
		}

		public QuickClassBrowser()
		{
			InitializeComponent();
		}

		/// <summary>
		/// Updates the list of available classes.
		/// This causes the classes combo box to lose its current selection,
		/// so the members combo box will be cleared.
		/// </summary>
		public void Update(FileName fileName)
		{
			currentFileName = fileName;
			runUpdateWhenDropDownClosed = true;
			runUpdateWhenDropDownClosedFile = fileName;
			if (!IsDropDownOpen)
				ComboBox_DropDownClosed(null, null);
		}

		// The lists of items currently visible in the combo boxes.
		// These should never be null.
		List<EntityItem> classItems = new List<EntityItem>();
		List<EntityItem> memberItems = new List<EntityItem>();
		IProject currentProject;
		FileName currentFileName;
		bool updatingTargetFrameworks;

		void DoUpdate(FileName fileName)
		{
			UpdateTargetFrameworks(fileName);
			classItems = new List<EntityItem>();
			classItems.Sort();
			classComboBox.ItemsSource = classItems;
			if (fileName != null) {
				// The outline round-trip is async (Roslyn in-process for C#/VB, an LSP
				// textDocument/documentSymbol request for F#/XAML/etc.). Blocking the UI thread on
				// .GetResult() deadlocks whenever the language service's continuations need the
				// dispatcher (the same trap as LanguageServiceParserAdapter's upsert) - so fetch on
				// a background thread and apply back on the UI thread, re-selecting the caret item
				// once the class list is actually populated.
				_ = FetchClassesAsync(fileName);
			}
		}

		async Task FetchClassesAsync(FileName fileName)
		{
			try {
				var registry = SD.GetService<LanguageServiceRegistry>();
				if (registry == null || !registry.TryGetService(fileName, out var service))
					return;
				var documentId = new ICSharpCode.SharpDevelop.LanguageServices.DocumentId(fileName);
				// Capture editor state on the UI thread before yielding (TextDocument is
				// owner-thread-bound); null means "no matching open editor", keep the server's
				// existing buffer.
				var editor = SD.Workbench.ActiveViewContent?.GetService<ITextEditor>();
				string text = editor != null && FileName.Equals(editor.FileName, fileName) ? editor.Document.Text : null;
				if (text != null)
					await service.UpsertDocumentAsync(documentId, text, System.Threading.CancellationToken.None).ConfigureAwait(false);
				var outline = await service.GetDocumentOutlineAsync(documentId, System.Threading.CancellationToken.None).ConfigureAwait(false);
				await SD.MainThread.InvokeAsync(() => {
					if (currentFileName != fileName)
						return;
					classItems = new List<EntityItem>();
					AddClasses(outline);
					classItems.Sort();
					classComboBox.ItemsSource = classItems;
					var caret = SD.Workbench.ActiveViewContent?.GetService<ITextEditor>()?.Caret.Location;
					if (caret.HasValue)
						DoSelectItem(caret.Value);
				});
			} catch (Exception ex) {
				LoggingService.Warn("Navigation bar outline failed for '" + fileName + "': " + ex.Message);
			}
		}

		void UpdateTargetFrameworks(FileName fileName)
		{
			currentProject = fileName == null ? null : SD.ProjectService.FindProjectContainingFile(fileName);
			var targetFrameworks = currentProject == null
				? Array.Empty<string>()
				: ProjectTargetFrameworkService.GetTargetFrameworks(currentProject);

			updatingTargetFrameworks = true;
			try {
				targetFrameworkComboBox.ItemsSource = targetFrameworks;
				targetFrameworkComboBox.Visibility = targetFrameworks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
				targetFrameworkComboBox.SelectedItem = currentProject == null
					? null
					: ProjectTargetFrameworkService.GetActiveTargetFramework(currentProject);
			} finally {
				updatingTargetFrameworks = false;
			}
		}

		void TargetFrameworkComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (updatingTargetFrameworks || currentProject == null || targetFrameworkComboBox.SelectedItem is not string targetFramework)
				return;

			ProjectTargetFrameworkService.SetActiveTargetFramework(currentProject, targetFramework);
			// RefreshProjectAsync can be a real round-trip (Roslyn reload for C#/VB; a no-op for
			// LSP-backed languages like F#) - never block the UI thread on it, then re-fetch the
			// outline afterwards.
			_ = RefreshProjectAndOutlineAsync(currentFileName);
		}

		async Task RefreshProjectAndOutlineAsync(FileName fileName)
		{
			try {
				var registry = SD.GetService<LanguageServiceRegistry>();
				if (fileName != null && registry != null && registry.TryGetService(fileName, out var service))
					await service.RefreshProjectAsync(new ICSharpCode.SharpDevelop.LanguageServices.DocumentId(fileName), System.Threading.CancellationToken.None).ConfigureAwait(false);
			} catch (Exception ex) {
				LoggingService.Warn("Navigation bar project refresh failed: " + ex.Message);
			}
			await SD.MainThread.InvokeAsync(() => {
				if (currentFileName != null)
					Update(currentFileName);
			});
		}

		bool IsDropDownOpen {
			get { return targetFrameworkComboBox.IsDropDownOpen || classComboBox.IsDropDownOpen || membersComboBox.IsDropDownOpen; }
		}

		// Delayed execution - avoid changing combo boxes while the user is browsing the dropdown list.
		bool runUpdateWhenDropDownClosed;
		FileName runUpdateWhenDropDownClosedFile;
		bool runSelectItemWhenDropDownClosed;
		TextLocation runSelectItemWhenDropDownClosedLocation;

		void ComboBox_DropDownClosed(object sender, EventArgs e)
		{
			if (runUpdateWhenDropDownClosed) {
				runUpdateWhenDropDownClosed = false;
				DoUpdate(runUpdateWhenDropDownClosedFile);
				runUpdateWhenDropDownClosedFile = null;
			}
			if (runSelectItemWhenDropDownClosed) {
				runSelectItemWhenDropDownClosed = false;
				DoSelectItem(runSelectItemWhenDropDownClosedLocation);
			}
			if (sender == classComboBox) {
				classComboBoxSelectionChanged(sender, null);
			}
			if (sender == membersComboBox) {
				membersComboBoxSelectionChanged(sender, null);
			}
		}

		void AddClasses(IEnumerable<DocumentOutlineNode> classes)
		{
			foreach (var c in classes) {
				classItems.Add(new EntityItem(c));
			}
		}

		/// <summary>
		/// Selects the class and member closest to the specified location.
		/// </summary>
		public void SelectItemAtCaretPosition(TextLocation location)
		{
			runSelectItemWhenDropDownClosed = true;
			runSelectItemWhenDropDownClosedLocation = location;
			if (!IsDropDownOpen)
				ComboBox_DropDownClosed(null, null);
		}

		static bool IsInside(ICSharpCode.SharpDevelop.LanguageServices.TextSpan span, int line, int column)
		{
			int beginLine = span.Start.Line, beginColumn = span.Start.Column;
			int endLine = span.End.Line, endColumn = span.End.Column;
			return line >= beginLine && (line <= endLine)
				&& (line != beginLine || column >= beginColumn)
				&& (line != endLine || column <= endColumn);
		}

		void DoSelectItem(TextLocation location)
		{
			EntityItem matchInside = null;
			EntityItem nearestMatch = null;
			int nearestMatchDistance = int.MaxValue;
			foreach (EntityItem item in classItems) {
				if (item.IsInSamePart) {
					var span = item.Entity.ExtentSpan;
					int beginLine = span.Start.Line, endLine = span.End.Line;
					if (IsInside(span, location.Line, location.Column)) {
						matchInside = item;
						// when there are multiple matches inside (nested classes), use the last one
					} else {
						// Not a perfect match?
						// Try to first the nearest match. We want the classes combo box to always
						// have a class selected if possible.
						int matchDistance = Math.Min(Math.Abs(location.Line - beginLine), Math.Abs(location.Line - endLine));
						if (matchDistance < nearestMatchDistance) {
							nearestMatchDistance = matchDistance;
							nearestMatch = item;
						}
					}
				}
			}
			jumpOnSelectionChange = false;
			try {
				classComboBox.SelectedItem = matchInside ?? nearestMatch;
				// the SelectedItem setter will update the list of member items
			} finally {
				jumpOnSelectionChange = true;
			}
			matchInside = null;
			foreach (EntityItem item in memberItems) {
				if (item.IsInSamePart) {
					if (IsInside(item.Entity.ExtentSpan, location.Line, location.Column)) {
						matchInside = item;
					}
				}
			}
			jumpOnSelectionChange = false;
			try {
				membersComboBox.SelectedItem = matchInside;
			} finally {
				jumpOnSelectionChange = true;
			}
		}

		bool jumpOnSelectionChange = true;

		void classComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			// The selected class was changed.
			// Update the list of member items to be the list of members of the current class.
			EntityItem item = classComboBox.SelectedItem as EntityItem;
			memberItems = new List<EntityItem>();
			if (item != null) {
				foreach (var member in item.Entity.Children) {
					memberItems.Add(new EntityItem(member));
				}
				memberItems.Sort();
				if (jumpOnSelectionChange) {
					SD.AnalyticsMonitor.TrackFeature(GetType(), "JumpToClass");
					JumpTo(item);
				}
			}
			membersComboBox.ItemsSource = memberItems;
		}

		void membersComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			EntityItem item = membersComboBox.SelectedItem as EntityItem;
			if (item != null && jumpOnSelectionChange) {
				SD.AnalyticsMonitor.TrackFeature(GetType(), "JumpToMember");
				JumpTo(item);
			}
		}

		void JumpTo(EntityItem item)
		{
			var span = item.Entity.Span;
			Action<int, int> jumpAction = this.JumpAction;
			if (item.IsInSamePart && jumpAction != null) {
				jumpAction(span.Start.Line, span.Start.Column);
			} else {
				jumpAction?.Invoke(span.Start.Line, span.Start.Column);
			}
		}

		/// <summary>
		/// Action used for jumping to a position inside the current file.
		/// </summary>
		public Action<int, int> JumpAction { get; set; }
	}
}
