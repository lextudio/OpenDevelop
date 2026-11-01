using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BlueprintBinding
{
	/// <summary>One problem the compiler reported: <see cref="Line"/> and <see cref="Column"/> are 1-based, <see cref="Length"/> is the
	/// width of the caret underline (0 when the compiler gave none).</summary>
	public sealed record BlueprintDiagnostic(string Severity, string Message, string? File, int Line, int Column, int Length, IReadOnlyList<string> Hints);

	/// <summary>What a compile or decompile did. <see cref="Failure"/> is set when the compiler could not be run or crashed (as opposed to
	/// reporting problems in the input, which are <see cref="Diagnostics"/>).</summary>
	public sealed record BlueprintResult(bool Success, string? Output, IReadOnlyList<BlueprintDiagnostic> Diagnostics, string? Failure);

	/// <summary>
	/// Runs <c>blueprint-compiler</c> for one file. <c>compile</c> turns a <c>.blp</c> into the GtkBuilder <c>.ui</c> the GTK designer
	/// and GTK itself read; <c>decompile</c> turns an existing <c>.ui</c> into Blueprint. The compiler colours its messages even when
	/// piped (<c>NO_COLOR</c> is not honoured), so they are stripped before they are parsed. Pure process work, no IDE services.
	/// </summary>
	public static class BlueprintCompiler
	{
		static readonly Regex Ansi = new Regex("\u001b\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
		static readonly Regex Header = new Regex(@"^(error|warning): (?<message>.*)$", RegexOptions.Compiled);
		static readonly Regex Location = new Regex(@"^at (?<file>.+?) line (?<line>\d+) column (?<column>\d+):?$", RegexOptions.Compiled);
		static readonly Regex Caret = new Regex(@"^\s*\|\s*(?<pad> *)(?<carets>\^+)", RegexOptions.Compiled);

		/// <summary>The problems in the compiler's message text: <c>error: ...</c> / <c>warning: ...</c> blocks with their <c>at FILE line L column C:</c>,
		/// the source line with its caret underline, and <c>hint:</c> / <c>suggestion:</c> lines.</summary>
		public static IReadOnlyList<BlueprintDiagnostic> ParseDiagnostics(string text)
		{
			var result = new List<BlueprintDiagnostic>();
			string? severity = null, message = null, file = null;
			int line = 0, column = 0, length = 0;
			var hints = new List<string>();
			void Flush()
			{
				if (severity != null) result.Add(new BlueprintDiagnostic(severity, message ?? "", file, line, column, length, hints.ToList()));
				severity = message = file = null; line = column = length = 0; hints.Clear();
			}
			foreach (var raw in Ansi.Replace(text ?? "", "").Replace("\r", "").Split('\n')) {
				var current = raw.TrimEnd();
				if (Header.Match(current) is { Success: true } header) { Flush(); severity = header.Groups[1].Value; message = header.Groups["message"].Value; continue; }
				if (severity == null) continue;
				if (Location.Match(current) is { Success: true } location) { file = location.Groups["file"].Value; line = int.Parse(location.Groups["line"].Value); column = int.Parse(location.Groups["column"].Value); continue; }
				if (Caret.Match(current) is { Success: true } caret) { length = caret.Groups["carets"].Length; continue; }
				if (current.StartsWith("hint:", StringComparison.Ordinal)) hints.Add(current.Substring(5).Trim());
				else if (current.StartsWith("suggestion:", StringComparison.Ordinal)) hints.Add("suggestion: " + current.Substring(11).Trim());
			}
			Flush();
			return result;
		}

		/// <summary>Compiles <paramref name="blueprintPath"/> to <paramref name="outputPath"/> (the same name with <c>.ui</c> when null).</summary>
		public static BlueprintResult Compile(string compiler, string blueprintPath, string? outputPath = null)
		{
			outputPath ??= Path.ChangeExtension(blueprintPath, ".ui");
			var run = Run(compiler, new[] { "compile", "--output", outputPath, blueprintPath }, Path.GetDirectoryName(Path.GetFullPath(blueprintPath)));
			var diagnostics = ParseDiagnostics(run.Error + "\n" + run.Output);
			if (run.ExitCode == 0 && File.Exists(outputPath)) return new BlueprintResult(true, outputPath, diagnostics, null);
			return new BlueprintResult(false, null, diagnostics, diagnostics.Any(d => d.Severity == "error") ? null : Describe(run));
		}

		/// <summary>Converts <paramref name="uiPath"/> to Blueprint at <paramref name="outputPath"/> (the same name with <c>.blp</c> when null); an existing
		/// file is not overwritten unless <paramref name="overwrite"/> is set.</summary>
		public static BlueprintResult Decompile(string compiler, string uiPath, string? outputPath = null, bool overwrite = false)
		{
			outputPath ??= Path.ChangeExtension(uiPath, ".blp");
			if (File.Exists(outputPath) && !overwrite) return new BlueprintResult(false, null, Array.Empty<BlueprintDiagnostic>(), $"{Path.GetFileName(outputPath)} already exists; it was not overwritten.");
			var run = Run(compiler, new[] { "decompile", uiPath }, Path.GetDirectoryName(Path.GetFullPath(uiPath)));
			var diagnostics = ParseDiagnostics(run.Error);
			if (run.ExitCode != 0 || run.Output.Trim().Length == 0) return new BlueprintResult(false, null, diagnostics, Describe(run));
			File.WriteAllText(outputPath, run.Output.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));
			return new BlueprintResult(true, outputPath, diagnostics, null);
		}

		sealed record ProcessRun(int ExitCode, string Output, string Error);

		/// <summary>One line saying why a run failed without a diagnostic: a crash prints a whole Python traceback, of which only the
		/// last exception line is useful.</summary>
		static string Describe(ProcessRun run)
		{
			if (run.ExitCode == -1) return run.Error; // the program never ran, or was stopped: Run wrote the reason itself
			var lines = Ansi.Replace(run.Error, "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
			var cause = lines.LastOrDefault(l => Regex.IsMatch(l, @"^[\w.]+(Error|Exception)\b") || l.Contains("SAXParseException", StringComparison.Ordinal))
				?? lines.FirstOrDefault(l => !l.StartsWith("Traceback", StringComparison.Ordinal));
			return $"blueprint-compiler failed (exit {run.ExitCode})" + (cause == null ? "." : ": " + cause);
		}

		static ProcessRun Run(string compiler, IEnumerable<string> arguments, string? workingDirectory)
		{
			var start = new ProcessStartInfo(compiler) {
				RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
				WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
				StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
			};
			foreach (var argument in arguments) start.ArgumentList.Add(argument);
			start.Environment["NO_COLOR"] = "1";
			start.Environment["PYTHONUTF8"] = "1";
			try {
				using var process = Process.Start(start)!;
				process.StandardInput.Close();
				var error = process.StandardError.ReadToEndAsync();
				var output = process.StandardOutput.ReadToEndAsync();
				if (!process.WaitForExit(60000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return new ProcessRun(-1, "", "blueprint-compiler did not finish within 60 seconds."); }
				return new ProcessRun(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
			} catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) {
				return new ProcessRun(-1, "", "Could not run blueprint-compiler: " + ex.Message);
			}
		}
	}
}
