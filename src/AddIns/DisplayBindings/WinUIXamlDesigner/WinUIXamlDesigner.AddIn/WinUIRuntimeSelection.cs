using System;
namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>
/// Which renderer orders the Microsoft WinUI designer runtimes: the <c>OD_WINUI_RUNTIME</c> value wins, then the stored
/// <c>WinUIXamlDesigner.Runtime</c> preference, then <see cref="DefaultRuntime"/>. Pure and dependency-free so it is
/// unit-tested without the IDE (source-linked into <c>OpenDevelop.Base.Tests</c>).
/// </summary>
public static class WinUIRuntimeSelection
{
	public const string DefaultRuntime = "surface";

	/// <summary>The effective runtime: <c>uno</c>, <c>microsoft</c> or <c>surface</c>, lower case. A blank value is
	/// ignored; an unrecognised one is reported through <paramref name="unrecognized"/> and replaced by the default.</summary>
	public static string Resolve(string? environmentValue, string? storedValue, out string? unrecognized)
	{
		unrecognized = null;
		var selection = environmentValue?.Trim();
		if (string.IsNullOrEmpty(selection))
			selection = storedValue?.Trim();
		if (string.IsNullOrEmpty(selection))
			return DefaultRuntime;
		foreach (var known in new[] { "uno", "microsoft", "surface" }) {
			if (string.Equals(selection, known, StringComparison.OrdinalIgnoreCase))
				return known;
		}
		unrecognized = selection;
		return DefaultRuntime;
	}

	/// <summary>
	/// The host families to register for a selection, in registration order (the registry tries the last one first and a family only accepts
	/// documents of its own runtime kind, so <c>uno</c> never takes a Microsoft WinUI project and the other two never take an Uno one).
	/// <c>surface</c> deliberately does not include <c>microsoft</c>: there is no fallback from the Surface host to another host. When the
	/// Surface host cannot be used (files not deployed, provisioning failed) the designer reports why and renders nothing; the Microsoft
	/// host is used only when it is selected.
	/// </summary>
	public static string[] HostsToRegister(string selection) => selection switch {
		"surface" => new[] { "uno", "surface" },
		"uno" => new[] { "microsoft", "uno" },
		_ => new[] { "uno", "microsoft" },
	};

	/// <summary>The Options page offers only <c>surface</c> and <c>microsoft</c>; a stored value it cannot show (<c>uno</c>,
	/// or anything unrecognised) is displayed as the default.</summary>
	public static string ToOptionsChoice(string? stored)
		=> string.Equals(stored?.Trim(), "microsoft", StringComparison.OrdinalIgnoreCase) ? "microsoft" : DefaultRuntime;

	/// <summary>The value to write when the Options dialog is accepted, or <c>null</c> to leave the stored value alone: it is
	/// untouched unless the user actually changed the choice, so a stored <c>uno</c> is not overwritten by accepting the dialog.</summary>
	public static string? ValueToStore(string? stored, string selectedChoice)
		=> string.Equals(ToOptionsChoice(stored), selectedChoice, StringComparison.OrdinalIgnoreCase) ? null : selectedChoice.ToLowerInvariant();
}
