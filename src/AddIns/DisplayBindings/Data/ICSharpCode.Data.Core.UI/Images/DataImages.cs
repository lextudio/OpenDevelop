using System.Windows.Media;
using ICSharpCode.Core.Presentation;

namespace ICSharpCode.Data.Core.UI
{
	public static class DataImages
	{
		public static readonly ImageSource Error = Load("Data.Error");
		public static readonly ImageSource Warning = Load("Data.Warning");
		public static readonly ImageSource Refresh = Load("Data.Refresh");
		public static readonly ImageSource Database = Load("Data.Database");
		public static readonly ImageSource ConnectToDatabase = Load("Data.ConnectToDatabase");
		public static readonly ImageSource Table = Load("Data.Table");
		public static readonly ImageSource Column = Load("Data.Column");
		public static readonly ImageSource ForeignKey = Load("Data.ForeignKey");
		public static readonly ImageSource Key = Load("Data.Key");
		public static readonly ImageSource StoredProcedure = Load("Data.StoredProcedure");

		static ImageSource Load(string name)
		{
			return PresentationResourceService.GetImageSource(name);
		}
	}
}
