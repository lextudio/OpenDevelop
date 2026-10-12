using System;
using System.Diagnostics;
using System.IO;

using ICSharpCode.SharpDevelop.Services;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The Solution Explorer status overlay must never write the repository index.
///
/// A plain `git status` refreshes stale index stat data and takes .git/index.lock to do it. The
/// overlay kills a slow `git status`, and the integration fixture kills the whole IDE process tree,
/// so a killed refresh left a zero-byte index.lock behind and every later git command in the user's
/// repository failed with "Another git process seems to be running". With optional locks disabled
/// the status query is read-only: it neither takes the lock nor rewrites the index.
/// </summary>
public sealed class GitStatusServiceLockTests : IDisposable
{
	readonly string repo = Path.Combine(Path.GetTempPath(), "GitStatusLock-" + Guid.NewGuid().ToString("N"));

	public GitStatusServiceLockTests()
	{
		Directory.CreateDirectory(repo);
		Git("init", "-q");
		Git("config", "user.email", "test@example.invalid");
		Git("config", "user.name", "Test");
		File.WriteAllText(Path.Combine(repo, "tracked.txt"), "content\n");
		Git("add", "tracked.txt");
		Git("commit", "-q", "-m", "initial");
	}

	public void Dispose()
	{
		GitStatusService.ClearCache();
		try { Directory.Delete(repo, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	[Fact]
	public void Refresh_DoesNotRewriteTheIndex()
	{
		var tracked = Path.Combine(repo, "tracked.txt");
		var index = Path.Combine(repo, ".git", "index");
		// Same content, new timestamp: the index's cached stat data is now stale, which is exactly
		// the case in which an ordinary `git status` rewrites the index under index.lock.
		File.SetLastWriteTimeUtc(tracked, DateTime.UtcNow.AddMinutes(5));
		var before = File.ReadAllBytes(index);

		GitStatusService.ClearCache();
		GitStatusService.Refresh(tracked);

		Assert.Equal(GitFileStatus.None, GitStatusService.GetStatus(tracked));
		Assert.Equal(before, File.ReadAllBytes(index));
		Assert.False(File.Exists(Path.Combine(repo, ".git", "index.lock")));
	}

	void Git(params string[] arguments)
	{
		var psi = new ProcessStartInfo("git") {
			WorkingDirectory = repo,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in arguments)
			psi.ArgumentList.Add(argument);
		using var process = Process.Start(psi)!;
		var error = process.StandardError.ReadToEnd();
		process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		Assert.True(process.ExitCode == 0, "git " + string.Join(" ", arguments) + " failed: " + error);
	}
}
