// Runtime-neutral dotnet-exec argument builder for isolated designer hosts.

using System;
using System.IO;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>Describes how a designer child is launched. It deliberately excludes runtime
	/// selection and process-environment policy; adapters supply those concerns before creating
	/// this value.</summary>
	public sealed class DesignerHostLaunchSpec
	{
		public string RuntimeConfigPath { get; init; } = "";
		public string DepsFilePath { get; init; } = "";
		public string? AppBinPath { get; init; }

		/// <summary>Includes <c>--appbin</c> when an app directory is available. This is needed
		/// by markup hosts that preload application assemblies, but not by WinForms.</summary>
		public bool IncludeAppBin { get; init; }

		public string BuildCommandLine(string childDll, int port, string token)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(childDll);
			ArgumentException.ThrowIfNullOrWhiteSpace(token);
			var child = Quote(childDll);
			var common = $"{child} --port {port} --token {token}";
			var hasRuntimeGraph = File.Exists(RuntimeConfigPath) && File.Exists(DepsFilePath);
			if (hasRuntimeGraph) {
				var arguments = $"exec --runtimeconfig {Quote(RuntimeConfigPath)} --depsfile {Quote(DepsFilePath)} {common}";
				var appBin = AppBinPath ?? Path.GetDirectoryName(RuntimeConfigPath);
				return IncludeAppBin && !string.IsNullOrWhiteSpace(appBin)
					? $"{arguments} --appbin {Quote(appBin)}"
					: arguments;
			}
			return IncludeAppBin && !string.IsNullOrWhiteSpace(AppBinPath)
				? $"exec {common} --appbin {Quote(AppBinPath)}"
				: $"exec {common}";
		}

		static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
	}
}
