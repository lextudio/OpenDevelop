using System.Globalization;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost;

/// <summary>Launch rules for the upstream Surface process that are not part of the JSON wire.</summary>
public static class SurfaceLaunch
{
	/// <summary>
	/// The longest root directory (cache, staging or run copy) this client will start a surface from.
	/// A surface started from a deep path dies in the Windows App SDK bootstrap with
	/// "filename too long (0x800700CE)"; the payload has relative paths of ~50 characters below the root.
	/// </summary>
	public const int MaxRootLength = 120;

	/// <summary>Throws when <paramref name="directory"/> is too long to host a surface.</summary>
	public static void EnsureShortRoot(string directory)
	{
		string full = Path.GetFullPath(directory);
		if (full.Length > MaxRootLength)
			throw new PathTooLongException(
				$"'{full}' is {full.Length} characters; a surface must run from a root of at most {MaxRootLength} " +
				"(the Windows App SDK bootstrap fails with 'filename too long' otherwise). Use a short root such as C:\\odsurf.");
	}

	/// <summary>
	/// Parses the stdout announcement <c>SURFACE_PORT=&lt;n&gt;</c>. Returns null for any other line.
	/// </summary>
	public static int? TryParsePort(string? line)
	{
		const string prefix = "SURFACE_PORT=";
		if (line == null || !line.StartsWith(prefix, StringComparison.Ordinal))
			return null;
		return int.TryParse(line.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
			&& port is > 0 and <= 65535 ? port : null;
	}

	/// <summary>
	/// The command line for a version-matched host. The cache entry itself is read-only and must not be launched:
	/// pass a per-process copy of it (see <see cref="PrepareRunCopy"/>) and its own merged PRI.
	/// </summary>
	public static string MatchedHostArguments(string runDirectory)
		=> "--user-pri \"" + Path.Combine(runDirectory, "Surface.designtime.pri") + "\"";

	/// <summary>Copies a cache entry's host folder to a fresh per-process directory under <paramref name="runRoot"/>.</summary>
	public static string PrepareRunCopy(string hostDirectory, string runRoot)
	{
		EnsureShortRoot(runRoot);
		string run = Path.Combine(runRoot, "run-" + Guid.NewGuid().ToString("N")[..8]);
		EnsureShortRoot(run);
		CopyDirectory(hostDirectory, run);
		return run;
	}

	static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (string file in Directory.EnumerateFiles(source))
			File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
		foreach (string dir in Directory.EnumerateDirectories(source))
			CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
	}
}
