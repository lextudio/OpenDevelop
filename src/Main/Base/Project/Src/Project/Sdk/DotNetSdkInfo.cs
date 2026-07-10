using System.Collections.Generic;

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
	}
}
