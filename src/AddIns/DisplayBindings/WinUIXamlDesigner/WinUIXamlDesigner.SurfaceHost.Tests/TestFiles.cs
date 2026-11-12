namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

static class TestFiles
{
	/// <summary>
	/// Removes a temporary directory a surface process may still hold files of for a moment after it was told to stop (a loaded DLL
	/// reports "access denied", not "in use"), the same retry the product's own cleanup does. A directory that stays locked is left
	/// for the OS temp cleanup: a test must not fail in its teardown for that.
	/// </summary>
	public static void TryDelete(string directory)
	{
		for (int attempt = 1; attempt <= 10; attempt++) {
			try {
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
				return;
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Thread.Sleep(300);
			}
		}
	}
}
