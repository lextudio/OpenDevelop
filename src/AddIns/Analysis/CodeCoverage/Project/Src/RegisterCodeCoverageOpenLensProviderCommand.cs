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

namespace ICSharpCode.CodeCoverage
{
	/// <summary>
	/// Registers <see cref="CodeCoverageOpenLensProvider"/> against the shared
	/// <see cref="OpenLensProviderRegistry"/>, mirroring
	/// <c>CSharpBinding.RegisterCSharpOpenLensProvidersCommand</c>'s ownership pattern - the
	/// CodeCoverage AddIn owns its own lens contribution rather than the OpenLens host needing to
	/// know CodeCoverage exists.
	/// </summary>
	public sealed class RegisterCodeCoverageOpenLensProviderCommand : AbstractCommand, IDisposable
	{
		IDisposable registration;
		OpenLensProviderRegistry registry;

		public override void Run()
		{
			registry = SD.GetRequiredService<OpenLensProviderRegistry>();
			registration = registry.RegisterProvider(new CodeCoverageOpenLensProvider());
			// This command runs from /SharpDevelop/Autostart (CoreStartup.RunInitialization()),
			// before IWorkbench is registered as a service - CodeCoverageService's static
			// constructor no longer requires it eagerly (see its TryHookViewOpened()), so this
			// touch is safe this early.
			CodeCoverageService.ResultsChanged += OnResultsChanged;
		}

		// doc §13: a coverage run finishing refreshes only the coverage lens, not references/
		// implementations/other providers - DocumentId null here means "every open document",
		// since a single run can touch many files at once.
		void OnResultsChanged(object sender, EventArgs e) =>
			registry.RequestRefresh(new OpenLensRefreshEventArgs("CodeCoverage"));

		public void Dispose()
		{
			CodeCoverageService.ResultsChanged -= OnResultsChanged;
			registration?.Dispose();
		}
	}
}
