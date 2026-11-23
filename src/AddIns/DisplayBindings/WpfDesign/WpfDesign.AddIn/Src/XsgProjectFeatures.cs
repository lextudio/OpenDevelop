using System;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>Which XAML source generator (XSG) features a project has on, read from its evaluated MSBuild
	/// properties. BAML and XSG are separate compile paths: every feature belongs to XSG and is off for a BAML project.</summary>
	public static class XsgProjectFeatures
	{
		/// <summary>Simpler XAML: needs the overall switch on and the feature not switched off (the SDK defaults the
		/// feature to the overall switch; a feature set without the overall switch does nothing).</summary>
		public static bool ImplicitXamlNamespaces(Func<string, string> evaluated)
		{
			if (evaluated == null)
				return false;
			try {
				return IsTrue(evaluated("UseLibreWpfXamlSourceGenerator"))
					&& !string.Equals(evaluated("LibreWpfSimplerXaml")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
			} catch (Exception ex) when (ex is not OutOfMemoryException) {
				return false;
			}
		}

		static bool IsTrue(string value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
	}
}
