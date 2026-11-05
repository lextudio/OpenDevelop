using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Debugger.AddIn.Android
{
	/// <summary>Build-time configuration for the .NET 11 Android CoreCLR debugger.</summary>
	public static class AndroidDebugSupport
	{
		static readonly Regex AndroidTargetFramework = new Regex(@"^net(?<major>\d+)\.\d+-android(?:\d+(?:\.\d+)*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		static readonly Regex PackageIdentifier = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant);

		public static bool IsDebuggableTargetFramework(string targetFramework)
		{
			var match = AndroidTargetFramework.Match(targetFramework ?? string.Empty);
			return match.Success && int.TryParse(match.Groups["major"].Value, out var major) && major >= 11;
		}

		/// <summary>
		/// Creates an import for a single debugger build. The session token is written into the app's
		/// private files after installation, never into the APK or this build import.
		/// </summary>
		public static string WriteBuildInjection(string directory, string nativeLibrary, string abi, string packageName, string existingTargets = null)
		{
			if (!File.Exists(nativeLibrary)) throw new FileNotFoundException("The Android debugger agent is missing.", nativeLibrary);
			if (abi is not ("arm64-v8a" or "armeabi-v7a" or "x86_64")) throw new ArgumentException("Unsupported Android ABI.", nameof(abi));
			if (!PackageIdentifier.IsMatch(packageName ?? string.Empty)) throw new ArgumentException("Invalid Android package name.", nameof(packageName));
			Directory.CreateDirectory(directory);
			string environment = Path.Combine(directory, "android-debug.env");
			File.WriteAllLines(environment, new[] {
				"CORECLR_ENABLE_PROFILING=1",
				"CORECLR_PROFILER={9DC623E8-C88F-4FD5-AD99-77E67E1D9631}",
				"CORECLR_PROFILER_PATH=" + Path.GetFileName(nativeLibrary),
				"OD_ANDROID_DEBUG_PACKAGE=" + packageName
			});
			string targets = Path.Combine(directory, "android-debug.targets");
			var document = new XDocument(new XElement("Project",
				string.IsNullOrWhiteSpace(existingTargets) || !File.Exists(existingTargets) ? null : new XElement("Import", new XAttribute("Project", existingTargets)),
				new XElement("PropertyGroup", new XElement("EmbedAssembliesIntoApk", "true")),
				new XElement("ItemGroup",
					new XElement("AndroidNativeLibrary", new XAttribute("Include", nativeLibrary), new XAttribute("Link", abi + "/" + Path.GetFileName(nativeLibrary))),
					new XElement("AndroidEnvironment", new XAttribute("Include", environment)))));
			document.Save(targets);
			return targets;
		}
	}
}
