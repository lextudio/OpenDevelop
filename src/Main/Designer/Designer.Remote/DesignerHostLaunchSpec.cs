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

		/// <summary>
		/// Additional probing path for the shared managed closure, when the packaging produced one
		/// (<c>shared/managed</c> beside the application). A host is its own application with its
		/// own deps.json, so without this it needs a private copy of every shared assembly;
		/// hostpolicy falls back to a probing path for a deps asset that is missing from the app
		/// base, and the layout it expects there is <c>&lt;library&gt;/&lt;version&gt;/&lt;asset&gt;</c>,
		/// which is what the packaging writes.
		///
		/// The default discovers it from <see cref="AppContext.BaseDirectory"/> so every launch
		/// site shares one decision; a development tree has no such directory and gets no switch.
		/// </summary>
		public string? SharedProbingPath { get; init; } = FindSharedProbingPath();

		/// <summary>Empty in a development tree, or when packaging kept per-host copies.</summary>
		public static string? FindSharedProbingPath()
		{
			var baseDirectory = AppContext.BaseDirectory;
			if (String.IsNullOrEmpty(baseDirectory))
				return null;
			var candidate = Path.Combine(baseDirectory, "shared", "managed");
			return Directory.Exists(candidate) ? candidate : null;
		}

		public string BuildCommandLine(string childDll, int port, string token)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(childDll);
			ArgumentException.ThrowIfNullOrWhiteSpace(token);
			var child = Quote(childDll);
			var probing = String.IsNullOrEmpty(SharedProbingPath)
				? ""
				: $"--additionalprobingpath {Quote(SharedProbingPath)} ";
			var common = $"{child} --port {port} --token {token}";
			var hasRuntimeGraph = File.Exists(RuntimeConfigPath) && File.Exists(DepsFilePath);
			if (hasRuntimeGraph) {
				var arguments = $"exec {probing}--runtimeconfig {Quote(RuntimeConfigPath)} --depsfile {Quote(DepsFilePath)} {common}";
				var appBin = AppBinPath ?? Path.GetDirectoryName(RuntimeConfigPath);
				return IncludeAppBin && !string.IsNullOrWhiteSpace(appBin)
					? $"{arguments} --appbin {Quote(appBin)}"
					: arguments;
			}
			return IncludeAppBin && !string.IsNullOrWhiteSpace(AppBinPath)
				? $"exec {probing}{common} --appbin {Quote(AppBinPath)}"
				: $"exec {probing}{common}";
		}

		static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
	}
}
