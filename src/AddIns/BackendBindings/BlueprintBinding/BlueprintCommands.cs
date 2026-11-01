using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace BlueprintBinding
{
	public sealed class RegisterDevFlowActionsCommand : DevFlowActionRegistrationCommand { }

	/// <summary>The IDE end of <see cref="BlueprintCompiler"/>: the active file, the located compiler and the message shown.</summary>
	static class BlueprintActions
	{
		public static string Summary(BlueprintResult result, string verb)
		{
			if (result.Success) return $"{verb} wrote {Path.GetFileName(result.Output)}" + (result.Diagnostics.Count > 0 ? $" with {result.Diagnostics.Count} warning(s)." : ".");
			var problems = result.Diagnostics.Take(5).Select(d => $"{Path.GetFileName(d.File ?? "")}({d.Line},{d.Column}): {d.Message.Split('\n')[0]}");
			return result.Failure ?? string.Join(Environment.NewLine, problems) + (result.Diagnostics.Count > 5 ? Environment.NewLine + $"... and {result.Diagnostics.Count - 5} more." : "");
		}

		public static void Run(string extension, Func<string, string, BlueprintResult> action, string verb)
		{
			var file = SD.Workbench?.ActiveViewContent?.PrimaryFile;
			if (file == null || !string.Equals(Path.GetExtension(file.FileName), extension, StringComparison.OrdinalIgnoreCase)) {
				MessageService.ShowMessage($"Open a {extension} file first.");
				return;
			}
			if (BlueprintCompilerLocator.Find() is not { } compiler) {
				MessageService.ShowMessage("blueprint-compiler was not found. Install it (for example brew install blueprint-compiler) or set " + BlueprintCompilerLocator.EnvironmentVariable + " to its path.");
				return;
			}
			if (file.IsDirty) file.SaveToDisk();
			MessageService.ShowMessage(Summary(action(compiler, file.FileName), verb));
		}
	}

	/// <summary>Tools &gt; Compile Blueprint: writes the active .blp's GtkBuilder .ui beside it.</summary>
	public sealed class CompileBlueprintCommand : AbstractMenuCommand
	{
		public override void Run() => BlueprintActions.Run(".blp", (compiler, file) => BlueprintCompiler.Compile(compiler, file), "Compile");
	}

	/// <summary>Tools &gt; Convert .ui to Blueprint: writes the active .ui's Blueprint beside it (never over an existing .blp).</summary>
	public sealed class DecompileToBlueprintCommand : AbstractMenuCommand
	{
		public override void Run() => BlueprintActions.Run(".ui", (compiler, file) => BlueprintCompiler.Decompile(compiler, file), "Convert");
	}

	[DevFlowUIThread]
	public static class BlueprintDevFlowActions
	{
		static string Json(BlueprintResult result) => JsonSerializer.Serialize(new {
			success = result.Success, output = result.Output, failure = result.Failure,
			diagnostics = result.Diagnostics.Select(d => new { severity = d.Severity, message = d.Message, file = d.File, line = d.Line, column = d.Column, length = d.Length, hints = d.Hints })
		});

		[DevFlowAction("od.blueprint.status", Description = "The located blueprint-compiler (null when none is installed)")]
		public static string Status() => JsonSerializer.Serialize(new { compiler = BlueprintCompilerLocator.Find() });

		[DevFlowAction("od.blueprint.compile", Description = "Compile a .blp to a GtkBuilder .ui (beside it unless an output path is given) with blueprint-compiler; returns its diagnostics")]
		public static string Compile(string path, string output = "")
			=> BlueprintCompilerLocator.Find() is { } compiler
				? Json(BlueprintCompiler.Compile(compiler, path, output.Length == 0 ? null : output))
				: Json(new BlueprintResult(false, null, Array.Empty<BlueprintDiagnostic>(), "blueprint-compiler was not found."));

		[DevFlowAction("od.blueprint.decompile", Description = "Convert a GtkBuilder .ui to Blueprint (beside it unless an output path is given) with blueprint-compiler; an existing .blp is kept unless overwrite is true")]
		public static string Decompile(string path, string output = "", bool overwrite = false)
			=> BlueprintCompilerLocator.Find() is { } compiler
				? Json(BlueprintCompiler.Decompile(compiler, path, output.Length == 0 ? null : output, overwrite))
				: Json(new BlueprintResult(false, null, Array.Empty<BlueprintDiagnostic>(), "blueprint-compiler was not found."));
	}
}
