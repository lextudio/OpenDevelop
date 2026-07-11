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
using ICSharpCode.SharpDevelop.Project.Sdk;
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

			// Was: an independent GetDotnetInstallation()/manual "newest sdk/ subdirectory" scan
			// that didn't know about Homebrew's split package layout (the "dotnet" binary under
			// .../Cellar/dotnet/<version>/bin is a separate directory from the actual SDK/runtime
			// tree under .../Cellar/dotnet/<version>/libexec/sdk) - so on a machine where
			// DOTNET_HOST_PATH pointed at the Homebrew bin/ wrapper, "sdk" under that directory
			// never existed and this whole method silently no-opped, leaving MSBuildToolsPath/
			// MSBuildSDKsPath unset or stale. That broke in-process MSBuild project evaluation
			// (Microsoft.CSharp.targets not found), which starves SD.ProjectService.AllProjects
			// and - via RoslynWorkspaceHelper.GetSolution() - makes RoslynParser.Parse() return
			// null for every .cs file. DotNetSdkService.ResolvePathDotnetRoot() already handles the
			// Homebrew split correctly; route through the single shared SDK-resolution service
			// instead of this file's own, incomplete copy of the same logic (matching
			// MinimalMSBuildEngine, which already made this switch).
			var sdk = DotNetSdkService.ResolveEffectiveSdk();
			if (sdk?.RootPath == null || sdk.HighestSdkVersion == null)
				return;

			foreach (var kv in DotNetSdkService.GetEnvironmentVariablesFor(sdk))
				Environment.SetEnvironmentVariable(kv.Key, kv.Value);

			string latestSdk = Path.Combine(sdk.RootPath, "sdk", sdk.HighestSdkVersion);
			latestSdkPath = latestSdk;

			Environment.SetEnvironmentVariable("MSBuildToolsPath", latestSdk);
			Environment.SetEnvironmentVariable("MSBuildToolsVersion", "Current");

			// Every SDK-style project unconditionally imports Microsoft.NET.Sdk.ImportWorkloads.props
			// (not just workload-based projects like MAUI - any plain Microsoft.NET.Sdk project pulls
			// it in), which resolves the "Microsoft.NET.SDK.WorkloadAutoImportPropsLocator" SDK via
			// the "Microsoft.NET.Sdk.WorkloadMSBuildSdkResolver" resolver. That resolver isn't
			// discoverable from this embedded engine's own SdkResolvers folder at all (no manifest
			// ships it, unlike the NuGet resolver's own), and even once manually deployed alongside
			// its full dependency closure, it still crashed trying to parse this process's
			// $(NetCoreTargetingPackRoot)-derived SDK version string as a workload release version -
			// so opening ANY project in this engine threw either "SDK ... could not be found" or a
			// deeper SDK Resolver Failure, leaving MSBuildBasedProject.GetEvaluatedProperty()
			// (OutputAssemblyFullPath, AssemblyName, etc.) unusable for every project, which starved
			// unit test discovery/build and made RoslynParser.Parse() return null for every .cs file.
			// This embedded engine has no use for workload-based SDKs (MAUI/Android/iOS workloads
			// aren't installed or relevant here) - MSBuildEnableWorkloadResolver=false is MSBuild's
			// own documented escape hatch to skip the whole workload-resolution props/resolver chain
			// instead of trying to make it succeed.
			Environment.SetEnvironmentVariable("MSBuildEnableWorkloadResolver", "false");

			LoggingService.InfoFormatted("MSBuild environment initialized: DOTNET_ROOT={0}, MSBuildSDKsPath={1}, MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET={2}",
				sdk.RootPath,
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

		static string latestSdkPath;
		
		internal static string GetLatestSdkPath()
		{
			return latestSdkPath;
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
