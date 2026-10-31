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
using ICSharpCode.SharpDevelop;
using ICSharpCode.AddInManager2.View;

namespace ICSharpCode.AddInManager2
{
	public class ShowAddInManagerCommand : SimpleCommand
	{
		public override void Execute(object parameter)
		{
			// Open AddInManager2 main dialog
			using (AddInManagerView view = AddInManagerView.Create())
			{
				view.ShowDialog();
			}
		}
	}
	
	public class AddInManagerInitializationCommand : SimpleCommand
	{
		public override void Execute(object parameter)
		{
			// Remove all unreferenced NuGet packages
			AddInManagerServices.Setup.RemoveUnreferencedNuGetPackages();
		}
	}
	
	public class AddInManagerVisualInitializationCommand : SimpleCommand
	{
		static UpdateNotifier updateNotifier;

		public override void Execute(object parameter)
		{
			if (!AddInManagerServices.Settings.AutoSearchForUpdates) {
				return;
			}
			// The lookup is an optional convenience that starts on its own during every startup,
			// so it must never be able to take the session down: a faulted read task is reported
			// in the AddIn Manager views, and anything thrown synchronously here is logged and
			// dropped. (The legacy NuGet engine can throw out of a static constructor on Unix;
			// PackageRepositories keeps remote sources away from it for that reason.)
			try {
				updateNotifier = new UpdateNotifier();
				updateNotifier.StartUpdateLookup();
			} catch (Exception ex) {
				ICSharpCode.Core.LoggingService.Warn("Could not start the add-in update lookup: " + ex.Message);
			}
		}
	}
}
