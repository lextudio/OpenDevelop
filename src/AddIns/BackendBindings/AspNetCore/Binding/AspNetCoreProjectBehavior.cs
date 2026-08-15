using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using ICSharpCode.AspNetCore;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.AspNetCore.AddIn
{
	/// <summary>Layers launchSettings-aware execution on the existing C# project binding.</summary>
	public sealed class AspNetCoreProjectBehavior : ProjectBehavior
	{
		public override bool IsStartable => true;

		public override void Start(bool withDebugging)
		{
			var profile = GetProfile();
			base.Start(withDebugging);
			if (profile.LaunchBrowser && profile.GetBrowserUrl() is { Length: > 0 } url)
				_ = OpenBrowserWhenReadyAsync(url);
		}

		public override ProcessStartInfo CreateStartInfo()
		{
			try
			{
				var profile = GetProfile();
				if (BlazorWebAssemblyDevServer.TryCreate(Project.FileName.ToString(), Project.OutputAssemblyFullPath.ToString(), profile, out var blazorStartInfo))
					return blazorStartInfo;
				return AspNetCoreLaunchCommand.Create(Project.FileName.ToString(), profile);
			}
			catch (ProjectStartException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new ProjectStartException("Unable to create the ASP.NET Core launch command: " + ex.Message);
			}
		}

		AspNetCoreLaunchProfile GetProfile()
		{
			var projectFile = Project.FileName.ToString();
			var projectDirectory = Path.GetDirectoryName(projectFile);
			var defaultNamespace = Project is MSBuildBasedProject msbuild
				? msbuild.GetEvaluatedProperty("RootNamespace") ?? msbuild.GetEvaluatedProperty("AssemblyName")
				: null;
			var provider = new AspNetCoreLaunchProfileProvider(projectDirectory, defaultNamespace ?? Path.GetFileNameWithoutExtension(projectFile));
			provider.LoadLaunchSettings();
			var preferredProfile = Project is MSBuildBasedProject p ? p.GetEvaluatedProperty("AspNetCoreLaunchProfile") : null;
			return provider.GetProfile(preferredProfile)
				?? throw new ProjectStartException("launchSettings.json contains no runnable Project or Executable profile.");
		}

		static async Task OpenBrowserWhenReadyAsync(string url)
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
			for (var attempt = 0; attempt < 300; attempt++)
			{
				await Task.Delay(100).ConfigureAwait(false);
				try
				{
					using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
					Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
					return;
				}
				catch (HttpRequestException) { }
				catch (TaskCanceledException) { }
			}
			LoggingService.Warn("ASP.NET Core started, but its launch URL did not become reachable: " + url);
		}
	}

	/// <summary>Recognizes SDK-style web projects without relying on legacy project-type GUIDs.</summary>
	public sealed class AspNetCoreProjectCondition : IConditionEvaluator
	{
		public bool IsValid(object owner, Condition condition)
		{
			var project = owner as IProject ?? ProjectService.CurrentProject;
			if (project?.FileName == null || !File.Exists(project.FileName.ToString())) return false;
			try
			{
				var root = XDocument.Load(project.FileName.ToString()).Root;
				if (root == null) return false;
				var sdk = ((string)root.Attribute("Sdk") ?? string.Empty) + ";" +
					string.Join(";", root.Elements().Where(e => e.Name.LocalName == "Sdk").Select(e => (string)e.Attribute("Name")));
				if (sdk.Split(';').Any(s => s.Trim().StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))) return true;
				return root.Descendants().Where(e => e.Name.LocalName == "PackageReference")
					.Select(e => (string)e.Attribute("Include") ?? (string)e.Attribute("Update"))
					.Any(p => p != null && p.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase));
			}
			catch
			{
				return false;
			}
		}
	}
}
