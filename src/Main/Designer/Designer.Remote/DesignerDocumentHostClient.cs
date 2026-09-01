// Shared parent-side state for a document lease on an isolated designer host.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>
	/// Provides the runtime-neutral part of a designer client: document identity, process status,
	/// host-exit forwarding and the basic ping/close lifecycle. Concrete adapters retain only their
	/// launch compatibility key and runtime-specific RPC payloads.
	/// </summary>
	public abstract class DesignerDocumentHostClient
	{
		EventHandler? hostExited;

		protected DesignerDocumentHostClient(DesignerHostProcessClient connection)
		{
			HostConnection = connection ?? throw new ArgumentNullException(nameof(connection));
			Document = new DesignerDocumentRpcClient(connection, SessionId, DocumentId);
			HostConnection.HostExited += OnHostConnectionExited;
		}

		protected DesignerHostProcessClient HostConnection { get; private set; }
		protected DesignerDocumentRpcClient Document { get; }

		public string DocumentId { get; } = Guid.NewGuid().ToString("N");
		public int ProcessId => HostConnection.ProcessId;
		public bool IsAlive => HostConnection.IsAlive;
		public string ChildLog => HostConnection.ChildLog;
		public string SessionId => HostConnection.SessionId;
		public event EventHandler? HostExited { add => hostExited += value; remove => hostExited -= value; }

		public Task PingAsync(CancellationToken cancellationToken = default)
			=> HostConnection.InvokeAsync<object>("ping", null!, cancellationToken);

		public void TerminateHost() => HostConnection.TerminateHost();

		public Task ShutdownAsync(CancellationToken cancellationToken = default)
			=> Document.CloseAsync(cancellationToken);

		/// <summary>Moves this document lease to a replacement shared host after recovery.
		/// The document id stays stable while the document RPC helper starts using the new
		/// session/transport.</summary>
		protected void RebindConnection(DesignerHostProcessClient replacement)
		{
			HostConnection.HostExited -= OnHostConnectionExited;
			HostConnection = replacement ?? throw new ArgumentNullException(nameof(replacement));
			Document.ReplaceConnection(replacement);
			HostConnection.HostExited += OnHostConnectionExited;
		}

		/// <summary>Removes the lease's event subscription before a shared connection is released.</summary>
		protected void DetachHostConnection() => HostConnection.HostExited -= OnHostConnectionExited;

		void OnHostConnectionExited(object? sender, EventArgs e) => hostExited?.Invoke(this, EventArgs.Empty);
	}
}
