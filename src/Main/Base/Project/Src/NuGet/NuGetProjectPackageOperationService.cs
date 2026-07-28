using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace ICSharpCode.SharpDevelop.NuGet
{
	public sealed class NuGetProjectPackageOperationService
	{
		readonly Func<string, CancellationToken, Task<RestoreResult>> restoreRunner;
		readonly NuGetPackageConflictResolutionService conflictResolutionService;

		public NuGetProjectPackageOperationService()
			: this(RunDotNetRestoreAsync)
		{
		}

		public NuGetProjectPackageOperationService(Func<string, CancellationToken, Task<RestoreResult>> restoreRunner)
			: this(restoreRunner, new NuGetPackageConflictResolutionService())
		{
		}

		public NuGetProjectPackageOperationService(
			Func<string, CancellationToken, Task<RestoreResult>> restoreRunner,
			NuGetPackageConflictResolutionService conflictResolutionService)
		{
			this.restoreRunner = restoreRunner ?? throw new ArgumentNullException(nameof(restoreRunner));
			this.conflictResolutionService = conflictResolutionService ?? throw new ArgumentNullException(nameof(conflictResolutionService));
		}

		/// <summary>
		/// Walks the full transitive dependency closure of <paramref name="packageId"/> against the
		/// project's currently installed direct package references (see
		/// <see cref="NuGetPackageConflictResolutionService"/>) before touching the project file.
		/// Returns the conflict report unresolved (with <see cref="NuGetProjectPackageOperationResult"/>
		/// left as an unstarted/unchanged result) when a version conflict is found, so a caller (UI or
		/// script) can surface it instead of silently proceeding with an incompatible graph.
		/// </summary>
		public async Task<(NuGetPackageConflictResolutionResult Conflicts, NuGetProjectPackageOperationResult Operation)> AddPackageReferenceWithConflictCheckAsync(
			string projectFileName,
			IReadOnlyList<PackageSource> sources,
			NuGetFramework targetFramework,
			string packageId,
			NuGetVersion version,
			bool restore,
			CancellationToken cancellationToken)
		{
			if (projectFileName is null)
				throw new ArgumentNullException(nameof(projectFileName));
			if (sources is null)
				throw new ArgumentNullException(nameof(sources));
			if (version is null)
				throw new ArgumentNullException(nameof(version));

			var editor = new SdkStylePackageReferenceEditor(projectFileName);
			var installed = editor.GetPackageReferences()
				.Where(reference => NuGetVersion.TryParse(reference.Version, out _))
				.Select(reference => new PackageIdentity(reference.Id, NuGetVersion.Parse(reference.Version)))
				.ToArray();

			var conflictResult = await conflictResolutionService
				.ResolveAsync(sources, installed, packageId, version, targetFramework, cancellationToken)
				.ConfigureAwait(false);

			if (!conflictResult.Succeeded)
				return (conflictResult, new NuGetProjectPackageOperationResult(false, false, null, string.Empty, string.Empty));

			var operationResult = await AddPackageReferenceAsync(projectFileName, packageId, version, restore, cancellationToken)
				.ConfigureAwait(false);
			return (conflictResult, operationResult);
		}

		public Task<NuGetProjectPackageOperationResult> AddPackageReferenceAsync(
			string projectFileName,
			string packageId,
			NuGetVersion version,
			bool restore,
			CancellationToken cancellationToken)
		{
			if (version is null)
				throw new ArgumentNullException(nameof(version));

			return RunAsync(
				projectFileName,
				restore,
				cancellationToken,
				editor => editor.AddOrUpdate(packageId, version));
		}

		public Task<NuGetProjectPackageOperationResult> RemovePackageReferenceAsync(
			string projectFileName,
			string packageId,
			bool restore,
			CancellationToken cancellationToken)
		{
			return RunAsync(
				projectFileName,
				restore,
				cancellationToken,
				editor => editor.Remove(packageId));
		}

		async Task<NuGetProjectPackageOperationResult> RunAsync(
			string projectFileName,
			bool restore,
			CancellationToken cancellationToken,
			Func<SdkStylePackageReferenceEditor, bool> operation)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var editor = new SdkStylePackageReferenceEditor(projectFileName);
			var changed = operation(editor);
			if (!changed || !restore)
				return new NuGetProjectPackageOperationResult(changed, false, null, string.Empty, string.Empty);

			var restoreResult = await restoreRunner(projectFileName, cancellationToken).ConfigureAwait(false);
			return new NuGetProjectPackageOperationResult(
				changed,
				true,
				restoreResult.ExitCode,
				restoreResult.Output,
				restoreResult.Error);
		}

		static async Task<RestoreResult> RunDotNetRestoreAsync(string projectFileName, CancellationToken cancellationToken)
		{
			var startInfo = new ProcessStartInfo("dotnet") {
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WorkingDirectory = Path.GetDirectoryName(projectFileName) ?? AppContext.BaseDirectory
			};
			startInfo.ArgumentList.Add("restore");
			startInfo.ArgumentList.Add(projectFileName);

			using (var process = new Process { StartInfo = startInfo }) {
				process.Start();
				var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
				var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
				await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
				var output = await outputTask.ConfigureAwait(false);
				var error = await errorTask.ConfigureAwait(false);
				return new RestoreResult(process.ExitCode, output, error);
			}
		}

		public sealed class RestoreResult
		{
			public RestoreResult(int exitCode, string output, string error)
			{
				ExitCode = exitCode;
				Output = output ?? string.Empty;
				Error = error ?? string.Empty;
			}

			public int ExitCode { get; }
			public string Output { get; }
			public string Error { get; }
		}
	}
}
