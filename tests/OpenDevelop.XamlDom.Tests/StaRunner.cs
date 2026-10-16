namespace OpenDevelop.XamlDom.Tests;

static class StaRunner
{
	/// <summary>Runs on a fresh STA thread on Windows, where real WPF refuses to build UI objects elsewhere.</summary>
	public static T Run<T>(Func<T> action)
	{
		T result = default!;
		Exception? error = null;
		var thread = new Thread(() => {
			try { result = action(); } catch (Exception e) { error = e; }
			finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
		});
		if (OperatingSystem.IsWindows())
			thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		thread.Join();
		if (error != null)
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
		return result;
	}
}
