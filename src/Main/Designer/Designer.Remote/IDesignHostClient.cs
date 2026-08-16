// Host-side seam for the common designer protocol (doc/technotes/designer-common.md).
// The shared designer canvas and the per-backend adapters depend on this contract; they
// never reference a runtime type. The lifecycle surface is unified here; the per-document
// method surface (open/update/flush/commands) converges backend-by-backend - WinForms
// already speaks the session/document/design method set, WinUI/Uno converges next.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>Host-side view of one out-of-process designer host.</summary>
	public interface IDesignHostClient : IDisposable
	{
		/// <summary>Child process id (visible so project-code debugging can attach).</summary>
		int ProcessId { get; }

		bool IsAlive { get; }

		/// <summary>Tail of the child's stdout/stderr, for diagnosing startup or render failures.</summary>
		string ChildLog { get; }

		event EventHandler HostExited;

		/// <summary>Liveness probe; a hung child is terminated by the shared timeout.</summary>
		Task PingAsync(CancellationToken cancellationToken = default);

		/// <summary>Requests bounded graceful shutdown (the child then exits).</summary>
		Task ShutdownAsync(CancellationToken cancellationToken = default);

		/// <summary>Kills the child process tree immediately.</summary>
		void TerminateHost();
	}
}
