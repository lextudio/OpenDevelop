using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.FormsDesigner.Host;

static class Program
{
	// Required, not cosmetic: the WinForms designer's BehaviorService creates an AdornerWindow,
	// whose OnHandleCreated calls SetAcceptDrops -> OLE RegisterDragDrop. On Microsoft WinForms
	// that throws ThreadStateException ("Current thread must be set to single thread apartment
	// (STA) mode before OLE calls can be made"), surfacing as an InvalidOperationException
	// "DragDrop registration did not succeed" that kills the child at the first design session.
	// LibreWinForms tolerated its absence because it does not go through real OLE, which is why
	// this only showed up once the Microsoft host was exercised. The WPF and WinUI hosts already
	// carry the same attribute.
	[STAThread]
	static int Main(string[] args)
	{
#if MICROSOFT_WINFORMS
		// Microsoft WinForms designers need a pumping STA thread, which the DDP wait loop's own
		// thread is not (StreamJsonRpc dispatches on the thread pool). Keep the pump on Main and
		// move the wait loop to a worker, marshalling every RPC back here - the same split the WPF
		// surface host uses. See WinFormsHeadlessPump for the full reasoning.
		using var pump = new MicrosoftHost.WinFormsHeadlessPump();
		var exitCode = 0;
		var host = Task.Run(() => exitCode = DesignerChildHost.Run(args, "FormsDesigner.Host",
			token => new MultiDocumentDesignerHostService(token),
			afterShutdown: pump.Shutdown,
			rpcSynchronizationContext: pump.SynchronizationContext));
		pump.Run();
		host.GetAwaiter().GetResult();
		return exitCode;
#else
		return DesignerChildHost.Run(args, "FormsDesigner.Host", token => new MultiDocumentDesignerHostService(token));
#endif
	}
}
