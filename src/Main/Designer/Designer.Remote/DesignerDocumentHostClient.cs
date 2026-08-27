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
		protected DesignerDocumentHostClient(DesignerHostProcessClient connection)
		{
			HostConnection = connection ?? throw new ArgumentNullException(nameof(connection));
			Document = new DesignerDocumentRpcClient(connection, SessionId, DocumentId);
		}

		protected DesignerHostProcessClient HostConnection { get; }
		protected DesignerDocumentRpcClient Document { get; }

		public string DocumentId { get; } = Guid.NewGuid().ToString("N");
		public int ProcessId => HostConnection.ProcessId;
		public bool IsAlive => HostConnection.IsAlive;
		public string ChildLog => HostConnection.ChildLog;
		public string SessionId => HostConnection.SessionId;
		public event EventHandler? HostExited { add => HostConnection.HostExited += value; remove => HostConnection.HostExited -= value; }

		public Task PingAsync(CancellationToken cancellationToken = default)
			=> HostConnection.InvokeAsync<object>("ping", null!, cancellationToken);

		public void TerminateHost() => HostConnection.TerminateHost();

		public Task ShutdownAsync(CancellationToken cancellationToken = default)
			=> Document.CloseAsync(cancellationToken);
	}
}
