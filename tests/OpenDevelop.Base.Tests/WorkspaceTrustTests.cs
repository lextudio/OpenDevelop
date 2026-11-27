using System;
using System.Diagnostics;
using System.IO;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Services;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>Workspace Trust: doc/technotes/workspace-trust.md.</summary>
public sealed class WorkspaceTrustTests : IDisposable
{
	readonly string temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WorkspaceTrust-" + Guid.NewGuid().ToString("N")));
	readonly string store;

	public WorkspaceTrustTests()
	{
		Directory.CreateDirectory(temp);
		store = Path.Combine(temp, "config", "workspace-trust.json");
		SD.InitializeForUnitTests();
	}

	public void Dispose()
	{
		SD.TearDownForUnitTests();
		GitStatusService.ClearCache();
		try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	WorkspaceTrustService Create(bool enabled = true) => new WorkspaceTrustService(store, enabled);

	string Repo(string name, bool withGit = true)
	{
		string root = Path.Combine(temp, name);
		Directory.CreateDirectory(Path.Combine(root, "src", "App"));
		if (withGit)
			Directory.CreateDirectory(Path.Combine(root, ".git"));
		File.WriteAllText(Path.Combine(root, "src", "App", "App.sln"), "");
		return root;
	}

	[Fact]
	public void TrustRoot_IsTheNearestAncestorWithGit()
	{
		string root = Repo("repo");
		var service = Create();
		Assert.Equal(root, service.GetTrustRoot(Path.Combine(root, "src", "App", "App.sln")));
	}

	[Fact]
	public void TrustRoot_WithoutGit_IsTheSolutionFolder()
	{
		string root = Repo("plain", withGit: false);
		var service = Create();
		Assert.Equal(Path.Combine(root, "src", "App"), service.GetTrustRoot(Path.Combine(root, "src", "App", "App.sln")));
	}

	[Fact]
	public void UnknownFolder_IsRestricted_UntilTrusted()
	{
		string root = Repo("repo");
		var service = Create();
		Assert.Equal(TrustState.Restricted, service.GetState(root));
		service.Trust(root);
		Assert.Equal(TrustState.Trusted, service.GetState(root));
		Assert.Equal(TrustState.Trusted, service.GetState(Path.Combine(root, "src", "App")));
	}

	[Fact]
	public void TrustCoversOnlyTheFolderAndItsChildren_NotSiblingsWithTheSamePrefix()
	{
		string root = Repo("repo");
		string sibling = Repo("repo-evil");
		var service = Create();
		service.Trust(root);
		Assert.Equal(TrustState.Restricted, service.GetState(sibling));
		Assert.Equal(TrustState.Restricted, service.GetState(Path.GetDirectoryName(root)!));
	}

	[Fact]
	public void TrustingAParent_CoversItsChildren()
	{
		string root = Repo("repo");
		var service = Create();
		service.Trust(temp);
		Assert.Equal(TrustState.Trusted, service.GetState(root));
	}

	[Fact]
	public void Decisions_SurviveARestart()
	{
		string root = Repo("repo");
		Create().Trust(root);
		Assert.Equal(TrustState.Trusted, Create().GetState(root));
	}

	[Fact]
	public void Revoke_RemovesTheDecision()
	{
		string root = Repo("repo");
		var service = Create();
		service.Trust(root);
		service.Revoke(root);
		Assert.Equal(TrustState.Restricted, service.GetState(root));
		Assert.Equal(TrustState.Restricted, Create().GetState(root));
	}

	[Fact]
	public void ACorruptStore_TrustsNothing_AndIsNotOverwrittenByReading()
	{
		string root = Repo("repo");
		Directory.CreateDirectory(Path.GetDirectoryName(store)!);
		File.WriteAllText(store, "{ not json");
		var service = Create();
		Assert.Equal(TrustState.Restricted, service.GetState(root));
		Assert.Equal("{ not json", File.ReadAllText(store));
	}

	[Fact]
	public void FilesystemRootAndUserProfile_AreTooBroadToTrust()
	{
		var service = Create();
		Assert.False(service.CanTrustRoot(Path.GetPathRoot(temp)!));
		Assert.False(service.CanTrustRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
		Assert.Throws<ArgumentException>(() => service.Trust(Path.GetPathRoot(temp)!));
		Assert.True(service.CanTrustRoot(temp));
	}

	[Fact]
	public void IsAllowed_FollowsTheCurrentWorkspace()
	{
		string root = Repo("repo");
		var service = Create();
		Assert.True(service.IsAllowed(TrustCapability.ExecuteBuild), "no workspace open");

		service.SetCurrentWorkspace(root);
		foreach (var capability in new[] { TrustCapability.LoadAnalyzers, TrustCapability.ExecuteBuild, TrustCapability.RunProgram,
			TrustCapability.LoadProjectAssemblies, TrustCapability.LaunchTerminal, TrustCapability.SemanticAnalysis })
			Assert.False(service.IsAllowed(capability), capability.ToString());

		service.Trust(root);
		Assert.True(service.IsAllowed(TrustCapability.ExecuteBuild));
		service.SetCurrentWorkspace(null);
		Assert.True(service.IsAllowed(TrustCapability.RunProgram));
	}

	[Fact]
	public void DisabledTrust_AllowsEverything()
	{
		var service = Create(enabled: false);
		service.SetCurrentWorkspace(Repo("repo"));
		Assert.True(service.IsAllowed(TrustCapability.ExecuteBuild));
		Assert.Equal(TrustState.Trusted, service.GetState(temp));
	}

	[Fact]
	public void TrustChanged_FiresWhenTheCurrentStateCanChange()
	{
		string root = Repo("repo");
		var service = Create();
		int raised = 0;
		service.TrustChanged += (_, _) => raised++;
		service.SetCurrentWorkspace(root);
		service.Trust(root);
		service.Revoke(root);
		Assert.Equal(3, raised);
	}

	[Fact]
	public void TheStaticGate_UsesTheRegisteredService_AndAllowsWhenNone()
	{
		Assert.True(WorkspaceTrust.IsAllowed(TrustCapability.ExecuteBuild));
		Assert.Equal("trusted", WorkspaceTrust.FingerprintToken());

		string root = Repo("repo");
		var service = Create();
		SD.Services.AddService(typeof(IWorkspaceTrustService), service);
		service.SetCurrentWorkspace(root);

		Assert.False(WorkspaceTrust.IsAllowed(TrustCapability.RunProgram));
		Assert.Equal("restricted", WorkspaceTrust.FingerprintToken());
		var ex = Assert.Throws<WorkspaceNotTrustedException>(() => WorkspaceTrust.ThrowIfDenied(TrustCapability.ExecuteBuild));
		Assert.Equal(TrustCapability.ExecuteBuild, ex.Capability);
		Assert.Contains("Restricted Mode", ex.Message);

		service.Trust(root);
		Assert.True(WorkspaceTrust.IsAllowed(TrustCapability.RunProgram));
		Assert.Equal("trusted", WorkspaceTrust.FingerprintToken());
	}

	[Fact]
	public void LanguageServiceRegistry_ResolvesNothingInRestrictedMode()
	{
		string root = Repo("repo");
		var service = Create();
		SD.Services.AddService(typeof(IWorkspaceTrustService), service);
		int resolved = 0;
		var registry = new LanguageServiceRegistry();
		using var registration = registry.RegisterExtension(".cs", _ => { resolved++; return NoOpLanguageService.Instance; });

		service.SetCurrentWorkspace(root);
		Assert.False(registry.TryGetService("a.cs", out _));
		Assert.False(registry.TryGetProtocol("a.cs", out _));
		Assert.Same(NoOpLanguageService.Instance, registry.GetService("a.cs"));
		Assert.Equal(0, resolved);

		service.Trust(root);
		Assert.True(registry.TryGetService("a.cs", out _));
		Assert.Equal(1, resolved);
	}

	[Fact]
	public void GitStatus_DoesNotRunTheRepositorysFsmonitorCommand()
	{
		string repo = Path.Combine(temp, "gitrepo");
		Directory.CreateDirectory(repo);
		Git(repo, "init", "-q");
		string marker = Path.Combine(temp, "fsmonitor-ran");
		string hook = Path.Combine(temp, "hook.sh");
		File.WriteAllText(hook, "#!/bin/sh\ntouch '" + marker + "'\n");
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		Git(repo, "config", "core.fsmonitor", hook);
		string file = Path.Combine(repo, "a.txt");
		File.WriteAllText(file, "x");

		GitStatusService.ClearCache();
		GitStatusService.Refresh(file);

		Assert.False(File.Exists(marker), "git status executed core.fsmonitor from the repository config");
	}

	static void Git(string workingDirectory, params string[] arguments)
	{
		var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var argument in arguments)
			psi.ArgumentList.Add(argument);
		using var process = Process.Start(psi)!;
		string error = process.StandardError.ReadToEnd();
		process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		Assert.True(process.ExitCode == 0, "git " + string.Join(" ", arguments) + " failed: " + error);
	}
}
