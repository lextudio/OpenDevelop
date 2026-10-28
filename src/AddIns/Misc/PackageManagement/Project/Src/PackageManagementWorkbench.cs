// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
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
using ICSharpCode.PackageManagement.Scripting;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.PackageManagement
{
	public class PackageManagementWorkbench : IPackageManagementWorkbench
	{
		public void CreateConsolePad()
		{
			PadDescriptor pad = GetConsolePad();
			EnsurePackageManagementConsoleViewModelIsCreated(pad);
		}
		
		PadDescriptor GetConsolePad()
		{
			return SD.Workbench.GetPad(typeof(PackageManagementConsolePad));
		}
		
		void EnsurePackageManagementConsoleViewModelIsCreated(PadDescriptor pad)
		{
			// Force creation of view model.
			object control = pad.PadContent.Control;
		}
		
		public void ShowConsolePad()
		{
			PadDescriptor pad = GetConsolePad();
			// PadDescriptor caches PadContent. Launch on every show so switching projects (or
			// reopening a console the user closed) requests the correctly keyed pwsh session.
			OpenConsoleAsync().FireAndForget();
			// OpenConsoleAsync brings the real Terminal pad forward. Do not raise this legacy
			// shim afterwards, or its placeholder control would obscure the pwsh session.
		}

		async System.Threading.Tasks.Task OpenConsoleAsync()
		{
			try {
				await PackageManagementConsolePad.OpenConsoleAsync();
			} catch (Exception ex) {
				SD.MessageService.ShowError("Package Manager Console could not start pwsh: " + ex.Message);
			}
		}
		
		public bool InvokeRequired {
			get { return SD.MainThread.InvokeRequired; }
		}
		
		public void SafeThreadAsyncCall<A>(Action<A> method, A arg1)
		{
			SD.MainThread.InvokeAsyncAndForget(() => method(arg1));
		}
		
		public void SafeThreadAsyncCall<A, B>(Action<A, B> method, A arg1, B arg2)
		{
			SD.MainThread.InvokeAsyncAndForget(() => method(arg1, arg2));
		}
		
		public R SafeThreadFunction<R>(Func<R> method)
		{
			return SD.MainThread.InvokeIfRequired(method);
		}
	}
}
