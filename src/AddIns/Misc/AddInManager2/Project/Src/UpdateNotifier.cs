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
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.AddInManager2.Model;
using ICSharpCode.AddInManager2.View;

namespace ICSharpCode.AddInManager2
{
	/// <summary>
	/// Looks for updates of the add-ins installed from trusted GitHub repositories once at startup and,
	/// when there are any, shows a notification that opens the AddIn Manager on the Updates list.
	/// </summary>
	public class UpdateNotifier
	{
		private readonly IAddInManagerServices _services;
		private bool _hasNotified;
		
		public UpdateNotifier()
			: this(AddInManagerServices.Services)
		{
		}

		public UpdateNotifier(IAddInManagerServices services)
		{
			_services = services;
		}
		
		/// <summary>
		/// Reads the release lists (from the cache when it is recent, so a normal start spends no quota) and
		/// notifies when an update exists. Never throws: this is a convenience that must not harm startup.
		/// </summary>
		public async Task StartUpdateLookupAsync()
		{
			if (_hasNotified)
			{
				return;
			}
			try
			{
				await _services.Releases.RefreshAsync(false, CancellationToken.None);
				if (_services.Releases.GetUpdates().Count == 0)
				{
					return;
				}
			}
			catch (Exception ex)
			{
				ICSharpCode.Core.LoggingService.Warn("The add-in update lookup failed: " + ex.Message);
				return;
			}
			_hasNotified = true;
			SD.MainThread.InvokeAsyncAndForget(() => {
				var notificationHost = SD.Services.GetService(typeof(INotificationHost)) as INotificationHost;
				notificationHost?.Show(
					SD.ResourceService.GetString("AddInManager2.UpdateNotifier.BubbleText"),
					SD.ResourceService.GetString("AddInManager.Title"),
					ShowUpdates);
			});
		}
		
		private void ShowUpdates()
		{
			using (AddInManagerView view = AddInManagerView.Create())
			{
				var viewModel = view.ViewModel;
				if (viewModel != null)
				{
					viewModel.UpdatedAddInsViewModel.IsExpandedInView = true;
				}
				view.ShowDialog();
			}
		}
	}
}
