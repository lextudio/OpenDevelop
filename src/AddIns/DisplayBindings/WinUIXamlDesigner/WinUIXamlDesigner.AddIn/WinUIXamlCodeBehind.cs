// Creates the code-behind handler method for a WinUI/Uno event binding, using Roslyn so the
// insert lands in the right partial class regardless of file formatting. The XAML event
// attribute references the handler by name (see WinUIXamlElementPropertyAdapter); this keeps
// the paired .xaml.cs compiling after a double-click or typed binding creates the reference.
//
// The file is edited through the IDE when it is already open (so an open editor's buffer and
// dirty state stay authoritative); otherwise it is written to disk directly.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace ICSharpCode.WinUIXamlDesigner
{
	static class WinUIXamlCodeBehind
	{
		static readonly XName ClassDirective = XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml");

		/// <summary>
		/// Ensures a <c>private void &lt;handlerName&gt;(object sender, &lt;args&gt; e)</c> method
		/// exists in the code-behind partial class for <paramref name="rootElement"/>'s
		/// <c>x:Class</c>. Returns the method's line when created, otherwise null (already
		/// present, no code-behind file, or the class was not found).
		/// </summary>
		public static int? CreateHandlerMethod(string xamlPath, XElement rootElement, string eventName, string handlerName)
		{
			if (string.IsNullOrEmpty(handlerName) || rootElement == null)
				return null;
			// WinUI/Uno code-behind convention is <file>.xaml.cs (append, not extension replace).
			var codePath = xamlPath + ".cs";

			var className = rootElement.Attribute(ClassDirective)?.Value
				?.Split('.').LastOrDefault();
			if (string.IsNullOrEmpty(className))
				return null;

			// No code-behind yet: create the partial class skeleton first (VS does the same
			// when a double-click introduces the first event handler).
			if (!File.Exists(codePath)) {
				if (CreateSkeleton(codePath, rootElement) == false)
					return null;
			}

			string text;
			try {
				text = File.ReadAllText(codePath);
			} catch (Exception ex) {
				LoggingService.Warn("WinUI designer: could not read code-behind " + codePath + ": " + ex.Message);
				return null;
			}

			var tree = CSharpSyntaxTree.ParseText(text);
			var root = tree.GetCompilationUnitRoot();
			var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
				.FirstOrDefault(c => c.Identifier.Text == className)
				?? root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
			if (classDecl == null)
				return null;
			if (classDecl.Members.OfType<MethodDeclarationSyntax>()
				.Any(m => m.Identifier.Text == handlerName))
				return null;

			var method = CreateMethodDeclaration(handlerName, eventName);
			var lastMember = classDecl.Members.LastOrDefault();
			SyntaxNode newRoot = lastMember != null
				? root.InsertNodesAfter(lastMember, new[] { method })
				: root.AddMembers(method);

			// Format so the inserted method matches the file's brace/indent conventions.
			var formatted = Formatter.Format(newRoot, new AdhocWorkspace());
			// The line comes from the formatted tree (the node's own SpanStart is 0 for a
			// detached node), so the caller can jump to the created method precisely.
			var insertedMethod = formatted.DescendantNodes().OfType<MethodDeclarationSyntax>()
				.FirstOrDefault(m => m.Identifier.Text == handlerName);
			int? line = insertedMethod != null
				? insertedMethod.GetLocation().GetLineSpan().StartLinePosition.Line + 1
				: null;

			try {
				ApplyText(codePath, formatted.ToFullString());
			} catch (Exception ex) {
				LoggingService.Warn("WinUI designer: could not write code-behind " + codePath + ": " + ex.Message);
				return null;
			}
			return line;
		}

		/// <summary>
		/// Creates a minimal partial-class code-behind for a XAML file that has none yet,
		/// matching the <c>x:Class</c> namespace/name and root element type. Returns null when
		/// the XAML has no usable class directive.</summary>
		static bool? CreateSkeleton(string codePath, XElement rootElement)
		{
			var fullName = rootElement.Attribute(ClassDirective)?.Value;
			if (string.IsNullOrEmpty(fullName))
				return null;
			var dot = fullName.LastIndexOf('.');
			var namespaceName = dot > 0 ? fullName.Substring(0, dot) : "";
			var className = dot > 0 ? fullName.Substring(dot + 1) : fullName;
			var rootType = rootElement.Name.LocalName;

			var text = "using Microsoft.UI.Xaml;\n" +
				"using Microsoft.UI.Xaml.Controls;\n" +
				"using Microsoft.UI.Xaml.Markup;\n" +
				"\n" +
				"namespace " + namespaceName + "\n" +
				"{\n" +
				"    public sealed partial class " + className + " : " + rootType + "\n" +
				"    {\n" +
				"        public " + className + "()\n" +
				"        {\n" +
				"            this.InitializeComponent();\n" +
				"        }\n" +
				"    }\n" +
				"}\n";
			try {
				ApplyText(codePath, text);
				return true;
			} catch (Exception ex) {
				LoggingService.Warn("WinUI designer: could not create code-behind " + codePath + ": " + ex.Message);
				return false;
			}
		}

		static MethodDeclarationSyntax CreateMethodDeclaration(string handlerName, string eventName)
		{
			var argsType = EventArgsType(eventName);
			return SyntaxFactory.MethodDeclaration(
					SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
					SyntaxFactory.Identifier(handlerName))
				.AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword))
				.AddParameterListParameters(
					SyntaxFactory.Parameter(SyntaxFactory.Identifier("sender"))
						.WithType(SyntaxFactory.ParseTypeName("object")),
					SyntaxFactory.Parameter(SyntaxFactory.Identifier("e"))
						.WithType(SyntaxFactory.ParseTypeName(argsType)))
				.WithBody(SyntaxFactory.Block())
				.NormalizeWhitespace();
		}

		/// <summary>The event's delegate argument type, fully qualified so the file needs no
		/// extra using. Falls back to RoutedEventArgs for unknown delegates.</summary>
		static string EventArgsType(string eventName)
		{
			if (ParameterTypes.TryGetValue(eventName, out var type))
				return "global::Microsoft.UI.Xaml." + type;
			return "global::Microsoft.UI.Xaml.RoutedEventArgs";
		}

		static readonly System.Collections.Generic.Dictionary<string, string> ParameterTypes =
			new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal) {
				["Click"] = "RoutedEventArgs",
				["Loaded"] = "RoutedEventArgs",
				["GotFocus"] = "RoutedEventArgs",
				["LostFocus"] = "RoutedEventArgs",
				["Tapped"] = "Input.TappedRoutedEventArgs",
				["DoubleTapped"] = "Input.DoubleTappedRoutedEventArgs",
				["PointerPressed"] = "Input.PointerRoutedEventArgs",
				["PointerReleased"] = "Input.PointerRoutedEventArgs",
				["PointerEntered"] = "Input.PointerRoutedEventArgs",
				["PointerExited"] = "Input.PointerRoutedEventArgs",
				["KeyDown"] = "Input.KeyRoutedEventArgs",
				["KeyUp"] = "Input.KeyRoutedEventArgs",
				["TextChanged"] = "Controls.TextChangedEventArgs",
				["SelectionChanged"] = "Controls.SelectionChangedEventArgs",
				["DropDownOpened"] = "Controls.DropDownOpenedEventArgs",
				["DropDownClosed"] = "Controls.DropDownClosedEventArgs",
				["ValueChanged"] = "Controls.Primitives.RangeBaseValueChangedEventArgs",
				["SizeChanged"] = "SizeChangedEventArgs",
				["ImageOpened"] = "RoutedEventArgs",
				["ImageFailed"] = "Media.ExceptionRoutedEventArgs",
				["Toggled"] = "RoutedEventArgs",
				["Checked"] = "RoutedEventArgs",
				["Unchecked"] = "RoutedEventArgs",
				["ItemClick"] = "Controls.ItemClickEventArgs",
				["Paste"] = "Controls.TextControlPasteEventArgs",
			};

		static void ApplyText(string path, string newText)
		{
			var fileName = FileName.Create(path);
			var service = SD.FileService;
			if (service.IsOpen(fileName)) {
				var view = service.GetOpenFile(fileName);
				var editor = view?.GetService(typeof(ITextEditor)) as ITextEditor;
				if (editor != null) {
					// Replacing the editor document marks the opened file dirty through the
					// editor's own TextChanged handling, keeping the open buffer authoritative.
					editor.Document.Text = newText;
					return;
				}
			}
			File.WriteAllText(path, newText);
		}
	}
}
