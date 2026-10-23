namespace OpenDevelop.Terminal
{
	/// <summary>Quotes a file path so the shell reads it back as one argument.</summary>
	static class TerminalQuote
	{
		/// <param name="shellPath">The shell's executable path; only its name matters.</param>
		public static string Path(string path, string shellPath)
		{
			string shell = System.IO.Path.GetFileNameWithoutExtension(shellPath ?? "").ToLowerInvariant();
			switch (shell) {
				case "cmd":
					return "\"" + path + "\"";
				case "powershell":
				case "pwsh":
					return "'" + path.Replace("'", "''") + "'";
				default: // sh, bash, zsh, fish...: single quotes, with an embedded ' written as '\''
					return "'" + path.Replace("'", "'\\''") + "'";
			}
		}
	}
}
