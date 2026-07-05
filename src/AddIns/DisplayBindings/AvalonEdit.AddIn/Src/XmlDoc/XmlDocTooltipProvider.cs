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

// Rewritten against Microsoft.CodeAnalysis directly (see doc/technotes/csharp-roslyn.md) - gets
// the symbol at the hover position via Roslyn's SemanticModel instead of the old
// ICSharpCode.TypeSystem.ResolveResult (which nothing produces today), and feeds its
// ISymbol.GetDocumentationCommentXml() into the existing (and more complete)
// ICSharpCode.SharpDevelop.Editor.DocumentationUIBuilder - via ICSharpCode.TypeSystem.XmlDocumentationElement,
// a small System.Xml.Linq-backed adapter with no compiler-specific dependencies of its own.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;

using ICSharpCode.AvalonEdit.AddIn.Options;
using ICSharpCode.SharpDevelop.Roslyn;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.TypeSystem;
using Microsoft.CodeAnalysis;
using ISymbol = Microsoft.CodeAnalysis.ISymbol;

namespace ICSharpCode.AvalonEdit.AddIn.XmlDoc
{
	public class XmlDocTooltipProvider : ITextAreaToolTipProvider
	{
		public void HandleToolTipRequest(ToolTipRequestEventArgs e)
		{
			if (!e.InDocument)
				return;
			ISymbol symbol = RoslynWorkspaceHelper.GetSymbolAt(e.Editor, e.LogicalPosition);
			if (symbol == null)
				return;
			e.SetToolTip(new FlowDocumentTooltip(CreateTooltip(symbol)));
		}

		static readonly SymbolDisplayFormat HeaderFormat = new SymbolDisplayFormat(
			typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
			genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
			memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType,
			parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut,
			miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

		static FlowDocument CreateTooltip(ISymbol symbol)
		{
			var builder = new DocumentationUIBuilder();
			builder.AddCodeBlock(symbol.ToDisplayString(HeaderFormat), keepLargeMargin: true);

			XElement xmlDoc = TryParseDocumentationXml(symbol);
			if (xmlDoc != null) {
				// <member name="..."><summary>...</summary>...</member> - render the children of <member>.
				foreach (var child in XmlDocumentationElement.Parse(xmlDoc)) {
					builder.AddDocumentationElement(child);
				}
			}

			return builder.CreateFlowDocument();
		}

		static XElement TryParseDocumentationXml(ISymbol symbol)
		{
			string xml = symbol.GetDocumentationCommentXml();
			if (string.IsNullOrEmpty(xml))
				return null;
			try {
				return XElement.Parse(xml);
			} catch (Exception) {
				return null;
			}
		}

		sealed class FlowDocumentTooltip : Popup, ITooltip
		{
			FlowDocumentScrollViewer viewer;

			public FlowDocumentTooltip(FlowDocument document)
			{
				TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
				viewer = new FlowDocumentScrollViewer();
				viewer.Document = document;
				Border border = new Border {
					Background = SystemColors.InfoBrush,
					BorderBrush = SystemColors.InfoTextBrush,
					BorderThickness = new Thickness(1),
					MaxHeight = 400,
					Child = viewer
				};
				this.Child = border;
				viewer.Foreground = SystemColors.InfoTextBrush;
				document.FontSize = CodeEditorOptions.Instance.FontSize;
			}

			public bool CloseWhenMouseMovesAway {
				get { return !this.IsKeyboardFocusWithin; }
			}

			protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
			{
				base.OnLostKeyboardFocus(e);
				this.IsOpen = false;
			}

			protected override void OnMouseLeave(MouseEventArgs e)
			{
				base.OnMouseLeave(e);
				// When the mouse is over the popup, it is possible for SharpDevelop to be minimized,
				// or moved into the background, and yet the popup stays open.
				// We don't have a good method here to check whether the mouse moved back into the text area
				// or somewhere else, so we'll just close the popup.
				if (CloseWhenMouseMovesAway)
					this.IsOpen = false;
			}
		}
	}
}
