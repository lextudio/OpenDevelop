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
using System.Runtime.InteropServices;
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
		
		/// <summary>
		/// Set once <see cref="InitializeMSBuildEnvironment"/> has run and found no .NET SDK whose
		/// host architecture matches this process's own (<see cref="RuntimeInformation.ProcessArchitecture"/>).
		/// Every SDK-style project load will fail in that state (there is no in-process-loadable
		/// Microsoft.Build.NuGetSdkResolver.dll at all) - callers that open solutions/projects
		/// should check this once and surface it to the user instead of letting it manifest only as
		/// a buried "SDK resolver assembly ... could not be loaded" build-channel message.
		/// </summary>
		public static bool NoCompatibleInProcessSdkFound { get; private set; }

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
			//
			// Use the architecture-restricted resolver, not ResolveEffectiveSdk(): everything below
			// (MSBuildToolsPath, and especially the SdkResolvers copy further down) gets loaded
			// directly into THIS process, so an SDK whose own host is a different architecture is
			// worse than useless here - Microsoft.Build.NuGetSdkResolver.dll is a ReadyToRun image
			// built for that SDK's own architecture, and loading a mismatched one crashes every
			// SDK-style project load ("Format of the executable (.exe) or library (.dll) is
			// invalid") instead of merely failing to resolve. This was exactly how dist.ps1's
			// cross-published OpenDevelop-win-x64 ended up shipping an ARM64 resolver DLL when
			// built on this ARM64 dev machine - see the SharpDevelop.csproj comment on the (now
			// removed) DeployNuGetSdkResolver*/ targets this replaces.
			var sdk = DotNetSdkService.ResolveEffectiveSdkForInProcessHosting();
			if (sdk?.RootPath == null || sdk.HighestSdkVersion == null) {
				NoCompatibleInProcessSdkFound = true;
				LoggingService.Error(
					$"No installed .NET SDK matches this process's architecture ({RuntimeInformation.ProcessArchitecture}). " +
					"SDK-style project loading (open/evaluate) will fail for every project until a matching-architecture " +
					".NET SDK 10 (or newer) is installed - see the setup instructions.");
				return;
			}

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

			// MSBuild's SdkResolverLoader looks for "SdkResolvers\<name>\<name>.dll" next to whichever
			// Microsoft.Build.dll is actually loaded in this process (this app's own bin/publish
			// directory for the embedded engine) - not relative to $(MSBuildToolsPath)/DOTNET_ROOT -
			// so the resolver DLL and its dependency closure still need to land there. Deploying
			// this at runtime, from the architecture-matched `sdk` resolved above, replaces the old
			// SharpDevelop.csproj build-time DeployNuGetSdkResolver*/ targets: those copied whatever
			// $(MSBuildToolsPath) happened to be on the machine that BUILT/PUBLISHED OpenDevelop,
			// which is only correct when that machine's own architecture happens to match the
			// published RuntimeIdentifier - copying here instead, from the SDK this process itself
			// resolved as architecture-compatible, is correct on every machine unconditionally and
			// needs no packaging-time knowledge of the eventual RuntimeIdentifier at all.
			//
			// The NuGet.*.dll set is deliberately NOT overwritten: those are also the assemblies
			// this app's own NuGet integration (NuGetPackageSearchService and friends) compiled
			// against, at the version Directory.Packages.props pins - while the SDK ships its own,
			// often newer, set. Overwriting them mixes two versions of one strongly-named closure
			// (an SDK NuGet.Common next to the app's NuGet.Commands) and the process then dies at
			// startup with "Could not load file or assembly 'NuGet.Common, Version=...'". Only fill
			// in what is genuinely missing, exactly as before.
			//
			// The resolver DLL itself is the one file that must track THIS process's architecture
			// rather than whatever a previous run or a packaging step left behind, so it is
			// replaced whenever the deployed copy is not loadable here - that is the actual bug
			// this whole code path exists to fix.
			string binDir = Path.GetDirectoryName(typeof(MSBuildInternals).Assembly.Location);
			if (!string.IsNullOrEmpty(binDir)) {
				foreach (string dependency in new[] {
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
				PointNuGetSdkResolverManifestAtSdk(latestSdk, binDir);
			}
		}

		/// <summary>
		/// Points this app's own NuGet SDK-resolver manifest at the resolver inside
		/// <paramref name="latestSdk"/>, instead of copying that resolver (and its NuGet.*
		/// dependency closure) in beside this assembly.
		///
		/// An SdkResolver manifest's &lt;Path&gt; may be absolute - MSBuild's SdkResolverLoader only
		/// rebases it against the manifest's own folder when it is relative - so the resolver can
		/// simply be loaded where it already lives. That matters for two reasons this code learned
		/// the hard way:
		/// - Microsoft.Build.NuGetSdkResolver.dll is a ReadyToRun image tied to its SDK's
		///   architecture. A copy taken at packaging time is only right when the building machine's
		///   architecture happened to match the published RuntimeIdentifier; cross-publishing
		///   win-x64 from an ARM64 machine baked in an ARM64 resolver, and every SDK-style project
		///   then failed to load ("Format of the executable (.exe) or library (.dll) is invalid").
		/// - The resolver's NuGet.* dependencies resolve from the resolver's own directory. Copying
		///   those in here instead would put the SDK's NuGet version next to the (different,
		///   Directory.Packages.props-pinned) one this app's own NuGet integration compiled
		///   against, and mixing one strongly-named closure like that kills the process at startup
		///   with "Could not load file or assembly 'NuGet.Common, Version=...'".
		///
		/// Loading it in place sidesteps both: the resolver is always the one belonging to the SDK
		/// this process already established is architecture-compatible, with its own matching
		/// dependencies beside it, and nothing about the app's own payload changes.
		/// </summary>
		static void PointNuGetSdkResolverManifestAtSdk(string latestSdk, string binDir)
		{
			const string resolverName = "Microsoft.Build.NuGetSdkResolver";
			string resolverAssembly = Path.Combine(latestSdk, resolverName + ".dll");
			if (!File.Exists(resolverAssembly)) {
				LoggingService.Warn($"{resolverName}.dll not found in {latestSdk}; NuGet-based MSBuild Sdks will not resolve.");
				return;
			}

			string manifestPath = Path.Combine(binDir, "SdkResolvers", resolverName, resolverName + ".xml");
			string manifest = "<SdkResolver>" + Environment.NewLine
				+ "  <Path>" + System.Security.SecurityElement.Escape(resolverAssembly) + "</Path>" + Environment.NewLine
				+ "</SdkResolver>" + Environment.NewLine;

			try {
				// Rewrite only on change: the common case is an unchanged SDK selection, and this
				// runs on the startup path.
				if (File.Exists(manifestPath) && File.ReadAllText(manifestPath) == manifest)
					return;
				Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
				File.WriteAllText(manifestPath, manifest);
				LoggingService.Info($"Pointed {resolverName} manifest at {resolverAssembly} ({RuntimeInformation.ProcessArchitecture}).");
			} catch (IOException ex) {
				LoggingService.Warn($"Could not update the {resolverName} manifest at {manifestPath}.", ex);
			} catch (UnauthorizedAccessException ex) {
				LoggingService.Warn($"Could not update the {resolverName} manifest at {manifestPath}.", ex);
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
				string toolsVersion = ResolveSupportedToolsVersion(projectCollection, rootElement);
#if HAS_UNO
				// SDK resolvers (e.g. NuGet → Uno.Sdk) may not be available in-process.
				// IgnoreMissingImports lets us read static XML properties (AssemblyName,
				// RootNamespace, items) without requiring full SDK resolution.
				return new MSBuild.Evaluation.Project(rootElement, globalProps, toolsVersion, null,
					projectCollection, MSBuild.Evaluation.ProjectLoadSettings.IgnoreMissingImports);
#else
				return new MSBuild.Evaluation.Project(rootElement, globalProps, toolsVersion, projectCollection);
#endif
			}
		}
		
		internal static ProjectInstance LoadProjectInstance(MSBuild.Evaluation.ProjectCollection projectCollection, ProjectRootElement rootElement, IDictionary<string, string> globalProps)
		{
			InitializeMSBuildEnvironment();
			lock (SolutionProjectCollectionLock) {
				string toolsVersion = ResolveSupportedToolsVersion(projectCollection, rootElement);
				return new ProjectInstance(rootElement, globalProps, toolsVersion, projectCollection);
			}
		}

		/// <summary>
		/// Evaluates legacy project XML with the installed MSBuild toolset. Modern dotnet MSBuild
		/// intentionally exposes only <c>Current</c>; passing an old XML attribute such as 4.0 or
		/// 12.0 explicitly therefore fails before project evaluation, even though the same project
		/// can normally be built by current MSBuild. Preserve the attribute on disk, but don't let
		/// it select a nonexistent toolset at runtime.
		/// </summary>
		static string ResolveSupportedToolsVersion(MSBuild.Evaluation.ProjectCollection projectCollection, ProjectRootElement rootElement)
		{
			string requested = rootElement.ToolsVersion;
			if (string.IsNullOrEmpty(requested))
				return projectCollection.DefaultToolsVersion;
			if (projectCollection.Toolsets.Any(toolset => string.Equals(toolset.ToolsVersion, requested, StringComparison.OrdinalIgnoreCase)))
				return requested;
			LoggingService.InfoFormatted("MSBuild ToolsVersion '{0}' from '{1}' is unavailable; evaluating with Current.",
				requested, rootElement.FullPath);
			return "Current";
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
