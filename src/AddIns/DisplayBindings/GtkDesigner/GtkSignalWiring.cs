using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace ICSharpCode.GtkDesigner;

/// <summary>A &lt;signal&gt; in a .ui document, resolved to the C# handler Gir.Core can call.
/// When <see cref="Supported"/> is false, <see cref="Reason"/> says why it is left to user code.</summary>
public sealed record GtkSignalHandler(
	string ObjectId, string ObjectType, string Signal, string Handler,
	string EventName, string ReturnType, string ArgsType, bool Supported, string Reason,
	string Target = "", string State = "")
{
	/// <summary>The method a behavior class needs: <c>void runButton_clicked(Gtk.Button sender, System.EventArgs args)</c>.</summary>
	public string Signature => $"{ReturnType} {Handler}({ObjectType} sender, {ArgsType} args)";
	/// <summary>An empty body that compiles: returning signals return false/0 (GTK's "not handled").</summary>
	public string DefaultBody => ReturnType switch { "bool" => "return false;", "void" => "", _ => "return default;" };
}

/// <summary>The wire form of the wiring for one .ui document (design host -> IDE).</summary>
public sealed class GtkSignalWiringResult
{
	public string Source { get; set; } = "";
	public List<GtkSignalHandlerInfo> Handlers { get; set; } = new();
}

public sealed class GtkSignalHandlerInfo
{
	public string ObjectId { get; set; } = "";
	public string Signal { get; set; } = "";
	public string Handler { get; set; } = "";
	public string EventName { get; set; } = "";
	public string Signature { get; set; } = "";
	public string DefaultBody { get; set; } = "";
	public bool Supported { get; set; }
	public string Reason { get; set; } = "";

	public static GtkSignalHandlerInfo From(GtkSignalHandler h) => new() {
		ObjectId = h.ObjectId, Signal = h.Signal, Handler = h.Handler, EventName = h.EventName, Signature = h.Signature,
		DefaultBody = h.DefaultBody, Supported = h.Supported, Reason = h.Reason
	};
}

/// <summary>
/// GtkBuilder cannot connect a &lt;signal handler="..."&gt; to a C# method: with the default
/// GtkBuilderCScope it looks the name up as a native symbol and ABORTS the process ("failed to
/// add UI: No function named ..."), and Gir.Core 0.8 offers no managed BuilderScope. So the .ui
/// keeps standard &lt;signal&gt; entries (Glade/Cambalache-compatible), and the designer generates a
/// companion partial class, <c>X.ui.cs</c>, whose <c>BuildUi</c> loads the .ui without them and
/// connects each one through the object's Gir.Core event - typed from GIR, no reflection, no
/// guessing of delegate shapes (an expression lambda adapts to SignalHandler and
/// ReturningSignalHandler alike). The handler methods live in the user-owned <c>X.cs</c>.
/// </summary>
public static class GtkSignalWiring
{
	/// <summary>Every &lt;signal&gt; of every object with an id, resolved against the catalogue.</summary>
	public static IReadOnlyList<GtkSignalHandler> Handlers(string uiText, GirCatalog catalog)
	{
		var result = new List<GtkSignalHandler>();
		var document = XDocument.Parse(uiText);
		foreach (var obj in document.Descendants().Where(e => e.Name.LocalName == "object")) {
			var id = (string?)obj.Attribute("id");
			var builderClass = (string?)obj.Attribute("class") ?? "";
			foreach (var signal in obj.Elements().Where(e => e.Name.LocalName == "signal")) {
				var name = (string?)signal.Attribute("name") ?? "";
				var handler = (string?)signal.Attribute("handler") ?? "";
				result.Add(Resolve(id, builderClass, name, handler, signal, catalog));
			}
		}
		return result;
	}

	/// <summary>The marker an action handler carries in <see cref="GtkSignalHandler.ObjectId"/>:
	/// <c>action:save</c> is the window action <c>win.save</c>, which has no object in the .ui.</summary>
	public const string ActionPrefix = "action:";

	/// <summary>The window actions (<c>win.name</c>) the document's menus refer to, as handlers of a
	/// <c>Gio.SimpleAction</c>: <c>void name_action(Gio.SimpleAction sender, Gio.SimpleAction.ActivateSignalArgs args)</c>.
	/// Window and application actions. Menu <c>target</c> gives a parameter type; the designer's
	/// <c>state</c> metadata gives the initial variant for a stateful action.</summary>
	public static IReadOnlyList<GtkSignalHandler> MenuActions(string uiText)
	{
		var actions = new List<(string Scope, string Name, string Target, string State)>();
		try {
			foreach (var attribute in XDocument.Parse(uiText).Descendants().Where(e => e.Name.LocalName == "attribute" && (string?)e.Attribute("name") == "action")) {
				var value = attribute.Value.Trim();
				var scope = value.StartsWith("win.", StringComparison.Ordinal) ? "win" : value.StartsWith("app.", StringComparison.Ordinal) ? "app" : "";
				if (scope.Length == 0) continue;
				var name = value.Substring(scope.Length + 1);
				// GAction names are alphanumerics, '-' and '.': g_simple_action_new rejects anything else (an
				// underscore included) and returns NULL, so such a name is not registered, only left to user code.
				if (name.Length == 0 || !name.All(c => (c < 128 && char.IsLetterOrDigit(c)) || c == '-' || c == '.')) continue;
				var item = attribute.Parent;
				var target = item?.Elements().FirstOrDefault(e => e.Name.LocalName == "attribute" && (string?)e.Attribute("name") == "target")?.Value.Trim() ?? "";
				var state = item?.Elements().FirstOrDefault(e => e.Name.LocalName == "attribute" && (string?)e.Attribute("name") == "state")?.Value.Trim() ?? "";
				if (!actions.Any(a => a.Scope == scope && a.Name == name)) actions.Add((scope, name, target, state));
			}
		} catch (System.Xml.XmlException) { return Array.Empty<GtkSignalHandler>(); }
		// The name itself decides the method (ActionMethod), which keeps an action bound to its user-written
		// stub whatever else is in the menu. Only names that still collide ("a--b" and "a.b" are both a__b)
		// are disambiguated, by ordinal name order.
		var methods = new Dictionary<string, string>(StringComparer.Ordinal);
		var taken = new HashSet<string>(StringComparer.Ordinal);
		foreach (var action in actions.OrderBy(a => a.Scope, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal)) {
			var method = ActionMethod((action.Scope == "app" ? "app_" : "") + action.Name) + (action.State.Length > 0 ? "_state" : "");
			for (var n = 2; !taken.Add(method); n++) method = ActionMethod((action.Scope == "app" ? "app_" : "") + action.Name) + "_" + n;
			methods[action.Scope + ":" + action.Name] = method;
		}
		return actions.Select(action => new GtkSignalHandler(action.Scope == "win" ? ActionPrefix + action.Name : "application-action:" + action.Name,
			"Gio.SimpleAction", action.State.Length > 0 ? "change-state" : "activate", methods[action.Scope + ":" + action.Name], action.State.Length > 0 ? "OnChangeState" : "OnActivate",
			"void", action.State.Length > 0 ? "Gio.SimpleAction.ChangeStateSignalArgs" : "Gio.SimpleAction.ActivateSignalArgs", true, "", action.Target, action.State)).ToList();
	}

	/// <summary>"save-as" -&gt; "save_as_action", "save.as" -&gt; "save__as_action" (a GAction name has no underscore
	/// of its own, so these never meet); "2nd" -&gt; "_2nd_action".</summary>
	public static string ActionMethod(string actionName)
	{
		var name = string.Concat(actionName.Select(c => c == '.' ? "__" : char.IsLetterOrDigit(c) ? c.ToString() : "_"));
		// "2nd" is a valid action name but not a C# identifier start.
		return (name.Length == 0 || char.IsDigit(name[0]) ? "_" : "") + name + "_action";
	}

	/// <summary>Resolves one signal for a document that cannot carry a &lt;signal&gt; entry (a
	/// composite template): same rules as <see cref="Handlers"/>, without the XML attributes.</summary>
	public static GtkSignalHandler Describe(string objectId, string builderClass, string signalName, string handler, GirCatalog catalog)
		=> Resolve(objectId, builderClass, signalName, handler, new XElement("signal"), catalog);

	static GtkSignalHandler Resolve(string? id, string builderClass, string signalName, string handler, XElement signal, GirCatalog catalog)
	{
		var girClass = catalog.FindByTypeName(builderClass);
		var objectType = girClass?.QualifiedName ?? builderClass;
		GtkSignalHandler Unsupported(string reason) => new(id ?? "", objectType, signalName, handler, "", "void", "System.EventArgs", false, reason);
		if (string.IsNullOrEmpty(id)) return Unsupported("the object has no id, so code cannot look it up");
		if (!IsIdentifier(handler)) return Unsupported($"'{handler}' is not a C# method name");
		if (girClass == null) return Unsupported($"{builderClass} is not in the GTK introspection data");
		if (signalName.Contains("::", StringComparison.Ordinal)) return Unsupported("a detailed signal; Gir.Core events have no detail, connect it in code");
		if ((string?)signal.Attribute("swapped") is "yes" or "true" or "True" or "1") return Unsupported("swapped signals have no Gir.Core equivalent");
		if ((string?)signal.Attribute("object") != null) return Unsupported("the 'object' attribute (connect to another object) is not supported; connect it in code");
		var gir = catalog.SignalsOf(builderClass).FirstOrDefault(s => s.Name == signalName.Replace('_', '-'));
		if (gir == null) return Unsupported($"{builderClass} has no signal '{signalName}'");
		var returnType = gir.ReturnType switch { "none" => "void", "gboolean" => "bool", "gint" => "int", "guint" => "uint", _ => "" };
		if (returnType.Length == 0) return Unsupported($"its return type {gir.ReturnType} is not supported; connect it in code");
		var pascal = Pascal(gir.Name);
		var argsType = gir.HasParameters ? gir.DeclaringType + "." + pascal + "SignalArgs" : "System.EventArgs";
		return new GtkSignalHandler(id, objectType, gir.Name, handler, "On" + pascal, returnType, argsType, true, "");
	}

	/// <summary>"close-request" -> "CloseRequest" (Gir.Core's event and SignalArgs naming).</summary>
	public static string Pascal(string name) => string.Concat(name.Split('-', '_').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));

	public static bool IsIdentifier(string name) => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

	/// <summary>The C# local for an object id ("run-button" -> "run_button"), never a keyword.</summary>
	static string Local(string id) { var s = new string(id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()); return "@" + (char.IsDigit(s[0]) ? "_" + s : s); }

	/// <summary>
	/// The designer-owned companion for <paramref name="uiFileName"/>: a partial
	/// <paramref name="className"/> whose <c>BuildUi(path)</c> builds the .ui with its signals
	/// connected to this class's handler methods. Regenerated whenever the .ui's signals change.
	/// </summary>
	public static string GenerateCompanion(string uiFileName, string? namespaceName, string className, IReadOnlyList<GtkSignalHandler> handlers)
	{
		var sb = new StringBuilder();
		sb.AppendLine("// <auto-generated>");
		sb.AppendLine($"//   Generated by the OpenDevelop GTK designer from {uiFileName}. Do not edit: it is");
		sb.AppendLine("//   rewritten whenever the .ui file's signals change. Handler bodies belong in the");
		sb.AppendLine($"//   other part of {className}.");
		sb.AppendLine("// </auto-generated>");
		sb.AppendLine("#nullable enable");
		sb.AppendLine();
		var indent = "";
		if (!string.IsNullOrEmpty(namespaceName)) { sb.AppendLine($"namespace {namespaceName}"); sb.AppendLine("{"); indent = "\t"; }
		sb.AppendLine($"{indent}partial class {className}");
		sb.AppendLine($"{indent}{{");
		sb.AppendLine($"{indent}\t/// <summary>Builds {uiFileName} and connects its &lt;signal&gt; handlers to this object.");
		sb.AppendLine($"{indent}\t/// GtkBuilder cannot resolve handler names to C# methods (it aborts on them), so the");
		sb.AppendLine($"{indent}\t/// signals are removed before building and connected through Gir.Core events here.</summary>");
		sb.AppendLine($"{indent}\tpublic global::Gtk.Builder BuildUi(string uiPath)");
		sb.AppendLine($"{indent}\t{{");
		sb.AppendLine($"{indent}\t\tvar document = global::System.Xml.Linq.XDocument.Load(uiPath);");
		sb.AppendLine($"{indent}\t\tforeach (var signal in global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.Where(document.Descendants(), e => e.Name.LocalName == \"signal\")))");
		sb.AppendLine($"{indent}\t\t\tsignal.Remove();");
		// AddFromString, not NewFromString: the latter aborts the process on any error; this throws.
		sb.AppendLine($"{indent}\t\tvar builder = global::Gtk.Builder.New();");
		sb.AppendLine($"{indent}\t\tbuilder.AddFromString(document.ToString(), -1);");
		foreach (var group in handlers.Where(h => !h.ObjectId.StartsWith(ActionPrefix, StringComparison.Ordinal) && !h.ObjectId.StartsWith("application-action:", StringComparison.Ordinal)).GroupBy(h => h.ObjectId)) {
			var supported = group.Where(h => h.Supported).ToList();
			if (supported.Count > 0) {
				var local = Local(group.Key);
				var type = "global::" + supported[0].ObjectType;
				sb.AppendLine($"{indent}\t\tvar {local} = ({type})(builder.GetObject(\"{group.Key}\") ?? throw new global::System.InvalidOperationException(\"{uiFileName} has no object '{group.Key}'.\"));");
				foreach (var h in supported)
					sb.AppendLine($"{indent}\t\t{local}.{h.EventName} += (_, args) => {h.Handler}({local}, args);");
			}
			foreach (var h in group.Where(h => !h.Supported))
				sb.AppendLine($"{indent}\t\t// <signal name=\"{h.Signal}\" handler=\"{h.Handler}\"> on '{h.ObjectId}' is not connected: {h.Reason}.");
		}
		sb.AppendLine($"{indent}\t\treturn builder;");
		sb.AppendLine($"{indent}\t}}");
		var windowActions = handlers.Where(h => h.ObjectId.StartsWith(ActionPrefix, StringComparison.Ordinal)).ToList();
		var applicationActions = handlers.Where(h => h.ObjectId.StartsWith("application-action:", StringComparison.Ordinal)).ToList();
		AppendMenuActions(sb, indent, uiFileName, windowActions, "AddMenuActions", "window");
		AppendMenuActions(sb, indent, uiFileName, applicationActions, "AddApplicationMenuActions", "application");
		sb.AppendLine($"{indent}}}");
		if (indent.Length > 0) sb.AppendLine("}");
		return sb.ToString();
	}

	static void AppendMenuActions(StringBuilder sb, string indent, string uiFileName, IReadOnlyList<GtkSignalHandler> actions, string method, string owner)
	{
		if (actions.Count == 0) return;
		sb.AppendLine();
		sb.AppendLine($"{indent}\t/// <summary>Registers the {owner} actions {uiFileName}'s menus refer to on <paramref name=\"map\"/>.");
		sb.AppendLine($"{indent}\t/// Pass the Gtk.Application for app.* actions and the window for win.* actions.</summary>");
		sb.AppendLine($"{indent}\tpublic void {method}(global::Gio.ActionMap map)");
		sb.AppendLine($"{indent}\t{{");
		foreach (var action in actions) {
			var name = action.ObjectId.StartsWith(ActionPrefix, StringComparison.Ordinal) ? action.ObjectId.Substring(ActionPrefix.Length) : action.ObjectId.Substring("application-action:".Length);
			var local = Local("action_" + action.Handler);
			var parameter = action.Target.Length == 0 ? "null" : $"global::GLib.Variant.Parse(null, \"{Escape(action.Target)}\", null, null)";
			if (action.State.Length > 0) {
				var state = $"global::GLib.Variant.Parse(null, \"{Escape(action.State)}\", null, null)";
				sb.AppendLine($"{indent}\t\tvar {local}_state = {state};");
				// GSimpleAction requires a stateful action's parameter type to match its state type.
				// A target supplies that type; without one the initial state is the only valid source.
				sb.AppendLine($"{indent}\t\tvar {local} = global::Gio.SimpleAction.NewStateful(\"{name}\", {(action.Target.Length == 0 ? local + "_state.GetType()" : parameter + ".GetType()")}, {local}_state);");
				sb.AppendLine($"{indent}\t\t{local}.OnChangeState += (_, args) => {{ if (args.Value != null) {local}.SetState(args.Value); {action.Handler}({local}, args); }};");
			} else {
				sb.AppendLine($"{indent}\t\tvar {local} = global::Gio.SimpleAction.New(\"{name}\", {(action.Target.Length == 0 ? "null" : parameter + ".GetType()")});");
				sb.AppendLine($"{indent}\t\t{local}.OnActivate += (_, args) => {action.Handler}({local}, args);");
			}
			sb.AppendLine($"{indent}\t\tmap.AddAction({local});");
		}
		sb.AppendLine($"{indent}\t}}");
	}

	static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
