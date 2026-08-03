// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// Registers the syntax highlighting definitions the hosted ILSpy needs but that AvalonEdit does not
// ship - MSIL above all.
//
// Why this is needed at all: a Language's highlighting is resolved by extension
// (Language.SyntaxHighlighting => HighlightingManager.Instance.GetDefinitionByExtension(FileExtension))
// and applied by DecompilerTextView (textEditor.SyntaxHighlighting = context.Language.SyntaxHighlighting).
// That pipeline is fine; what was missing was the registration, so IL output rendered as plain,
// unhighlighted text.
//
// ILSpy's own DecompilerTextView.RegisterHighlighting() cannot supply it here. It calls
// HighlightingManager.Instance.RegisterHighlighting("ILAsm", [".il"], "ILAsm-Mode") - an overload
// that resolves the resource name against *AvalonEdit's own* embedded resources
// (DefaultHighlightingManager.RegisterHighlighting -> Resources.OpenStream). AvalonEdit ships no
// ILAsm definition at all, and its built-ins are named with the extension included
// ("CSharp-Mode.xshd"), so "ILAsm-Mode" was never going to resolve. C# keeps working only because
// AvalonEdit does ship a C# definition for ".cs".
//
// So the .xshd is linked from the ILSpy submodule (see ILSpyAddIn.csproj - the file is ILSpy's own
// language definition, so linking it is the same "link, don't fork" policy the rest of this addin
// follows) and registered here through AvalonEdit's *public* lazy overload against this assembly's
// resources.
using System;
using System.Reflection;
using System.Xml;

using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.Core;

namespace ICSharpCode.ILSpyAddIn
{
	static class IlSpyHighlighting
	{
		static bool registered;

		/// <summary>
		/// Registers the ILSpy language highlightings that AvalonEdit lacks. Idempotent, and a
		/// failure is logged rather than thrown - missing highlighting must not stop the addin from
		/// loading, it just means plain text.
		/// </summary>
		public static void Register()
		{
			if (registered)
				return;
			registered = true;

			// name, extensions, embedded resource - mirroring the names
			// DecompilerTextView.RegisterHighlighting() uses, so any linked ILSpy code that looks a
			// definition up by name or extension finds the same thing it expects.
			TryRegister("ILAsm", new[] { ".il" }, "ILAsm-Mode.xshd");
			TryRegister("Asm", new[] { ".s", ".asm" }, "Asm-Mode.xshd");
		}

		static void TryRegister(string name, string[] extensions, string resourceName)
		{
			try {
				// Don't fight an existing registration (AvalonEdit's own, or a second call).
				if (HighlightingManager.Instance.GetDefinition(name) != null)
					return;

				HighlightingManager.Instance.RegisterHighlighting(name, extensions,
					() => LoadDefinition(resourceName));
			} catch (Exception ex) {
				LoggingService.Warn("Could not register the '" + name + "' syntax highlighting for the hosted ILSpy.", ex);
			}
		}

		static IHighlightingDefinition LoadDefinition(string resourceName)
		{
			using var stream = Assembly.GetExecutingAssembly()
				.GetManifestResourceStream("Highlighting." + resourceName);
			if (stream == null)
				throw new InvalidOperationException("Embedded highlighting resource 'Highlighting." + resourceName + "' not found.");
			using var reader = XmlReader.Create(stream);
			return HighlightingLoader.Load(reader, HighlightingManager.Instance);
		}
	}
}
