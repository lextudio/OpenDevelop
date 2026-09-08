// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.LanguageServices.Protocol
{
	/// <summary>
	/// Runs the Roslyn language host as a child process and speaks the protocol to it, by reusing
	/// the designers' <see cref="DesignerHostProcessClient"/> for everything that is not
	/// language-specific: spawning, the port/token handshake, liveness, the child's log, timeouts
	/// and the <c>HostExited</c> signal.
	///
	/// Reusing it rather than writing a second one is not only less code - those parts are where
	/// the awkward failure modes live (a child that dies during handshake, a parent that exits
	/// while a call is in flight), and they are already solved and tested for the designers.
	/// </summary>
	public sealed class RoslynHostProcessTransport : DesignerHostProcessClient, IRoslynProtocolTransport, IRecoveringRoslynHost
	{
		readonly string hostDllPath;
		readonly SemaphoreSlim startupGate = new(1, 1);

		public RoslynHostProcessTransport(string hostDllPath, TimeSpan? operationTimeout = null)
			: base(operationTimeout)
		{
			this.hostDllPath = hostDllPath ?? throw new ArgumentNullException(nameof(hostDllPath));
		}

		protected override string GetChildDllPath() => hostDllPath;

		/// <summary>
		/// The host next to the running IDE. Returns null when it was not deployed, which callers
		/// must treat as "stay in-process" rather than as an error: an IDE whose language features
		/// stop working because an optional child process is missing would be a bad trade.
		/// </summary>
		public static string? TryResolveDefaultHostPath()
		{
			var directory = Path.GetDirectoryName(typeof(RoslynHostProcessTransport).Assembly.Location);
			if (string.IsNullOrEmpty(directory))
				return null;
			var candidate = Path.Combine(directory, "RoslynHost", "ICSharpCode.Roslyn.Host.dll");
			return File.Exists(candidate) ? candidate : null;
		}

		/// <summary>
		/// Spawns the host if it is not already running. Separate from the first request rather than
		/// implicit in it, because startup is where the interesting failures are (missing runtime,
		/// handshake timeout) and a caller wants those attributable to "the host would not start"
		/// rather than to whichever request happened to be first.
		/// </summary>
		public async Task EnsureStartedAsync(CancellationToken cancellationToken)
		{
			// IsAlive becomes true before StartAsync completes the handshake. Every caller must
			// pass the gate, including those that see a running process during its startup.
			await startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				if (!IsAlive)
					await StartAsync(cancellationToken).ConfigureAwait(false);
			} finally {
				startupGate.Release();
			}
		}

		public async Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken)
		{
			await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
			// DesignerHostProcessClient sends an anonymous object as a JSON-RPC parameter object,
			// expanding each of its properties into a separate RPC argument. The Roslyn dispatcher
			// deliberately takes ONE JsonRpcArguments object, so preserve that wire envelope instead
			// of making every protocol method invent a positional handler signature.
			if (method == RoslynProtocolMethods.Status)
				return await base.InvokeAsync<T>(method, arguments, cancellationToken, timeout: null).ConfigureAwait(false);
			return await base.InvokeAsync<T>(method, new { arguments }, cancellationToken, timeout: null).ConfigureAwait(false);
		}
	}
}
