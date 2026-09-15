using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ICSharpCode.SharpDevelop.Project.Sdk
{
	public enum DotNetSdkOrigin
	{
		System,
		Bundled,
		Custom
	}

	/// <summary>
	/// One discovered ".NET SDK root" - a DOTNET_ROOT-style folder containing a "dotnet" host
	/// executable and an "sdk/" subfolder with one or more installed SDK versions.
	/// </summary>
	public sealed class DotNetSdkInfo
	{
		public string Label { get; set; }
		public string RootPath { get; set; }
		public string DotnetExecutablePath { get; set; }
		public IReadOnlyList<string> InstalledSdkVersions { get; set; } = new List<string>();
		public string HighestSdkVersion { get; set; }
		public DotNetSdkOrigin Origin { get; set; }

		/// <summary>
		/// The processor architecture of this root's "dotnet" host executable, read straight from
		/// its PE header (see <see cref="DotNetSdkService.DetectHostArchitecture"/>) - null if it
		/// couldn't be determined. Some MSBuild-adjacent assemblies this SDK ships (notably
		/// Microsoft.Build.NuGetSdkResolver.dll) are ReadyToRun images built for this exact
		/// architecture, not AnyCPU - loading one into a process of a different architecture fails
		/// the OS loader outright ("Format of the executable (.exe) or library (.dll) is invalid"),
		/// so any in-process consumer (this IDE's own embedded MSBuild engine) must only ever pick
		/// an SDK whose Architecture matches <see cref="RuntimeInformation.ProcessArchitecture"/>.
		/// </summary>
		public Architecture? Architecture { get; set; }
	}
}
