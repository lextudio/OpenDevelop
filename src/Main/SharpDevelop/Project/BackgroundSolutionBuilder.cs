// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Builds solutions on the thread pool, one at a time, where a newer request supersedes (cancels) the
	/// one in progress.
	///
	/// The finished model is handed to the thread that awaited the build (see
	/// <see cref="ModelThreadAffinity.TransferOwnershipToCurrentThread"/>), which publishes it; a superseded
	/// build's model is disposed instead. Only one build runs at a time because MSBuild evaluation state
	/// shared by a load (the shared EvaluationContext, the pre-evaluation store) is process-wide.
	/// </summary>
	sealed class BackgroundSolutionBuilder
	{
		readonly SemaphoreSlim oneBuildAtATime = new SemaphoreSlim(1, 1);
		CancellationTokenSource current;

		/// <summary>Cancels the build in progress, if any; its <see cref="BuildAsync"/> returns null.</summary>
		public void CancelPending()
		{
			Interlocked.Exchange(ref current, null)?.Cancel();
		}

		/// <summary>
		/// Blocks until no build is running (after <see cref="CancelPending"/>, at the next project). For the
		/// synchronous load path only; a build never calls into the UI thread, so this cannot deadlock it.
		/// </summary>
		public void WaitUntilIdle()
		{
			oneBuildAtATime.Wait();
			oneBuildAtATime.Release();
		}

		/// <summary>
		/// Runs <paramref name="build"/> on the thread pool after any previous build has finished and returns
		/// its solution, owned by the awaiting thread; or null when the build was cancelled or superseded.
		/// </summary>
		/// <param name="createProgress">Creates the progress monitor for the build; its cancellation token
		/// is the build's.</param>
		public async Task<Solution> BuildAsync(Func<IProgressMonitor, Solution> build, Func<CancellationToken, IProgressMonitor> createProgress, CancellationToken cancellationToken)
		{
			using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var superseded = Interlocked.Exchange(ref current, cancellation);
			if (superseded != null)
				await superseded.CancelAsync();
			try {
				Solution solution;
				using (var progress = createProgress(cancellation.Token)) {
					try {
						await oneBuildAtATime.WaitAsync(cancellation.Token);
					} catch (OperationCanceledException) {
						return null;
					}
					try {
						solution = await Task.Run(() => build(progress), cancellation.Token);
					} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
						return null;
					} finally {
						oneBuildAtATime.Release();
					}
				}
				// The build is over: the awaiting thread takes the model over, to publish or discard it.
				solution.ThreadAffinity.TransferOwnershipToCurrentThread();
				if (cancellation.IsCancellationRequested) {
					solution.Dispose();
					return null;
				}
				return solution;
			} finally {
				Interlocked.CompareExchange(ref current, null, cancellation);
			}
		}
	}
}
