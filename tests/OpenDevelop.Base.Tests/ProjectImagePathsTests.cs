using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class ProjectImagePathsTests
{
	[Fact]
	public void ImagesAreRelativeToTheXamlFolderWithForwardSlashes()
	{
		var root = Path.Combine(Path.GetTempPath(), "od-images");
		string In(params string[] parts) => Path.Combine(new[] { root }.Concat(parts).ToArray());
		var paths = ProjectImagePaths.RelativeTo(In("Views", "Main.xaml"), new[] {
			In("Views", "icon.PNG"), In("Assets", "logo.png"), In("Views", "Parts", "a.jpg"),
			In("Views", "Main.xaml"), In("readme.txt"), In("Assets", "logo.png"), ""
		});
		Assert.Equal(new[] { "../Assets/logo.png", "Parts/a.jpg", "icon.PNG" }, paths);
	}
}
