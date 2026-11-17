using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AddInManager2.Model;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.AddInManager2.ViewModel
{
	/// <summary>The installed add-ins whose repository has a newer release.</summary>
	public class UpdatedAddInsViewModel : ReleaseAddInsViewModelBase
	{
		public UpdatedAddInsViewModel()
			: base()
		{
			Initialize();
		}
		
		public UpdatedAddInsViewModel(IAddInManagerServices services)
			: base(services)
		{
			Initialize();
		}
		
		private void Initialize()
		{
			Title = SD.ResourceService.GetString("AddInManager2.Views.Updates");
		}
		
		protected override IEnumerable<AddInCatalogEntry> SelectEntries(IReadOnlyList<AddInCatalogEntry> all)
		{
			return all.Where(e => e.HasUpdate);
		}
		
		protected override int CountHighlight(IReadOnlyList<AddInCatalogEntry> all)
		{
			return all.Count(e => e.HasUpdate);
		}
	}
}
