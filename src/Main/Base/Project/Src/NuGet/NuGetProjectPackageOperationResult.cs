namespace ICSharpCode.SharpDevelop.NuGet
{
	public sealed class NuGetProjectPackageOperationResult
	{
		public NuGetProjectPackageOperationResult(
			bool changed,
			bool restoreRequested,
			int? restoreExitCode,
			string restoreOutput,
			string restoreError)
		{
			Changed = changed;
			RestoreRequested = restoreRequested;
			RestoreExitCode = restoreExitCode;
			RestoreOutput = restoreOutput ?? string.Empty;
			RestoreError = restoreError ?? string.Empty;
		}

		public bool Changed { get; }
		public bool RestoreRequested { get; }
		public int? RestoreExitCode { get; }
		public string RestoreOutput { get; }
		public string RestoreError { get; }
		public bool RestoreSucceeded => !RestoreRequested || RestoreExitCode == 0;
		public bool Succeeded => RestoreSucceeded;
	}
}
