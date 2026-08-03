// Copyright (c) 2025 AlphaSierraPapa for the SharpDevelop Team
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

using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;

namespace ICSharpCode.UnitTesting
{
	/// <summary>
	/// Registers <see cref="TestOpenLensProvider"/> against the shared
	/// <see cref="OpenLensProviderRegistry"/>, mirroring
	/// <c>CSharpBinding.RegisterCSharpOpenLensProvidersCommand</c>'s ownership pattern.
	/// </summary>
	public sealed class RegisterTestOpenLensProviderCommand : AbstractCommand, IDisposable
	{
		IDisposable registration;
		ITestService testService;

		public override void Run()
		{
			var registry = SD.GetRequiredService<OpenLensProviderRegistry>();
			registration = registry.RegisterProvider(new TestOpenLensProvider());

			testService = SD.GetService<ITestService>();
			if (testService != null)
				testService.RunningTestsChanged += OnRunningTestsChanged;
		}

		// doc §13: a test run finishing refreshes only the test lens. There's no per-test
		// "discovery/result changed" event exposed by ITestService (see
		// doc/technotes/openlens.md's Phase 4 status notes) - RunningTestsChanged also fires when a
		// run *starts*, so this fires one redundant, harmless refresh per run in addition to the one
		// that actually matters (when it ends).
		void OnRunningTestsChanged(object sender, EventArgs e)
		{
			if (!testService.IsRunningTests)
				SD.GetRequiredService<OpenLensProviderRegistry>().RequestRefresh(new OpenLensRefreshEventArgs("UnitTesting"));
		}

		public void Dispose()
		{
			if (testService != null)
				testService.RunningTestsChanged -= OnRunningTestsChanged;
			registration?.Dispose();
		}
	}
}
