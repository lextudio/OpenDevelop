using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Services;

/// <summary>
/// Git working-tree status for a node's path, used to render the same modified/added/untracked
/// overlay VS/VS Code show in their own solution/project trees. Set only by hosts with a git
/// status provider wired up; stays <see cref="None"/> and renders no overlay for hosts that don't.
/// </summary>
public enum GitFileStatus
{
    None,
    Modified,
    Added,
    Untracked,
    Deleted,
    Renamed,
    Conflicted,
    Ignored
}

public readonly record struct GitStatusPresentation(
    string Key,
    string ColorHex,
    string Glyph,
    bool HasOverlay);

public static class GitStatusPresentationService
{
    public static GitStatusPresentation GetPresentation(GitFileStatus status)
    {
        return status switch
        {
            GitFileStatus.Added => new("Added", "#289A3E", "+", true),
            GitFileStatus.Deleted => new("Deleted", "#D32F2F", "-", true),
            GitFileStatus.Modified => new("Modified", "#F5A01E", "!", true),
            GitFileStatus.Renamed => new("Renamed", "#1E88E5", ">", true),
            GitFileStatus.Untracked => new("Untracked", "#009688", "+", true),
            GitFileStatus.Conflicted => new("Conflicted", "#C62828", "!", true),
            _ => new("None", string.Empty, string.Empty, false)
        };
    }
}

/// <summary>
/// Real <c>git status --porcelain</c> against the actual working tree - not a VCS abstraction,
/// just enough to color/badge Project Browser nodes the way every other IDE's git integration
/// does. Refreshed once per tree rebuild (see callers), not per file access - "git status" walks
/// the whole working tree, so querying it once and caching the result for every node beats
/// re-running it per file.
///
/// Shared by both hosts (see doc/technotes/solution-explorer.md): this used to be UnoDevelop-only
/// (proper porcelain-v1 X/Y status parsing, cross-platform `git` discovery, Untracked/Ignored/
/// Renamed/Conflicted states) while OpenDevelop's GitAddIn had an older, narrower engine
/// (GitStatusCache - `git ls-files` + `status --porcelain --untracked-files=no`, only
/// Added/Modified/Deleted/OK/None). This implementation has no host-specific dependency
/// (ICSharpCode.Core.FileUtility is the only non-BCL type it touches), so it was the one worth
/// keeping, not GitStatusCache.
/// </summary>
public static class GitStatusService
{
    private static readonly Dictionary<string, Dictionary<string, GitFileStatus>> _statusesByRepoRoot =
        new(StringComparer.OrdinalIgnoreCase);

    private static string? _gitExecutable;
    private static bool _gitSearched;

    /// <summary>
    /// Re-runs `git status` for the repository containing <paramref name="anyPathUnderRepo"/> and
    /// caches the result. No-ops (clears any stale cache entry) if the path isn't under a git
    /// working copy or git isn't found - the tree then just shows no status, same as a plain
    /// non-git folder.
    /// </summary>
    public static void Refresh(string anyPathUnderRepo)
    {
        var repoRoot = FindRepositoryRoot(anyPathUnderRepo);
        if (repoRoot is null)
            return;
        repoRoot = FileUtility.NormalizePath(repoRoot);

        if (_statusesByRepoRoot.ContainsKey(repoRoot))
        {
            // Already refreshed in this tree-rebuild pass (multiple projects commonly share one
            // repo root) - avoid running `git status` on the same repo multiple times per rebuild.
            return;
        }

        var git = FindGit();
        var statuses = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
        if (git is not null)
        {
            try
            {
                var output = RunGit(git, repoRoot, "status --porcelain=v1 --untracked-files=all --ignored=no");
                ParsePorcelainStatus(output, repoRoot, statuses);
            }
            catch
            {
                // Best-effort: a failed/timed-out `git status` just means no decorations this pass.
            }
        }

        _statusesByRepoRoot[repoRoot] = statuses;
    }

    /// <summary>Clears every cached repo's status - call once at the start of a full tree rebuild.</summary>
    public static void ClearCache() => _statusesByRepoRoot.Clear();

    /// <summary>
    /// Clears the cached status for whichever repository contains <paramref name="anyPathUnderRepo"/>
    /// (e.g. after a single file is added/removed/renamed outside a full tree rebuild) - the next
    /// <see cref="GetStatus"/>/<see cref="Refresh"/> call for that repo re-runs `git status`.
    /// </summary>
    public static void ClearCachedStatus(string anyPathUnderRepo)
    {
        var repoRoot = FindRepositoryRoot(anyPathUnderRepo);
        if (repoRoot is not null)
        {
            repoRoot = FileUtility.NormalizePath(repoRoot);
            _statusesByRepoRoot.Remove(repoRoot);
        }
    }

    public static GitFileStatus GetStatus(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return GitFileStatus.None;

        // ICSharpCode.Core's FileName/DirectoryName routinely render an absolute Unix path with a
        // leading "//" (treated as a UNC-style prefix) - Path.GetFullPath does NOT collapse that
        // to a single separator, so a node's FullPath ("//Users/...") would never match a plain
        // single-slash key ("/Users/...") via naive string comparison. Normalize through the same
        // FileUtility.NormalizePath this codebase already uses everywhere else for exactly this.
        var normalized = FileUtility.NormalizePath(fullPath);
        var repoRoot = FindRepositoryRoot(normalized);

        if (repoRoot is not null)
        {
            repoRoot = FileUtility.NormalizePath(repoRoot);
            if (!_statusesByRepoRoot.ContainsKey(repoRoot))
            {
                Refresh(repoRoot);
            }

            if (_statusesByRepoRoot.TryGetValue(repoRoot, out var repoStatuses)
                && repoStatuses.TryGetValue(normalized, out var status))
            {
                return status;
            }
        }

        return GitFileStatus.None;
    }

    public static GitFileStatus GetStatusForTreeNode(string? fullPath, bool isDirectory)
    {
        var status = GetStatus(fullPath);
        if (status != GitFileStatus.None)
            return status;

        if (string.IsNullOrWhiteSpace(fullPath))
            return GitFileStatus.None;

        var statusRoot = ResolveTreeStatusRoot(fullPath, isDirectory);
        if (string.IsNullOrWhiteSpace(statusRoot))
            return GitFileStatus.None;

        statusRoot = FileUtility.NormalizePath(statusRoot);
        var repoRoot = FindRepositoryRoot(statusRoot);
        if (repoRoot is null)
            return GitFileStatus.None;

        repoRoot = FileUtility.NormalizePath(repoRoot);
        if (!_statusesByRepoRoot.ContainsKey(repoRoot))
        {
            Refresh(repoRoot);
        }

        if (!_statusesByRepoRoot.TryGetValue(repoRoot, out var repoStatuses))
            return GitFileStatus.None;

        var prefix = statusRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return repoStatuses
            .Where(pair => pair.Value != GitFileStatus.None
                && pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .DefaultIfEmpty(GitFileStatus.None)
            .OrderByDescending(GetStatusPriority)
            .First();
    }

    private static string? ResolveTreeStatusRoot(string fullPath, bool isDirectory)
    {
        if (isDirectory)
            return fullPath;

        var extension = Path.GetExtension(fullPath);
        if (extension is ".sln" or ".slnx" or ".csproj" or ".vbproj" or ".fsproj")
            return Path.GetDirectoryName(fullPath);

        return null;
    }

    private static int GetStatusPriority(GitFileStatus status)
    {
        return status switch
        {
            GitFileStatus.Conflicted => 60,
            GitFileStatus.Deleted => 50,
            GitFileStatus.Modified => 40,
            GitFileStatus.Renamed => 30,
            GitFileStatus.Added => 20,
            GitFileStatus.Untracked => 10,
            _ => 0
        };
    }

    private static void ParsePorcelainStatus(string output, string repoRoot, Dictionary<string, GitFileStatus> statuses)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            if (rawLine.Length < 4)
                continue;

            // Porcelain v1 format: "XY PATH" or "XY ORIG_PATH -> PATH" for renames.
            // X = index status, Y = worktree status; only Y (what you'd actually commit next if
            // you `git add`ed everything) is shown, matching what other IDEs badge by default.
            var indexStatus = rawLine[0];
            var worktreeStatus = rawLine[1];
            var pathPart = rawLine.Substring(3);

            var arrowIndex = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
            var relativePath = arrowIndex >= 0 ? pathPart[(arrowIndex + 4)..] : pathPart;
            relativePath = relativePath.Trim('"');

            var fullPath = FileUtility.NormalizePath(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            statuses[fullPath] = Classify(indexStatus, worktreeStatus);
        }
    }

    private static GitFileStatus Classify(char indexStatus, char worktreeStatus)
    {
        if (indexStatus == '?' && worktreeStatus == '?')
            return GitFileStatus.Untracked;
        if (indexStatus == '!' && worktreeStatus == '!')
            return GitFileStatus.Ignored;
        if (indexStatus == 'U' || worktreeStatus == 'U' || (indexStatus == 'A' && worktreeStatus == 'A') || (indexStatus == 'D' && worktreeStatus == 'D'))
            return GitFileStatus.Conflicted;
        if (indexStatus == 'R' || worktreeStatus == 'R')
            return GitFileStatus.Renamed;
        if (indexStatus == 'D' || worktreeStatus == 'D')
            return GitFileStatus.Deleted;
        if (indexStatus == 'A')
            return GitFileStatus.Added;
        if (indexStatus == 'M' || worktreeStatus == 'M')
            return GitFileStatus.Modified;
        return GitFileStatus.None;
    }

    public static bool IsFileInGitRepo(string fullPath)
    {
        return FindRepositoryRoot(fullPath) != null;
    }

    private static string? FindRepositoryRoot(string fileOrDirectory)
    {
        try
        {
            if (!Path.IsPathRooted(fileOrDirectory))
                return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        var current = Directory.Exists(fileOrDirectory) ? fileOrDirectory : Path.GetDirectoryName(fileOrDirectory);
        var info = current is null ? null : new DirectoryInfo(current);
        while (info is not null)
        {
            var gitEntry = Path.Combine(info.FullName, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                return info.FullName;
            info = info.Parent;
        }

        return null;
    }

    private static string? FindGit()
    {
        if (_gitSearched)
            return _gitExecutable;
        _gitSearched = true;

        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "git.exe", "git.cmd", "git.bat" }
            : new[] { "git" };

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var path in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                foreach (var candidate in candidates)
                {
                    var exe = Path.Combine(path, candidate);
                    if (File.Exists(exe))
                    {
                        _gitExecutable = exe;
                        return _gitExecutable;
                    }
                }
            }
            catch (ArgumentException)
            {
                // ignore invalid PATH entries
            }
        }

        return null;
    }

    private static string RunGit(string git, string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = git,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return output;
    }
}
