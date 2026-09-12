using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// Corrections applied to every document just before <c>XamlReader.Load</c> sees it.
	///
	/// All three are markup-level and runtime-agnostic - they fix what the XAML rules say should
	/// happen but a runtime parser does not do - so both WinUI children share them rather than only
	/// the Microsoft one, where they were first written. Each has its own file with the full story;
	/// in short:
	///
	///  * <see cref="XamlPrefixNormalizer"/> - one xmlns prefix bound to two different namespaces in
	///    a combined document, which the parser resolves through the wrong one.
	///  * <see cref="AttachedPropertyQualifier"/> - an unprefixed attached property belongs to the
	///    default xmlns, but the parser qualifies it with whatever `using:` prefix is in scope.
	///  * <see cref="CompiledXamlControlSubstituter"/> - the app's own XAML-declared controls cannot
	///    be constructed by a runtime parser (their markup uses compile-time x:Bind), so they are
	///    stood in for by a panel that keeps their children.
	///
	/// Runs on the FINAL text, after any host-specific transform has merged or injected into it:
	/// the first two problems only exist once documents are combined, because that is when one
	/// document's prefixes come to govern another's markup.
	/// </summary>
	static class XamlDocumentRepair
	{
		/// <summary>Returns <paramref name="xaml"/> repaired, or unchanged when nothing applied.
		/// Never throws: a document this cannot parse is handed on so the real
		/// <c>XamlReader.Load</c> reports the syntax error rather than this masking it.</summary>
		public static string Apply(string xaml)
		{
			if (string.IsNullOrEmpty(xaml))
			{
				return xaml;
			}
			// Diagnostic only: skips every repair below so a raw XamlReader.Load sees exactly what
			// the app author wrote, no substitution/stripping/qualification at all. Used to measure
			// how much of a real app's XAML is renderable by this design host's own metadata and
			// resource support alone, with none of the markup-level workarounds this file exists for.
			if (Environment.GetEnvironmentVariable("OD_DESIGNHOST_NO_REPAIR") == "1")
			{
				return Dump(xaml);
			}
			// Must run on the raw text, before parsing - see ConditionalXmlnsStripper's remarks on
			// why editing the parsed DOM's namespace declarations afterward would not work.
			xaml = ConditionalXmlnsStripper.Strip(xaml, out var conditions);
			XElement root;
			try
			{
				root = XDocument.Parse(xaml).Root!;
			}
			catch (Exception)
			{
				return xaml;
			}
			var renamed = XamlPrefixNormalizer.Normalize(root);
			var qualified = AttachedPropertyQualifier.Qualify(root);
			var unbound = CompileTimeBindingStripper.Strip(root);
			var events = EventHandlerAttributeStripper.Strip(root);
			var substituted = CompiledXamlControlSubstituter.Substitute(root);
			if (renamed > 0 || qualified > 0 || unbound > 0 || events > 0 || conditions > 0 || substituted.Count > 0)
			{
				Report(renamed, qualified, unbound, events, conditions, substituted);
				xaml = root.ToString(SaveOptions.DisableFormatting);
			}
			return Dump(xaml);
		}

		/// <summary>
		/// Opt-in dump of the exact text handed to XamlReader (<c>OD_DESIGNHOST_XAML_DUMP=&lt;dir&gt;</c>).
		///
		/// A parse error's position indexes into THIS text and into nothing on disk - the document
		/// can be a page plus megabytes of injected theme resources, then rewritten by the repairs
		/// above. Reading a reported position without it is guesswork, and guessing at one sent this
		/// investigation down two wrong paths. Deliberately the last step, so what lands on disk is
		/// byte-for-byte what the parser sees.
		/// </summary>
		static string Dump(string xaml)
		{
			var directory = Environment.GetEnvironmentVariable("OD_DESIGNHOST_XAML_DUMP");
			if (string.IsNullOrEmpty(directory))
			{
				return xaml;
			}
			try
			{
				System.IO.Directory.CreateDirectory(directory);
				var path = System.IO.Path.Combine(directory,
					$"xamlreader-input-{DateTime.Now:HHmmss-fff}.xaml");
				System.IO.File.WriteAllText(path, xaml);
				Console.Error.WriteLine($"design-host: wrote XamlReader input ({xaml.Length} chars) to {path}");
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("design-host: XAML dump failed: " + e.Message);
			}
			return xaml;
		}

		static void Report(int renamed, int qualified, int unbound, int events, int conditions, List<string> substituted)
		{
			Console.Error.WriteLine($"design-host: document repaired: {qualified} unprefixed attached"
				+ $" propert{(qualified == 1 ? "y" : "ies")} qualified,"
				+ $" {renamed} ambiguous xmlns prefix{(renamed == 1 ? "" : "es")} renamed,"
				+ $" {unbound} compiled binding{(unbound == 1 ? "" : "s")} dropped,"
				+ $" {events} event handler attribute{(events == 1 ? "" : "s")} dropped,"
				+ $" {conditions} conditional xmlns declaration{(conditions == 1 ? "" : "s")} stripped.");
			if (substituted.Count > 0)
			{
				// Named rather than counted: this one changes what the surface SHOWS, so the reader
				// has to be able to tell which control was stood in for.
				Console.Error.WriteLine("design-host: substituted a panel for app control(s) whose"
					+ " compiled XAML cannot be loaded by a runtime parser: "
					+ string.Join(", ", substituted.Distinct()));
			}
		}
	}
}
