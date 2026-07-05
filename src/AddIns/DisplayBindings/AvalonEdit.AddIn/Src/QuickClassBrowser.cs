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

// Rewritten against Microsoft.CodeAnalysis directly (see doc/technotes/csharp-roslyn.md, Phase 1
// "option (b)") - no longer routes through ICSharpCode.TypeSystem's IUnresolvedFile/
// IUnresolvedTypeDefinition/IUnresolvedMember two-phase model (Roslyn has no separate
// unresolved/resolved split).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;

using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Roslyn;
using Microsoft.CodeAnalysis;
using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// Panel with two combo boxes. Used to quickly navigate to entities in the current file.
	/// </summary>
	public partial class QuickClassBrowser : UserControl
	{
		static readonly SymbolDisplayFormat ClassFormat = new SymbolDisplayFormat(
			typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
			genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

		static readonly SymbolDisplayFormat MemberFormat = new SymbolDisplayFormat(
			genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
			memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
			parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName);

		/// <summary>
		/// ViewModel used for combobox items.
		/// </summary>
		class EntityItem : IComparable<EntityItem>, System.ComponentModel.INotifyPropertyChanged
		{
			readonly ISymbol entity;
			ImageSource image;
			string text;

			public ISymbol Entity {
				get { return entity; }
			}

			public EntityItem(INamedTypeSymbol typeDef)
			{
				this.IsInSamePart = true;
				this.entity = typeDef;
				this.text = typeDef.ToDisplayString(ClassFormat);
				this.image = RoslynSymbolIcons.GetImage(typeDef);
			}

			public EntityItem(ISymbol member)
			{
				this.IsInSamePart = true;
				this.entity = member;
				this.text = member.ToDisplayString(MemberFormat);
				this.image = RoslynSymbolIcons.GetImage(member);
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
				int r = this.Entity.Kind.CompareTo(other.Entity.Kind);
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
			runUpdateWhenDropDownClosed = true;
			runUpdateWhenDropDownClosedFile = fileName;
			if (!IsDropDownOpen)
				ComboBox_DropDownClosed(null, null);
		}

		// The lists of items currently visible in the combo boxes.
		// These should never be null.
		List<EntityItem> classItems = new List<EntityItem>();
		List<EntityItem> memberItems = new List<EntityItem>();

		void DoUpdate(FileName fileName)
		{
			classItems = new List<EntityItem>();
			if (fileName != null) {
				var document = RoslynWorkspaceHelper.FindDocument(fileName);
				if (document != null) {
					var model = document.GetSemanticModelAsync().Result;
					var root = model.SyntaxTree.GetRoot();
					var topLevelTypes = root.DescendantNodes()
						.Select(n => model.GetDeclaredSymbol(n) as INamedTypeSymbol)
						.Where(s => s != null && s.ContainingType == null)
						.Distinct(SymbolEqualityComparer.Default)
						.Cast<INamedTypeSymbol>();
					AddClasses(topLevelTypes);
				}
			}
			classItems.Sort();
			classComboBox.ItemsSource = classItems;
		}

		bool IsDropDownOpen {
			get { return classComboBox.IsDropDownOpen || membersComboBox.IsDropDownOpen; }
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

		void AddClasses(IEnumerable<INamedTypeSymbol> classes)
		{
			foreach (var c in classes) {
				if (c.IsImplicitlyDeclared)
					continue;
				classItems.Add(new EntityItem(c));
				AddClasses(c.GetTypeMembers());
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

		static bool IsInside(Location location, int line, int column)
		{
			var span = location.GetLineSpan();
			return IsInside(span, line, column);
		}

		static bool IsInside(FileLinePositionSpan span, int line, int column)
		{
			int beginLine = span.StartLinePosition.Line + 1, beginColumn = span.StartLinePosition.Character + 1;
			int endLine = span.EndLinePosition.Line + 1, endColumn = span.EndLinePosition.Character + 1;
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
					var loc = item.Entity.Locations.FirstOrDefault(l => l.IsInSource);
					if (loc == null) continue;
					var span = loc.GetLineSpan();
					int beginLine = span.StartLinePosition.Line + 1, endLine = span.EndLinePosition.Line + 1;
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
					var loc = item.Entity.Locations.FirstOrDefault(l => l.IsInSource);
					if (loc != null && IsInside(loc, location.Line, location.Column)) {
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
			INamedTypeSymbol selectedClass = item != null ? item.Entity as INamedTypeSymbol : null;
			memberItems = new List<EntityItem>();
			if (selectedClass != null) {
				var selectedClassLocation = selectedClass.Locations.FirstOrDefault(l => l.IsInSource);
				foreach (var member in selectedClass.GetMembers()) {
					if (member.IsImplicitlyDeclared)
						continue;
					if (!(member is IMethodSymbol || member is IFieldSymbol || member is IPropertySymbol || member is IEventSymbol))
						continue;
					var memberLocation = member.Locations.FirstOrDefault(l => l.IsInSource);
					bool isInSamePart = memberLocation != null && selectedClassLocation != null
						&& string.Equals(memberLocation.SourceTree.FilePath, selectedClassLocation.SourceTree.FilePath, StringComparison.OrdinalIgnoreCase);
					memberItems.Add(new EntityItem(member) { IsInSamePart = isInSamePart });
				}
				memberItems.Sort();
				if (jumpOnSelectionChange) {
					SD.AnalyticsMonitor.TrackFeature(GetType(), "JumpToClass");
					JumpTo(item, selectedClassLocation);
				}
			}
			membersComboBox.ItemsSource = memberItems;
		}

		void membersComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			EntityItem item = membersComboBox.SelectedItem as EntityItem;
			if (item != null && jumpOnSelectionChange) {
				SD.AnalyticsMonitor.TrackFeature(GetType(), "JumpToMember");
				JumpTo(item, item.Entity.Locations.FirstOrDefault(l => l.IsInSource));
			}
		}

		void JumpTo(EntityItem item, Location location)
		{
			if (location == null)
				return;
			var span = location.GetLineSpan();
			Action<int, int> jumpAction = this.JumpAction;
			if (item.IsInSamePart && jumpAction != null) {
				jumpAction(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
			} else {
				FileService.JumpToFilePosition(FileName.Create(span.Path), span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
			}
		}

		/// <summary>
		/// Action used for jumping to a position inside the current file.
		/// </summary>
		public Action<int, int> JumpAction { get; set; }
	}
}
