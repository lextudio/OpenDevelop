// Extracted from the excluded NewFileDialog.cs (WinForms-only) so AbstractProject.cs's namespace-generation
// logic (a pure string utility, no WinForms dependency) keeps working without pulling NewFileDialog back in.
using System;
using System.Text;

namespace ICSharpCode.SharpDevelop.Project
{
	public static class NamespaceNameGenerator
	{
		public static string GenerateValidClassOrNamespaceName(string className, bool allowDot)
		{
			if (className == null)
				throw new ArgumentNullException("className");
			className = className.Trim();
			if (className.Length == 0)
				return string.Empty;
			StringBuilder nameBuilder = new StringBuilder();
			if (className[0] != '_' && !char.IsLetter(className, 0))
				nameBuilder.Append('_');
			for (int idx = 0; idx < className.Length; ++idx) {
				if (char.IsLetterOrDigit(className[idx]) || className[idx] == '_') {
					nameBuilder.Append(className[idx]);
				} else if (className[idx] == '.' && allowDot) {
					nameBuilder.Append('.');
				} else {
					nameBuilder.Append('_');
				}
			}
			return nameBuilder.ToString();
		}
	}
}
