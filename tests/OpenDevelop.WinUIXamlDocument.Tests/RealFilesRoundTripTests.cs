using Xunit;

namespace OpenDevelop.WinUIXamlDocument.Tests;

/// <summary>
/// The same measurement on real files: every .xaml under <c>tests/fixtures</c>. The expected category of each file is checked in
/// (<c>real-files-baseline.txt</c>) and the test fails in both directions, so a file that starts round-tripping, or one that gets worse,
/// has to be noticed and the baseline updated on purpose: <c>OD_UPDATE_BASELINE=1</c> rewrites it.
/// </summary>
public class RealFilesRoundTripTests
{
	static string RepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory != null && !File.Exists(Path.Combine(directory.FullName, "OpenDevelop.Mvp.slnx")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new InvalidOperationException("OpenDevelop.Mvp.slnx was not found above " + AppContext.BaseDirectory);
	}

	static IEnumerable<string> Files(string root)
		=> Directory.EnumerateFiles(Path.Combine(root, "tests", "fixtures"), "*.xaml", SearchOption.AllDirectories)
			.Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
				&& !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
			.OrderBy(path => path, StringComparer.Ordinal);

	[Fact]
	public void Every_fixture_file_round_trips_as_the_checked_in_baseline_says()
	{
		string root = RepositoryRoot();
		var current = Files(root)
			.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/') + "\t" + RoundTrip.Classify(File.ReadAllText(path)))
			.ToList();
		Assert.NotEmpty(current);

		string baselineInSource = Path.Combine(root, "tests", "OpenDevelop.WinUIXamlDocument.Tests", "real-files-baseline.txt");
		if (Environment.GetEnvironmentVariable("OD_UPDATE_BASELINE") == "1") {
			File.WriteAllLines(baselineInSource, current);
			return;
		}

		var baseline = File.ReadAllLines(baselineInSource).Where(line => line.Length > 0).ToList();
		var missing = baseline.Except(current).ToList();
		var added = current.Except(baseline).ToList();
		Assert.True(missing.Count == 0 && added.Count == 0,
			"The round-trip result of the fixture files changed. Expected (baseline) but not seen:\n  " + string.Join("\n  ", missing)
			+ "\nSeen but not in the baseline:\n  " + string.Join("\n  ", added)
			+ "\nIf that is an improvement (or a new file), rerun with OD_UPDATE_BASELINE=1 and review the diff.");
	}

	[Fact]
	public void The_baseline_records_how_much_is_lost_so_the_total_is_visible()
	{
		var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "tests", "OpenDevelop.WinUIXamlDocument.Tests", "real-files-baseline.txt"))
			.Where(line => line.Length > 0).ToList();
		int identical = lines.Count(line => line.EndsWith("\tIdentical", StringComparison.Ordinal));
		TestContext.Current.SendDiagnosticMessage($"{identical} of {lines.Count} fixture files round-trip unchanged.");
		Assert.NotEmpty(lines);
	}
}
