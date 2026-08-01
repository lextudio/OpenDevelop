using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeCoverage.Core;
using ICSharpCode.SharpDevelop.Project;
using MonoDevelop.Projects;

namespace ICSharpCode.CodeCoverage
{
	/// <summary>
	/// Adapts OpenDevelop's detailed OpenCover model to the platform-neutral repository linked
	/// from VSMac-CodeCoverage. The upstream repository now owns configuration-aware persistence;
	/// the existing model remains responsible for the richer class/method/sequence-point UI.
	/// </summary>
	static class VSMacCoverageRepositoryAdapter
	{
		static readonly CoverageResultsRepository repository = new CoverageResultsRepository(new OpenCoverResultsParser());

		public static void Save(IProject project, string resultsFile)
		{
			using (var stream = File.OpenRead(resultsFile)) {
				var results = new OpenCoverResultsParser().ParseFrom(stream);
				repository.SaveResults(results, Wrap(project), GetConfiguration(project));
			}
		}

		public static CodeCoverageResults Load(IProject project)
		{
			try {
				return (repository.ResultsFor(Wrap(project), GetConfiguration(project)) as OpenCoverResultsAdapter)?.Results;
			} catch (FileNotFoundException) {
				// Non-test and not-yet-built projects have no output path for the repository.
				return null;
			} catch (DirectoryNotFoundException) {
				return null;
			}
		}

		static Project Wrap(IProject project) => new Project(project);
		static ConfigurationSelector GetConfiguration(IProject project)
			=> new ConfigurationSelector(project.ActiveConfiguration.Configuration ?? string.Empty);
	}

	sealed class OpenCoverResultsParser : ICoverageResultsParser
	{
		public string FileExtension => "xml";

		public ICoverageResults ParseFrom(Stream stream)
		{
			using (var memory = new MemoryStream()) {
				stream.CopyTo(memory);
				return new OpenCoverResultsAdapter(memory.ToArray());
			}
		}
	}

	sealed class OpenCoverResultsAdapter : ICoverageResults
	{
		readonly byte[] report;

		public OpenCoverResultsAdapter(byte[] report)
		{
			this.report = report;
			using (var stream = new MemoryStream(report, writable: false))
			using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true))
				Results = new CodeCoverageResults(reader);

			ModuleCoverage = Results.Modules.ToDictionary(
				module => module.Name,
				module => {
					int visited = module.GetVisitedCodeLength();
					int total = visited + module.GetUnvisitedCodeLength();
					double line = total == 0 ? 0 : visited * 100.0 / total;
					return new CoverageSummary(line, (double)module.GetVisitedBranchCoverage());
				},
				StringComparer.OrdinalIgnoreCase);
		}

		public CodeCoverageResults Results { get; }
		public Dictionary<string, CoverageSummary> ModuleCoverage { get; }

		public Dictionary<int, int> CoverageForFile(string path)
		{
			return Results.GetSequencePoints(path)
				.GroupBy(point => point.Line)
				.ToDictionary(group => group.Key, group => group.Sum(point => point.VisitCount));
		}

		public void SaveTo(Stream stream) => stream.Write(report, 0, report.Length);
	}
}
