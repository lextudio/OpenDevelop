// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Resolves (or generates) the executable launcher project for a Stride game, so Run/Debug has
// something startable to point at - see doc/technotes/stride-game-studio.md, "launcher probe".
//
// A Stride game's own project (<Game>.Game) is a class library holding the scripts and the .sdpkg;
// it is NOT startable. The runnable entry point is a separate, nearly empty platform project whose
// entire body is "using var game = new Game(); game.Run();" - everything platform-specific about it
// lives in MSBuild metadata, not code. The template ships only a Windows one (net10.0-windows,
// WinExe), which cannot run on macOS/Linux, which is why "run the game" had nothing to start there.
//
// Measured: a plain net10.0 + RuntimeIdentifier launcher builds clean (full asset pipeline) and
// runs the FirstPersonShooter sample natively on macOS with ZERO engine changes - Stride's own
// Stride.Core.targets already derives StridePlatform (Windows/macOS/Linux) from the RID. So one
// cross-platform "<Game>.Desktop" launcher covers every host, and per-OS triplication is
// unnecessary.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.StrideGameStudio
{
	/// <summary>
	/// Finds, adds, or generates the startable launcher project for a Stride game solution.
	/// </summary>
	public static class StrideLauncherService
	{
		/// <summary>Directory-name suffix of the cross-platform launcher this service generates.</summary>
		public const string DesktopSuffix = ".Desktop";

		public sealed class LauncherResult
		{
			public bool Success { get; set; }
			public string Status { get; set; }
			public string GameProjectName { get; set; }
			public string LauncherProjectName { get; set; }
			public string LauncherProjectPath { get; set; }
			public bool Generated { get; set; }
			public bool AddedToSolution { get; set; }
			public bool SetAsStartupProject { get; set; }
			public string Error { get; set; }
		}

		/// <summary>
		/// Ensures <paramref name="solution"/> contains a launcher project that can actually run on
		/// this host, and makes it the startup project so the normal Run/Debug commands (and
		/// od.run-project) work. Generates the launcher when the host OS has none.
		/// </summary>
		public static LauncherResult EnsureLauncher(ISolution solution)
		{
			if (solution == null)
				return new LauncherResult { Success = false, Error = "No solution is open." };

			var gameProject = FindGameProject(solution);
			if (gameProject == null)
				return new LauncherResult { Success = false, Status = "not-a-stride-solution", Error = "No project in the solution owns a .sdpkg asset package." };

			var result = new LauncherResult { GameProjectName = gameProject.Name };
			var gameDirectory = Path.GetDirectoryName(gameProject.FileName.ToString());
			var parent = Path.GetDirectoryName(gameDirectory);
			if (string.IsNullOrEmpty(parent)) {
				result.Error = "The game project has no parent directory to host a launcher.";
				return result;
			}

			// A launcher already in the solution and runnable here wins - never generate over the
			// user's own entry project.
			var existing = solution.Projects.FirstOrDefault(p => IsUsableLauncherHere(p.FileName.ToString()));
			if (existing != null) {
				result.Success = true;
				result.Status = "already-present";
				result.LauncherProjectName = existing.Name;
				result.LauncherProjectPath = existing.FileName.ToString();
				result.SetAsStartupProject = SetStartupProject(solution, existing);
				return result;
			}

			var baseName = StripGameSuffix(Path.GetFileNameWithoutExtension(gameProject.FileName.ToString()));

			// Next: one sitting on disk but outside the solution (the common case - a solution
			// opened straight from the .Game csproj contains that project only).
			var onDisk = FindLauncherOnDisk(parent);
			if (onDisk == null) {
				try {
					onDisk = GenerateDesktopLauncher(parent, baseName, gameProject);
					result.Generated = true;
				} catch (Exception ex) {
					result.Error = "Could not generate a launcher project: " + ex.Message;
					return result;
				}
			}

			IProject launcher;
			try {
				launcher = solution.AddExistingProject(FileName.Create(onDisk));
				solution.Save();
			} catch (Exception ex) {
				result.LauncherProjectPath = onDisk;
				result.Error = "Could not add the launcher project to the solution: " + ex.Message;
				return result;
			}

			result.Success = true;
			result.Status = result.Generated ? "generated" : "added-existing";
			result.AddedToSolution = true;
			result.LauncherProjectName = launcher.Name;
			result.LauncherProjectPath = onDisk;
			result.SetAsStartupProject = SetStartupProject(solution, launcher);
			return result;
		}

		/// <summary>The solution's Stride game project: the one whose directory holds a .sdpkg.</summary>
		public static IProject FindGameProject(ISolution solution)
		{
			return solution.Projects.FirstOrDefault(p => {
				var directory = Path.GetDirectoryName(p.FileName.ToString());
				return !string.IsNullOrEmpty(directory)
					&& Directory.Exists(directory)
					&& Directory.EnumerateFiles(directory, "*.sdpkg", SearchOption.TopDirectoryOnly).Any();
			});
		}

		static bool SetStartupProject(ISolution solution, IProject launcher)
		{
			if (solution.StartupProject == launcher)
				return true;
			try {
				solution.StartupProject = launcher;
				solution.SavePreferences();
				return true;
			} catch (Exception ex) {
				LoggingService.Warn("Stride: could not set the launcher as startup project: " + ex.Message);
				return false;
			}
		}

		static string StripGameSuffix(string projectName)
		{
			return projectName.EndsWith(".Game", StringComparison.OrdinalIgnoreCase)
				? projectName.Substring(0, projectName.Length - ".Game".Length)
				: projectName;
		}

		/// <summary>
		/// Platform suffixes whose launcher can actually be started on this host. A ".Windows"
		/// launcher targets net10.0-windows and cannot run on macOS/Linux at all, so it must not be
		/// picked there - that mis-pick is what made "run the game" look implemented but fail.
		/// </summary>
		static IReadOnlyList<string> HostLauncherSuffixes()
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				return new[] { DesktopSuffix, ".Windows" };
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				return new[] { DesktopSuffix, ".macOS" };
			return new[] { DesktopSuffix, ".Linux" };
		}

		static bool IsUsableLauncherHere(string projectFilePath)
		{
			var name = Path.GetFileNameWithoutExtension(projectFilePath);
			return HostLauncherSuffixes().Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
		}

		static string FindLauncherOnDisk(string parentDirectory)
		{
			foreach (var suffix in HostLauncherSuffixes()) {
				foreach (var directory in Directory.EnumerateDirectories(parentDirectory, "*" + suffix)) {
					var csproj = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
					if (csproj != null)
						return csproj;
				}
			}
			return null;
		}

		/// <summary>
		/// Writes a "&lt;base&gt;.Desktop" launcher next to the game project. The shape here is the
		/// one measured to build and run on macOS (technote, launcher probe): no "-windows" TFM,
		/// the SDK's own host RID (which is what makes Stride pick the matching StridePlatform),
		/// framework-dependent so the apphost is the single extensionless binary the IDE's
		/// CompilableProject.GetExtension expects, and BOTH Append*ToOutputPath disabled - with
		/// either left on, the RID/TFM get appended and OutputAssemblyFullPath resolves to a path
		/// that does not exist, so the run command fails before launching anything.
		/// </summary>
		static string GenerateDesktopLauncher(string parentDirectory, string baseName, IProject gameProject)
		{
			var projectName = baseName + DesktopSuffix;
			var directory = Path.Combine(parentDirectory, projectName);
			Directory.CreateDirectory(directory);

			var gameProjectPath = gameProject.FileName.ToString();
			var relativeGameProject = MakeRelative(directory, gameProjectPath);
			var packagePath = Directory.EnumerateFiles(
				Path.GetDirectoryName(gameProjectPath), "*.sdpkg", SearchOption.TopDirectoryOnly).First();
			var relativePackage = MakeRelative(directory, packagePath);

			var csprojPath = Path.Combine(directory, projectName + ".csproj");
			File.WriteAllText(csprojPath,
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <!-- Generated by OpenDevelop's Stride integration: the startable entry point for the game
       project next door, which is a class library and cannot be run directly. -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <RootNamespace>" + baseName + @"</RootNamespace>
    <!-- Stride.Core.targets derives StridePlatform (Windows/macOS/Linux) from this RID, which is
         what makes one launcher work on every host. -->
    <RuntimeIdentifier>$(NETCoreSdkRuntimeIdentifier)</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <OutputPath>..\Bin\Desktop\$(Configuration)\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
    <DefineConstants>STRIDE_PLATFORM_DESKTOP</DefineConstants>
  </PropertyGroup>
  <PropertyGroup>
    <StrideCurrentPackagePath>$(MSBuildThisFileDirectory)" + relativePackage + @"</StrideCurrentPackagePath>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""" + relativeGameProject + @""" />
  </ItemGroup>
</Project>
");

			File.WriteAllText(Path.Combine(directory, projectName.Replace('.', '_') + "App.cs"),
@"// Generated by OpenDevelop's Stride integration. The engine reads the compiled asset bundle
// produced by the referenced game project, so this is very nearly the whole launcher.
using Stride.Engine;

using var game = new Game();

// Required on macOS/MoltenVK, harmless elsewhere. Without it the Vulkan presenter overwrites the
// preferred backbuffer size with the CAMetalLayer's reported drawable extent on every swapchain
// (re)creation; on a Retina display that extent is already scaled, so it feeds back and doubles
// each time - 2560x1440 -> 5120x1956 -> 10240x3912 - until a texture descriptor exceeds Metal's
// 16384 limit and the game aborts a few seconds in. This switch (public API, present in the
// shipped Stride packages) keeps our requested size authoritative instead. Measured: with it, the
// swapchain locks at the requested size and the game runs until the window is closed.
game.GraphicsDeviceManager.SkipBackBufferClampToWindow = true;

game.Run();
");

			LoggingService.Info("Stride: generated launcher project " + csprojPath);
			return csprojPath;
		}

		static string MakeRelative(string fromDirectory, string toPath)
		{
			var relative = Path.GetRelativePath(fromDirectory, toPath);
			return relative.Replace('/', '\\');
		}
	}
}
