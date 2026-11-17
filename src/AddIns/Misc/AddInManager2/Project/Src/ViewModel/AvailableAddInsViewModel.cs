using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AddInManager2.Model;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.AddInManager2.ViewModel
{
	/// <summary>The add-ins of the trusted repositories that can be installed or updated.</summary>
	public class AvailableAddInsViewModel : ReleaseAddInsViewModelBase
	{
		public AvailableAddInsViewModel()
			: base()
		{
			Initialize();
		}
		
		public AvailableAddInsViewModel(IAddInManagerServices services)
			: base(services)
		{
			Initialize();
		}
		
		private void Initialize()
		{
			Title = SD.ResourceService.GetString("AddInManager2.Views.Available");
		}
		
		protected override IEnumerable<AddInCatalogEntry> SelectEntries(IReadOnlyList<AddInCatalogEntry> all)
		{
			return all;
		}
	}
}
