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
					// No continuation here may need the caller's (UI) context: the synchronous open blocks the UI
					// thread in WaitUntilIdle until the semaphore is released.
					try {
						await oneBuildAtATime.WaitAsync(cancellation.Token).ConfigureAwait(false);
					} catch (OperationCanceledException) {
						return null;
					}
					int released = 0;
					void Release() { if (Interlocked.Exchange(ref released, 1) == 0) oneBuildAtATime.Release(); }
					try {
						solution = await Task.Run(() => { try { return build(progress); } finally { Release(); } }, cancellation.Token).ConfigureAwait(false);
					} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
						return null;
					} finally {
						Release(); // when the task was cancelled before it started
					}
				}
				if (cancellation.IsCancellationRequested) {
					// Superseded: the build has finished, so this thread may take the model over to discard it.
					solution.ThreadAffinity.TransferOwnershipToCurrentThread();
					solution.Dispose();
					return null;
				}
				return solution; // still owned by the build thread; the caller takes it over
			} finally {
				Interlocked.CompareExchange(ref current, null, cancellation);
			}
		}
	}
}
