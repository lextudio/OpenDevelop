using System.CodeDom.Compiler;
using ICSharpCode.Core;

namespace ICSharpCode.TextTemplating
{
	public static class TextTemplatingService
	{
		public static void ShowTemplateHostErrors(CompilerErrorCollection errors)
		{
			if (errors.Count == 0)
				return;

			foreach (CompilerError err in errors)
			{
				var location = string.IsNullOrEmpty(err.FileName)
					? string.Empty
					: $"{err.FileName}({err.Line},{err.Column}): ";
				var severity = err.IsWarning ? "warning" : "error";
				LoggingService.Warn($"T4 {severity}: {location}{err.ErrorText}");
			}

			MessageService.ShowWarning("T4 processing completed with errors. See the log for details.");
		}
	}
}
