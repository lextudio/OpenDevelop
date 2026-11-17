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

namespace ICSharpCode.AddInManager2.Model
{
	/// <summary>
	/// Catches and broadcasts package-related events.
	/// </summary>
	public class AddInManagerEvents : IAddInManagerEvents
	{
		public event EventHandler OperationStarted;
		
		public void OnOperationStarted()
		{
			if (OperationStarted != null)
			{
				SD.Log.DebugFormatted("[AddInManager2.Events] Operation started.");
				OperationStarted(this, new EventArgs());
			}
		}
		
		public void OnOperationStarted(EventArgs e)
		{
			if (OperationStarted != null)
			{
				SD.Log.DebugFormatted("[AddInManager2.Events] Operation started.");
				OperationStarted(this, e);
			}
		}
		
		public event EventHandler AddInManagerViewOpened;
		
		public void OnAddInManagerViewOpened(EventArgs e)
		{
			if (AddInManagerViewOpened != null)
			{
				SD.Log.DebugFormatted("[AddInManager2.Events] AddInManagerView opened.");
				AddInManagerViewOpened(this, e);
			}
		}
		
		public void OnAddInManagerViewOpened()
		{
			if (AddInManagerViewOpened != null)
			{
				SD.Log.DebugFormatted("[AddInManager2.Events] AddInManagerView opened.");
				AddInManagerViewOpened(this, new EventArgs());
			}
		}
		
		public event EventHandler<AddInInstallationEventArgs> AddInInstalled;
		
		public void OnAddInInstalled(AddInInstallationEventArgs e)
		{
			if (AddInInstalled != null)
			{
				SD.Log.DebugFormatted("[AddInManager2.Events] AddIn installed: {0}", e.AddIn.Name);
				AddInInstalled(this, e);
			}
		}
		
		public event EventHandler<AddInInstallationEventArgs> AddInUninstalled;
		
		public void OnAddInUninstalled(AddInInstallationEventArgs e)
		{
			if (AddInUninstalled != null)
			{
				SD.Log.DebugFormatted("[AddInManager2.Events] AddIn uninstalled: {0}", e.AddIn.Name);
				AddInUninstalled(this, e);
			}
		}
		
		public event EventHandler<AddInOperationErrorEventArgs> AddInOperationError;
		
		public void OnAddInOperationError(AddInOperationErrorEventArgs e)
		{
			if (AddInOperationError != null)
			{
				SD.Log.DebugFormatted("[AddInManager2.Events] Operation error: {0}", e.Message);
				AddInOperationError(this, e);
			}
		}
		
		public event EventHandler<AddInInstallationEventArgs> AddInStateChanged;
		
		public void OnAddInStateChanged(AddInInstallationEventArgs e)
		{
			if (AddInStateChanged != null)
			{
				SD.Log.DebugFormatted("[AddInManager2.Events] AddIn state changed: {0}", e.AddIn.Name);
				AddInStateChanged(this, e);
			}
		}
		
	}
}
