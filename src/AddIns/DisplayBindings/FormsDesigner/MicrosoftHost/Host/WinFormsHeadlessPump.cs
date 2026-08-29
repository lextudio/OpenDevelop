using System.Windows.Forms;

namespace ICSharpCode.FormsDesigner.MicrosoftHost;

/// <summary>
/// A headless WinForms message pump for the Microsoft WindowsDesktop child host, and the
/// SynchronizationContext that marshals DDP calls onto it.
///
/// Why this exists at all: unlike LibreWinForms, Microsoft WinForms designers create real Win32
/// windows - <c>BehaviorService</c>'s <c>AdornerWindow</c> is an actual HWND, and building a design
/// surface goes through <c>Control.CreateHandle</c> and OLE drag/drop registration. Two things
/// follow that the LibreWinForms host never needed:
///
///   1. The thread doing it must be STA (otherwise RegisterDragDrop throws
///      "DragDrop registration did not succeed").
///   2. That thread must be pumping messages, or handle creation and the designer's own
///      SendMessage-based initialization simply never complete - which showed up as session/open
///      hanging until the client's timeout, not as an error.
///
/// StreamJsonRpc dispatches incoming requests on thread-pool threads (MTA, no pump) by default, so
/// neither holds without this. The host therefore keeps the pump on its STA Main thread and hands
/// <see cref="SynchronizationContext"/> to DesignerChildHost.Run, which makes every RPC invocation
/// run here. This mirrors what the WPF surface host does with WpfHeadlessDispatcher.
/// </summary>
sealed class WinFormsHeadlessPump : IDisposable
{
	// WindowsFormsSynchronizationContext needs a control with a created handle to marshal through.
	// A plain Control never made visible is enough - it is only ever used as a message sink.
	readonly Control marshal = new();

	public WinFormsHeadlessPump()
	{
		// Force handle creation NOW, on the constructing (STA, soon-to-pump) thread. Deferring it
		// would let the first marshaled call create the handle on whatever thread got there first,
		// reintroducing exactly the affinity bug this class exists to prevent.
		_ = marshal.Handle;
		SynchronizationContext = new WindowsFormsSynchronizationContext();
	}

	/// <summary>Marshals onto the pumping thread. Safe to hand out before <see cref="Run"/> starts:
	/// posts queue on the control's HWND and drain once the loop is running.</summary>
	public SynchronizationContext SynchronizationContext { get; }

	/// <summary>Runs the message loop. Blocks until <see cref="Shutdown"/>.</summary>
	public void Run() => Application.Run();

	/// <summary>Ends the message loop. Callable from any thread - DesignerChildHost invokes it from
	/// the worker running the DDP wait loop.</summary>
	public void Shutdown() => SynchronizationContext.Post(_ => Application.ExitThread(), null);

	public void Dispose() => marshal.Dispose();
}
