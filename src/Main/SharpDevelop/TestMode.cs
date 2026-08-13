using System;

namespace ICSharpCode.SharpDevelop
{
	/// <summary>
	/// Whether this process is being driven by the integration-test agent
	/// (OpenDevelopAppFixture launches the app with OD_TEST_MODE=1).
	///
	/// A test run - and CI especially - has nobody to click a modal dialog, so any prompt shown
	/// there hangs the whole run until it times out. Code that would block on user input must
	/// check this and fall back to a safe default instead. Note that <see cref="IMessageService"/>
	/// already does this centrally (see WpfMessageService); this flag is for the handful of call
	/// sites that deliberately bypass that service and call MessageBox.Show directly.
	/// </summary>
	static class TestMode
	{
		public static bool IsActive { get; } = Environment.GetEnvironmentVariable("OD_TEST_MODE") == "1";
	}
}
