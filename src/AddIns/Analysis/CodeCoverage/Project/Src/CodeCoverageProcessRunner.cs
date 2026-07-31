using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.CodeCoverage
{
	static class CodeCoverageProcessRunner
	{
		public static async Task<CodeCoverageProcessResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
		{
			var lines = new List<string> { "> " + FormatCommandLine(startInfo) };
			startInfo.UseShellExecute = false;
			startInfo.RedirectStandardOutput = true;
			startInfo.RedirectStandardError = true;

			using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true }) {
				process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
					if (e.Data != null) {
						lock (lines)
							lines.Add(e.Data);
					}
				};
				process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
					if (e.Data != null) {
						lock (lines)
							lines.Add(e.Data);
					}
				};

				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();

				await process.WaitForExitAsync(cancellationToken);
				lock (lines)
					return new CodeCoverageProcessResult(process.ExitCode, lines.ToArray());
			}
		}

		public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments, string workingDirectory)
		{
			var startInfo = new ProcessStartInfo {
				FileName = fileName,
				CreateNoWindow = true,
				WorkingDirectory = workingDirectory
			};

			foreach (string argument in arguments)
				startInfo.ArgumentList.Add(argument);

			return startInfo;
		}

		static string FormatCommandLine(ProcessStartInfo startInfo)
		{
			if (!string.IsNullOrEmpty(startInfo.Arguments))
				return QuoteIfNeeded(startInfo.FileName) + " " + startInfo.Arguments;

			return string.Join(" ", new[] { startInfo.FileName }.Concat(startInfo.ArgumentList).Select(QuoteIfNeeded));
		}

		static string QuoteIfNeeded(string value)
		{
			return value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
		}
	}
}
