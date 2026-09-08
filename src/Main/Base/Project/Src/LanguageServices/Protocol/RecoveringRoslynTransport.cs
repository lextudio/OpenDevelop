#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;

namespace ICSharpCode.SharpDevelop.LanguageServices.Protocol
{
	/// <summary>Host operations needed by recovery; separate so ordering can be tested without a process.</summary>
	public interface IRecoveringRoslynHost : IDisposable
	{
		bool IsAlive { get; }
		Task EnsureStartedAsync(CancellationToken cancellationToken);
		Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken);
	}

    /// <summary>Parent-owned state replayed before queries enter a replacement child.</summary>
    public sealed class RecoveringRoslynTransport : IRoslynProtocolTransport, IDisposable
    {
		readonly Func<IRecoveringRoslynHost> createHost;
        readonly SemaphoreSlim gate = new(1, 1);
        readonly Dictionary<(string, string?), RemoteRoslynLanguageProtocol.ProjectUpdate> projects = new();
        readonly Dictionary<string, RemoteRoslynLanguageProtocol.DocumentUpdate> documents = new();
		IRecoveringRoslynHost? host;
        bool disposed;

		public RecoveringRoslynTransport(Func<IRecoveringRoslynHost> createHost) =>
			this.createHost = createHost ?? throw new ArgumentNullException(nameof(createHost));

        public async Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken)
        {
			var isStateMutation = arguments is RemoteRoslynLanguageProtocol.ProjectUpdate
				|| arguments is RemoteRoslynLanguageProtocol.DocumentUpdate
				|| method == RoslynProtocolMethods.SolutionClosed;
			IRecoveringRoslynHost activeHost;
			await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				ObjectDisposedException.ThrowIf(disposed, this);
                // Updates are parent intent, even if the child dies before acknowledging them.
                // A later replay therefore restores the latest buffer instead of the last reply.
                if (arguments is RemoteRoslynLanguageProtocol.ProjectUpdate project)
                    projects[(project.snapshot.ProjectFileName, project.snapshot.TargetFramework)] = project;
                if (arguments is RemoteRoslynLanguageProtocol.DocumentUpdate document)
                    documents[document.document.Uri] = document;
                if (method == RoslynProtocolMethods.SolutionClosed)
                {
                    projects.Clear();
                    documents.Clear();
                    // Closing a solution ends this child lifetime, including loose buffers.
					host?.Dispose();
					host = null;
					return default!;
				}
				activeHost = await EnsureHostAndReplayAsync(cancellationToken).ConfigureAwait(false);
				// Mutations remain serialized through their acknowledgement. Queries only need the
				// short critical section above: after replay they may share StreamJsonRpc's normal
				// concurrent request path instead of one slow reference search blocking completion.
				if (isStateMutation)
					return await InvokeAndHandleFailureAsync<T>(activeHost, method, arguments, cancellationToken, stateGateAlreadyHeld: true).ConfigureAwait(false);
			}
			finally { gate.Release(); }

			return await InvokeAndHandleFailureAsync<T>(activeHost, method, arguments, cancellationToken, stateGateAlreadyHeld: false).ConfigureAwait(false);
		}

		async Task<IRecoveringRoslynHost> EnsureHostAndReplayAsync(CancellationToken cancellationToken)
		{
			if (host != null && host.IsAlive)
				return host;
			host?.Dispose();
			host = createHost();
			try {
				await host.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
				foreach (var snapshot in projects.Values)
					await host.InvokeAsync<object?>(RoslynProtocolMethods.ProjectLoad, snapshot, cancellationToken).ConfigureAwait(false);
				foreach (var buffer in documents.Values)
					await host.InvokeAsync<object?>(RoslynProtocolMethods.DidChange, buffer, cancellationToken).ConfigureAwait(false);
				return host;
			} catch {
				host.Dispose();
				host = null;
				throw;
			}
		}

		async Task<T> InvokeAndHandleFailureAsync<T>(IRecoveringRoslynHost activeHost, string method, object arguments, CancellationToken cancellationToken, bool stateGateAlreadyHeld)
		{
			try { return await activeHost.InvokeAsync<T>(method, arguments, cancellationToken).ConfigureAwait(false); }
				catch (RemoteInvocationException)
                {
                    // A reply containing a language-service error proves that RPC is working.
                    // Stale selections and invalid requests are not process failures: restarting
                    // here would discard a healthy workspace and turn retries into cold starts.
                    throw;
                }
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && activeHost.IsAlive)
                {
                    // Caret movement cancels queries routinely. Keep a live transport and its
                    // workspace; cancellation is not evidence of a crashed child.
                    throw;
                }
				catch {
                    // Do not replay a request with an uncertain outcome automatically. Next
                    // request reconstructs state, including partially applied/cancelled updates.
					if (!stateGateAlreadyHeld)
						await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
					try {
						if (ReferenceEquals(host, activeHost)) {
							host.Dispose();
							host = null;
						}
					} finally {
						if (!stateGateAlreadyHeld) gate.Release();
					}
					throw;
				}
		}

        public void Dispose()
        {
            gate.Wait();
            try
            {
                if (disposed) return;
                disposed = true;
                host?.Dispose();
                host = null;
                projects.Clear();
                documents.Clear();
            }
            finally { gate.Release(); }
        }
    }
}
