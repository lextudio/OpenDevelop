using System;
using System.IO;
using System.Linq;

using ICSharpCode.SharpDevelop;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>The open-time decision and the revoke rule of Workspace Trust (WorkspaceTrustPrompt).</summary>
public sealed class WorkspaceTrustPromptTests : IDisposable
{
	readonly string temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WorkspaceTrustPrompt-" + Guid.NewGuid().ToString("N")));
	readonly string store;

	public WorkspaceTrustPromptTests()
	{
		Directory.CreateDirectory(temp);
		store = Path.Combine(temp, "config", "workspace-trust.json");
		SD.InitializeForUnitTests();
	}

	public void Dispose()
	{
		SD.TearDownForUnitTests();
		try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	WorkspaceTrustService Create(bool enabled = true) => new WorkspaceTrustService(store, enabled);

	string Solution(string name)
	{
		string root = Path.Combine(temp, name);
		Directory.CreateDirectory(Path.Combine(root, ".git"));
		Directory.CreateDirectory(Path.Combine(root, "src"));
		string file = Path.Combine(root, "src", "App.sln");
		File.WriteAllText(file, "");
		return file;
	}

	sealed class Dialog
	{
		readonly int answer;
		public int Calls;
		public string[] Buttons = Array.Empty<string>();
		public int Accept = -2, Cancel = -2;
		public Dialog(int answer) => this.answer = answer;

		public int Show(string caption, string text, int accept, int cancel, string[] buttons)
		{
			Calls++;
			Buttons = buttons;
			Accept = accept;
			Cancel = cancel;
			return answer;
		}
	}

	[Fact]
	public void Establish_RestrictedAnswer_SetsTheWorkspaceAndTrustsNothing()
	{
		string file = Solution("repo");
		var service = Create();
		var dialog = new Dialog(answer: 2); // Restricted Mode is the last button

		WorkspaceTrustPrompt.Establish(service, file, dialog.Show);

		Assert.Equal(1, dialog.Calls);
		Assert.Equal(3, dialog.Buttons.Length);
		Assert.Equal(2, dialog.Accept);
		Assert.Equal(2, dialog.Cancel);
		Assert.Equal(TrustState.Restricted, service.CurrentState);
		Assert.Equal(Path.Combine(temp, "repo"), service.CurrentWorkspaceRoot);
		Assert.Empty(service.TrustedRoots);
		Assert.False(File.Exists(store), "a Restricted decision must not be persisted");
	}

	[Fact]
	public void Establish_ClosedDialog_IsRestricted()
	{
		var service = Create();
		WorkspaceTrustPrompt.Establish(service, Solution("repo"), new Dialog(answer: -1).Show);
		Assert.Equal(TrustState.Restricted, service.CurrentState);
		Assert.Empty(service.TrustedRoots);
	}

	[Fact]
	public void Establish_TrustFolder_TrustsTheGitRootAndStoresIt()
	{
		string file = Solution("repo");
		var service = Create();

		WorkspaceTrustPrompt.Establish(service, file, new Dialog(answer: 0).Show);

		Assert.Equal(TrustState.Trusted, service.CurrentState);
		Assert.Equal(new[] { Path.Combine(temp, "repo") }, service.TrustedRoots);
		Assert.Equal(TrustState.Trusted, Create().GetState(Path.Combine(temp, "repo", "src")));
	}

	[Fact]
	public void Establish_TrustParent_CoversSiblings()
	{
		string file = Solution("repo");
		string sibling = Solution("other");
		var service = Create();

		WorkspaceTrustPrompt.Establish(service, file, new Dialog(answer: 1).Show);

		Assert.Equal(new[] { temp }, service.TrustedRoots);
		Assert.Equal(TrustState.Trusted, service.GetState(Path.GetDirectoryName(sibling)!));
	}

	[Fact]
	public void Establish_AlreadyTrusted_AsksNothing()
	{
		string file = Solution("repo");
		var service = Create();
		service.Trust(temp);
		var dialog = new Dialog(answer: 2);

		WorkspaceTrustPrompt.Establish(service, file, dialog.Show);

		Assert.Equal(0, dialog.Calls);
		Assert.Equal(TrustState.Trusted, service.CurrentState);
	}

	[Fact]
	public void Establish_WithTrustDisabled_AsksNothingAndAllowsEverything()
	{
		var service = Create(enabled: false);
		var dialog = new Dialog(answer: 2);

		WorkspaceTrustPrompt.Establish(service, Solution("repo"), dialog.Show);

		Assert.Equal(0, dialog.Calls);
		Assert.True(service.IsAllowed(TrustCapability.ExecuteBuild));
	}

	[Fact]
	public void Ask_ForARootThatIsTooBroad_OffersOnlyRestrictedMode_AndNeverThrows()
	{
		string rootFile = Path.Combine(Path.GetPathRoot(temp)!, "App.sln");
		var service = Create();
		Assert.False(service.CanTrustRoot(service.GetTrustRoot(rootFile)));
		var dialog = new Dialog(answer: 0); // a stray "first button" answer must not trust the filesystem root

		WorkspaceTrustPrompt.Establish(service, rootFile, dialog.Show);

		Assert.Single(dialog.Buttons);
		Assert.Equal(TrustState.Restricted, service.CurrentState);
		Assert.Empty(service.TrustedRoots);
	}

	[Fact]
	public void Revoke_ReportsWhetherTheOpenWorkspaceBecameRestricted()
	{
		string file = Solution("repo");
		string other = Path.Combine(temp, "elsewhere");
		Directory.CreateDirectory(other);
		var service = Create();
		service.Trust(temp);
		service.Trust(other);
		service.SetCurrentWorkspace(service.GetTrustRoot(file));

		// An unrelated root does not touch the open workspace.
		Assert.False(WorkspaceTrustPrompt.Revoke(service, new[] { other }));
		Assert.Equal(TrustState.Trusted, service.CurrentState);

		// Revoking the parent that covers it does, even though the workspace itself was never stored.
		Assert.True(WorkspaceTrustPrompt.Revoke(service, new[] { temp }));
		Assert.Equal(TrustState.Restricted, service.CurrentState);
		Assert.Empty(service.TrustedRoots);

		// Nothing left to revoke, and it was already Restricted.
		Assert.False(WorkspaceTrustPrompt.Revoke(service, new[] { temp }));
	}

	[Fact]
	public void Revoke_WithoutAWorkspace_ReportsNothing()
	{
		var service = Create();
		service.Trust(temp);
		Assert.False(WorkspaceTrustPrompt.Revoke(service, service.TrustedRoots.ToList()));
		Assert.Empty(service.TrustedRoots);
	}
}
