// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.Core;
using Microsoft.Build.Construction;
using Microsoft.Build.Execution;
using MSBuild = Microsoft.Build;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Messing with MSBuild's internals.
	/// </summary>
	public static class MSBuildInternals
	{
		/// <summary>
		/// SharpDevelop uses one project collection per solution.
		/// Code accessing one of those collections (even if indirectly through MSBuild) should lock on
		/// MSBuildInternals.SolutionProjectCollectionLock.
		/// </summary>
		public readonly static object SolutionProjectCollectionLock = new object();
		
		// TODO: I think MSBuild actually uses OrdinalIgnoreCase. SharpDevelop 3.x just used string.operator ==, so I'm keeping
		// that setting until all code is ported to use PropertyNameComparer and we've verified what MSBuild is actually using.
		public readonly static StringComparer PropertyNameComparer = StringComparer.Ordinal;
		public readonly static StringComparer ConfigurationNameComparer = ConfigurationAndPlatform.ConfigurationNameComparer;
		static bool msbuildEnvironmentInitialized;
		
		public static void InitializeMSBuildEnvironment()
		{
			if (msbuildEnvironmentInitialized)
				return;
			msbuildEnvironmentInitialized = true;
			
			var dotnet = GetDotnetInstallation();
			if (dotnet == null)
				return;
			
			Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnet.Root);
			Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", dotnet.HostPath);
			
			string sdksDir = Path.Combine(dotnet.Root, "sdk");
			if (!Directory.Exists(sdksDir))
				return;
			
			string latestSdk = Directory.GetDirectories(sdksDir)
				.Where(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out _))
				.OrderByDescending(d => {
					Version version;
					Version.TryParse(Path.GetFileName(d).Split('-')[0], out version);
					return version;
				})
				.FirstOrDefault();
			
			if (latestSdk == null)
				return;
			
			Environment.SetEnvironmentVariable("MSBuildSDKsPath", Path.Combine(latestSdk, "Sdks"));
			Environment.SetEnvironmentVariable("MSBuildExtensionsPath", latestSdk);
			Environment.SetEnvironmentVariable("MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET", Path.Combine(latestSdk, "SdkResolvers"));
			Environment.SetEnvironmentVariable("MSBUILD_NUGET_PATH", latestSdk);
			LoggingService.InfoFormatted("MSBuild environment initialized: DOTNET_ROOT={0}, MSBuildSDKsPath={1}, MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET={2}",
				dotnet.Root,
				Environment.GetEnvironmentVariable("MSBuildSDKsPath"),
				Environment.GetEnvironmentVariable("MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET"));
			
			string binDir = Path.GetDirectoryName(typeof(MSBuildInternals).Assembly.Location);
			if (!string.IsNullOrEmpty(binDir)) {
				foreach (string dependency in new[] {
					"Microsoft.Build.NuGetSdkResolver.dll",
					"NuGet.Common.dll",
					"NuGet.Configuration.dll",
					"NuGet.Frameworks.dll",
					"NuGet.Packaging.dll",
					"NuGet.ProjectModel.dll",
					"NuGet.Protocol.dll",
					"NuGet.Versioning.dll"
				}) {
					string source = Path.Combine(latestSdk, dependency);
					string destination = Path.Combine(binDir, dependency);
					if (!File.Exists(destination) && File.Exists(source))
						File.Copy(source, destination);
				}
			}
		}
		
		sealed class DotnetInstallation
		{
			public string Root;
			public string HostPath;
		}
		
		static DotnetInstallation GetDotnetInstallation()
		{
			string hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
			if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath))
				return new DotnetInstallation { Root = Path.GetDirectoryName(hostPath), HostPath = hostPath };
			
			string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
			if (!string.IsNullOrEmpty(dotnetRoot) && Directory.Exists(dotnetRoot)) {
				string dotnetHost = Path.Combine(dotnetRoot, "dotnet");
				if (File.Exists(dotnetHost))
					return new DotnetInstallation { Root = dotnetRoot, HostPath = dotnetHost };
			}
			
			string processPath = Environment.ProcessPath;
			if (!string.IsNullOrEmpty(processPath) && string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
				return new DotnetInstallation { Root = Path.GetDirectoryName(processPath), HostPath = processPath };
			
			foreach (string candidate in new[] {
				"/usr/local/share/dotnet",
				"/opt/homebrew/share/dotnet",
				"/Users/lextm/uno-tools/librewpf/.dotnet",
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
			}) {
				string dotnetHost = Path.Combine(candidate, "dotnet");
				if (Directory.Exists(candidate) && File.Exists(dotnetHost))
					return new DotnetInstallation { Root = candidate, HostPath = dotnetHost };
			}
			
			return null;
		}
		
		internal static void UnloadProject(MSBuild.Evaluation.ProjectCollection projectCollection, MSBuild.Evaluation.Project project)
		{
			lock (SolutionProjectCollectionLock) {
				projectCollection.UnloadProject(project);
			}
		}
		
		internal static MSBuild.Evaluation.Project LoadProject(MSBuild.Evaluation.ProjectCollection projectCollection, ProjectRootElement rootElement, IDictionary<string, string> globalProps)
		{
			InitializeMSBuildEnvironment();
			lock (SolutionProjectCollectionLock) {
				string toolsVersion = rootElement.ToolsVersion;
				if (string.IsNullOrEmpty(toolsVersion))
					toolsVersion = projectCollection.DefaultToolsVersion;
				return new MSBuild.Evaluation.Project(rootElement, globalProps, toolsVersion, projectCollection);
			}
		}
		
		internal static ProjectInstance LoadProjectInstance(MSBuild.Evaluation.ProjectCollection projectCollection, ProjectRootElement rootElement, IDictionary<string, string> globalProps)
		{
			InitializeMSBuildEnvironment();
			lock (SolutionProjectCollectionLock) {
				string toolsVersion = rootElement.ToolsVersion;
				if (string.IsNullOrEmpty(toolsVersion))
					toolsVersion = projectCollection.DefaultToolsVersion;
				return new ProjectInstance(rootElement, globalProps, toolsVersion, projectCollection);
			}
		}
		
		public static void AddMSBuildSolutionProperties(ISolution solution, IDictionary<string, string> propertyDict)
		{
			propertyDict["SolutionDir"] = solution.Directory.ToStringWithTrailingBackslash();
			propertyDict["SolutionExt"] = solution.FileName.GetExtension();
			propertyDict["SolutionFileName"] = solution.FileName.GetFileName();
			propertyDict["SolutionName"] = solution.Name ?? string.Empty;
			propertyDict["SolutionPath"] = solution.FileName;
		}
		
		public const string MSBuildXmlNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";
		
		#region Escaping
		/// <summary>
		/// Escapes special MSBuild characters ( '%', '*', '?', '@', '$', '(', ')', ';', "'" ).
		/// </summary>
		public static string Escape(string text)
		{
			return MSBuild.Evaluation.ProjectCollection.Escape(text);
		}
		
		/// <summary>
		/// Unescapes escaped MSBuild characters.
		/// </summary>
		public static string Unescape(string text)
		{
			return MSBuild.Evaluation.ProjectCollection.Unescape(text);
		}
		#endregion
		
		/// <summary>
		/// This is a special case in MSBuild we need to take care of.
		/// </summary>
		public static string FixPlatformNameForProject(string platformName)
		{
			if (ConfigurationAndPlatform.ConfigurationNameComparer.Equals(platformName, "Any CPU")) {
				return "AnyCPU";
			} else {
				return platformName;
			}
		}
		
		/// <summary>
		/// This is a special case in MSBuild we need to take care of.
		/// Opposite of FixPlatformNameForProject
		/// </summary>
		public static string FixPlatformNameForSolution(string platformName)
		{
			if (ConfigurationAndPlatform.ConfigurationNameComparer.Equals(platformName, "AnyCPU")) {
				return "Any CPU";
			} else {
				return platformName;
			}
		}
		
		internal static PropertyStorageLocations GetLocationFromCondition(MSBuild.Construction.ProjectElement element)
		{
			while (element != null) {
				if (!string.IsNullOrEmpty(element.Condition))
					return GetLocationFromCondition(element.Condition);
				element = element.Parent;
			}
			return PropertyStorageLocations.Base;
		}
		
		internal static PropertyStorageLocations GetLocationFromCondition(string condition)
		{
			if (string.IsNullOrEmpty(condition)) {
				return PropertyStorageLocations.Base;
			}
			PropertyStorageLocations location = 0; // 0 is unknown
			if (condition.IndexOf("$(Configuration)", StringComparison.OrdinalIgnoreCase) >= 0)
				location |= PropertyStorageLocations.ConfigurationSpecific;
			if (condition.IndexOf("$(Platform)", StringComparison.OrdinalIgnoreCase) >= 0)
				location |= PropertyStorageLocations.PlatformSpecific;
			return location;
		}
	}
}
