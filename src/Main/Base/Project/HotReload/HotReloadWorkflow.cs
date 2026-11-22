using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace ICSharpCode.SharpDevelop.Project.HotReload
{
	/// <summary>What the Apply gesture actually has to do for the current adapter.</summary>
	public enum HotReloadApplyAction
	{
		/// <summary>No live session, or the document cannot be applied - the command is disabled.</summary>
		None,

		/// <summary>Send the document to the framework and report what it answered.</summary>
		Apply,

		/// <summary>
		/// Save the document and let the framework notice. The only honest action for an adapter
		/// whose framework watches the filesystem: the IDE has nothing to transmit.
		/// </summary>
		Save,
	}

	/// <summary>
	/// The capability-driven decisions behind the Hot Reload commands, kept free of workbench
	/// statics so they can be tested directly. The UI must never offer an action the resolved
	/// adapter cannot perform, which is the whole reason this is a decision and not a fixed command.
	/// </summary>
	public static class HotReloadWorkflow
	{
		public static HotReloadApplyAction DecideApplyAction(IHotReloadSession session)
		{
			if (session == null)
				return HotReloadApplyAction.None;

			switch (session.State) {
				case HotReloadSessionState.Disconnected:
				case HotReloadSessionState.Stopped:
				case HotReloadSessionState.Unsupported:
				case HotReloadSessionState.NotStarted:
					return HotReloadApplyAction.None;
			}

			return session.Capabilities.CanApplyFromIde ? HotReloadApplyAction.Apply : HotReloadApplyAction.Save;
		}

		/// <summary>
		/// The label the command shows. "Apply" would be a lie for a framework-driven adapter, and
		/// a user who clicked it and saw nothing happen would have no way to know why.
		/// </summary>
		public static string DescribeApplyAction(HotReloadApplyAction action)
		{
			switch (action) {
				case HotReloadApplyAction.Apply:
					return "${res:XML.MainMenu.DebugMenu.ApplyHotReload}";
				case HotReloadApplyAction.Save:
					return "${res:XML.MainMenu.DebugMenu.SaveForHotReload}";
				default:
					return "${res:XML.MainMenu.DebugMenu.ApplyHotReload}";
			}
		}

		/// <summary>
		/// Whether an unsaved editor buffer may be sent as-is. When the framework reads the file
		/// from disk, applying buffer text would report success for something the framework never
		/// saw, so the document has to be saved first.
		/// </summary>
		public static bool MustSaveBeforeApply(IHotReloadSession session)
			=> session != null && session.Capabilities.RequiresSavedFile;

		/// <summary>
		/// A conservative classification. The adapters treat this as a hint about the smallest safe
		/// update and are free to ignore it, so guessing narrowly is a correctness risk while
		/// guessing broadly only costs efficiency - hence FullDocument whenever anything structural
		/// might have changed.
		/// </summary>
		public static HotReloadChangeKind Classify(string previousText, string currentText)
			=> Classify(previousText, currentText, generatesXamlCode: false);

		/// <param name="generatesXamlCode">
		/// True when the project compiles its XAML to C# (the LibreWPF XAML source generator): only then
		/// does an x:Name own a generated field and a {cs:} attribute own generated code, so only then is
		/// removing a name a restart and changing an expression a rebuild. For BAML and WinUI a re-parse
		/// carries those edits, and reporting them as structural would block a reload that works.
		/// </param>
		/// <summary>
		/// <see cref="Classify"/> for an Apply whose "previous" text may be unknown: nothing was accepted in this
		/// session and the file was written after the launch, so neither the editor nor the disk holds what the
		/// application was compiled from. With the XAML source generator the code-delta path still knows (its
		/// baseline is the launch compilation), so it takes the edit; a text push has nothing to diff against.
		/// </summary>
		public static HotReloadChangeKind ClassifyApply(string previousText, string currentText, bool generatesXamlCode)
			=> previousText == null && generatesXamlCode
				? HotReloadChangeKind.CodeDelta
				: Classify(previousText ?? currentText, currentText, generatesXamlCode);

		public static HotReloadChangeKind Classify(string previousText, string currentText, bool generatesXamlCode)
		{
			if (previousText == null || currentText == null)
				return HotReloadChangeKind.FullDocument;
			if (string.Equals(previousText, currentText, StringComparison.Ordinal))
				return HotReloadChangeKind.FullDocument;

			// Edits a text push cannot express. Needs both sides to parse: text typed half-way is
			// invalid XML all the time, and that must not be mistaken for a structural change.
			// Names are collected from the whole file because the generator emits one field per
			// x:Name anywhere in it, template content included. {cs:} values compare in order: moving
			// one changes the generated code, and a false CodeDelta only costs a rebuild whereas a
			// missed one loses the edit.
			var previousFacts = generatesXamlCode ? XamlFacts.TryRead(previousText) : null;
			var currentFacts = generatesXamlCode ? XamlFacts.TryRead(currentText) : null;
			if (previousFacts != null && currentFacts != null) {
				if (previousFacts.XClass != currentFacts.XClass || previousFacts.Names.Except(currentFacts.Names).Any())
					return HotReloadChangeKind.RestartRequired;
				if (!previousFacts.CsExpressions.SequenceEqual(currentFacts.CsExpressions)
					|| currentFacts.Names.Except(previousFacts.Names).Any())
					return HotReloadChangeKind.CodeDelta;
				return previousFacts.ElementCount == currentFacts.ElementCount
					? HotReloadChangeKind.Property
					: HotReloadChangeKind.Subtree;
			}

			// Same element structure, different attribute text: treat as a property edit. Counting
			// tags is deliberately crude - it only has to be right about "nothing was added or
			// removed", because everything else falls back to the full document.
			return CountElements(previousText) == CountElements(currentText)
				? HotReloadChangeKind.Property
				: HotReloadChangeKind.Subtree;
		}

		/// <summary>
		/// What a text push cannot carry, read with namespace processing off so Simpler XAML
		/// (no xmlns declarations) still reads. Null when the text is not well-formed.
		/// </summary>
		sealed class XamlFacts
		{
			const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

			public string XClass;
			public readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal);
			public readonly List<string> CsExpressions = new List<string>();
			public int ElementCount;

			public static XamlFacts TryRead(string xaml)
			{
				try {
					var facts = new XamlFacts();
					using (var text = new StringReader(xaml))
					using (var reader = new XmlTextReader(text) { Namespaces = false, DtdProcessing = DtdProcessing.Prohibit }) {
						var xamlPrefixes = new HashSet<string>(StringComparer.Ordinal) { "x" };
						while (reader.Read()) {
							if (reader.NodeType != XmlNodeType.Element)
								continue;
							var isRoot = facts.ElementCount == 0;
							facts.ElementCount++;
							if (!reader.HasAttributes)
								continue;
							// Declarations first: a prefix may be declared after the attribute that uses it.
							// The set is file-wide rather than scoped, which can only over-recognise.
							while (reader.MoveToNextAttribute()) {
								if (reader.Name.StartsWith("xmlns:", StringComparison.Ordinal) && reader.Value == XamlNamespace)
									xamlPrefixes.Add(reader.Name.Substring("xmlns:".Length));
							}
							reader.MoveToElement();
							while (reader.MoveToNextAttribute()) {
								var name = reader.Name;
								var value = reader.Value;
								var colon = name.IndexOf(':');
								var local = colon < 0 ? name : name.Substring(colon + 1);
								var inXamlNamespace = colon >= 0 && xamlPrefixes.Contains(name.Substring(0, colon));
								if (inXamlNamespace && local == "Class" && isRoot)
									facts.XClass = value;
								else if ((inXamlNamespace && local == "Name") || name == "Name")
									facts.Names.Add(value);
								if (value.StartsWith("{cs:", StringComparison.Ordinal))
									facts.CsExpressions.Add(value);
							}
							reader.MoveToElement();
						}
					}
					return facts;
				} catch (XmlException) {
					return null;
				}
			}
		}

		/// <summary>
		/// Whether the project opted in to the XAML source generator, through the property in its own
		/// project file or the environment switch the targets also honour. A value set elsewhere
		/// (Directory.Build.props) is not seen and reads as false, which keeps the text path.
		/// </summary>
		public static bool UsesGeneratedXaml(string projectFileName, Func<string, string> getEnvironmentVariable = null)
		{
			getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
			if (IsSwitchOn(getEnvironmentVariable("LIBREWPF_USE_XSG"))
				|| IsSwitchOn(getEnvironmentVariable("LIBREWPF_USE_XAML_SOURCE_GENERATOR")))
				return true;
			if (string.IsNullOrEmpty(projectFileName) || !File.Exists(projectFileName))
				return false;
			try {
				return System.Xml.Linq.XDocument.Load(projectFileName)
					.Descendants("UseLibreWpfXamlSourceGenerator")
					.Any(e => IsSwitchOn(e.Value));
			} catch (Exception ex) when (ex is XmlException || ex is IOException || ex is UnauthorizedAccessException) {
				return false;
			}
		}

		// The targets treat any non-empty environment value as on, including "0"; this is stricter on
		// purpose. Reading a project as not generated only keeps the text path, whereas the opposite
		// mistake blocks reloads that work.
		static bool IsSwitchOn(string value)
		{
			value = value?.Trim();
			return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
		}

		static int CountElements(string xaml)
		{
			var count = 0;
			for (var i = 0; i < xaml.Length; i++) {
				if (xaml[i] != '<')
					continue;
				if (i + 1 < xaml.Length && (xaml[i + 1] == '/' || xaml[i + 1] == '!' || xaml[i + 1] == '?'))
					continue;
				count++;
			}
			return count;
		}
	}
}
