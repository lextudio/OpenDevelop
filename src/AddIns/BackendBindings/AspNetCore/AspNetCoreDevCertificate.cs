using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.AspNetCore
{
	public enum AspNetCoreDevCertificateStatus
	{
		Trusted,
		Untrusted,
		Missing,
		Error
	}

	public sealed class AspNetCoreDevCertificateResult
	{
		internal AspNetCoreDevCertificateResult(AspNetCoreDevCertificateStatus status, string message)
		{
			Status = status;
			Message = message ?? string.Empty;
		}

		public AspNetCoreDevCertificateStatus Status { get; }
		public string Message { get; }
	}

	/// <summary>Cross-platform wrapper around the SDK's ASP.NET Core development-certificate tool.</summary>
	public static class AspNetCoreDevCertificate
	{
		public static async Task<AspNetCoreDevCertificateResult> CheckAsync(CancellationToken cancellationToken = default)
		{
			var result = await RunAsync(new[] { "dev-certs", "https", "--check-trust-machine-readable" }, cancellationToken);
			return AnalyzeMachineReadableCheck(result.ExitCode, result.StandardOutput, result.StandardError);
		}

		public static async Task<AspNetCoreDevCertificateResult> TrustAsync(CancellationToken cancellationToken = default)
		{
			var result = await RunAsync(new[] { "dev-certs", "https", "--trust" }, cancellationToken);
			if (result.ExitCode != 0)
				return new AspNetCoreDevCertificateResult(AspNetCoreDevCertificateStatus.Error, BestMessage(result));
			return await CheckAsync(cancellationToken);
		}

		public static AspNetCoreDevCertificateResult AnalyzeMachineReadableCheck(int exitCode, string standardOutput, string standardError)
		{
			if (exitCode != 0)
				return new AspNetCoreDevCertificateResult(AspNetCoreDevCertificateStatus.Error,
					string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError);
			try {
				using var document = JsonDocument.Parse(standardOutput ?? "[]");
				if (document.RootElement.ValueKind != JsonValueKind.Array)
					throw new JsonException("The response root must be an array.");
				var certificates = document.RootElement;
				if (certificates.GetArrayLength() == 0)
					return new AspNetCoreDevCertificateResult(AspNetCoreDevCertificateStatus.Missing, "No valid HTTPS development certificate was found.");
				var trusted = certificates.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.Object
					&& c.TryGetProperty("TrustLevel", out var level)
					&& string.Equals(level.GetString(), "Full", StringComparison.OrdinalIgnoreCase));
				return new AspNetCoreDevCertificateResult(
					trusted ? AspNetCoreDevCertificateStatus.Trusted : AspNetCoreDevCertificateStatus.Untrusted,
					trusted ? "A trusted HTTPS development certificate is available." : "An HTTPS development certificate exists but is not trusted.");
			} catch (Exception ex) when (ex is JsonException || ex is ArgumentException) {
				return new AspNetCoreDevCertificateResult(AspNetCoreDevCertificateStatus.Error,
					"Could not understand the dotnet dev-certs response: " + ex.Message);
			}
		}

		static async Task<CommandResult> RunAsync(string[] arguments, CancellationToken cancellationToken)
		{
			var startInfo = new ProcessStartInfo("dotnet") {
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);
			using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the dotnet CLI.");
			try {
				var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
				var error = process.StandardError.ReadToEndAsync(cancellationToken);
				await process.WaitForExitAsync(cancellationToken);
				return new CommandResult(process.ExitCode, await output, await error);
			} catch (OperationCanceledException) {
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
				throw;
			}
		}

		static string BestMessage(CommandResult result) =>
			string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;

		readonly record struct CommandResult(int ExitCode, string StandardOutput, string StandardError);
	}
}
