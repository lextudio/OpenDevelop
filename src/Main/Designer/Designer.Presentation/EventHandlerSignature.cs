using System;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// The event-args type of a generated handler, derived from the event's delegate type as a host
/// reports it (<c>Type.FullName</c>). By the .NET convention a <c>XxxEventHandler</c> delegate takes
/// <c>XxxEventArgs</c> from the same namespace; the generic <c>XxxEventHandler`1[[T, ...]]</c>
/// takes <c>XxxEventArgs&lt;T&gt;</c>, except <c>System.EventHandler&lt;T&gt;</c>, which takes <c>T</c>. Results are <c>global::</c>-qualified so the code-behind needs
/// no extra using directive.
/// </summary>
public static class EventHandlerSignature
{
	public const string Fallback = "global::System.EventArgs";

	public static string EventArgsTypeName(string? delegateTypeFullName)
	{
		if (string.IsNullOrWhiteSpace(delegateTypeFullName))
			return Fallback;
		var name = delegateTypeFullName.Trim();
		string? typeArgument = null;
		var tick = name.IndexOf('`');
		if (tick >= 0)
		{
			// System.Windows.RoutedPropertyChangedEventHandler`1[[System.Double, System.Private.CoreLib, ...]]
			var open = name.IndexOf("[[", tick, StringComparison.Ordinal);
			if (open < 0 || name.Substring(tick + 1, open - tick - 1) != "1")
				return Fallback;
			var argument = name.Substring(open + 2);
			var end = argument.IndexOfAny(new[] { ',', ']' });
			if (end <= 0 || argument.IndexOf('`') >= 0)
				return Fallback;
			typeArgument = argument.Substring(0, end).Replace('+', '.');
			name = name.Substring(0, tick);
			// System.EventHandler<TEventArgs> takes TEventArgs itself.
			if (name == "System.EventHandler")
				return "global::" + typeArgument;
		}
		const string suffix = "EventHandler";
		if (!name.EndsWith(suffix, StringComparison.Ordinal) || name.Length == suffix.Length || name.IndexOf('+') >= 0)
			return Fallback;
		var argsType = "global::" + name.Substring(0, name.Length - suffix.Length) + "EventArgs";
		return typeArgument == null ? argsType : argsType + "<global::" + typeArgument + ">";
	}
}
