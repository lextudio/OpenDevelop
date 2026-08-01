using System.Windows.Media;
using ICSharpCode.Core.Presentation;

namespace ICSharpCode.AddInManager2
{
	public static class AddInManagerImages
	{
		public static readonly ImageSource Installed = Load("AddInManager.Installed");
		public static readonly ImageSource Warning = Load("AddInManager.Warning");
		public static readonly ImageSource Search = Load("AddInManager.Search");
		public static readonly ImageSource Previous = Load("AddInManager.Previous");
		public static readonly ImageSource Next = Load("AddInManager.Next");
		public static readonly ImageSource Package = Load("AddInManager.Package");
		public static readonly ImageSource AddIn = Load("AddInManager.AddIn");
		public static readonly ImageSource Extension = Load("AddInManager.Extension");

		static ImageSource Load(string name)
		{
			return PresentationResourceService.GetImageSource(name);
		}
	}
}
