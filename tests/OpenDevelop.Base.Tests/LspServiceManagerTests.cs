using System.Reflection;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class LspServiceManagerTests
{
	[Fact]
	public void FindWorkspaceRoot_WhenTemporaryWorkspaceWasDeleted_DoesNotThrow()
	{
		var temporaryDirectory = Path.Combine(Path.GetTempPath(), "LspWorkspace-" + Guid.NewGuid().ToString("N"));
		var fileName = Path.Combine(temporaryDirectory, "Page.xaml");
		Directory.CreateDirectory(temporaryDirectory);
		Directory.Delete(temporaryDirectory);

		var method = typeof(LspServiceManager).GetMethod("FindWorkspaceRoot", BindingFlags.Static | BindingFlags.NonPublic);
		Assert.NotNull(method);
		var root = (string)method.Invoke(null, new object[] { fileName })!;

		Assert.Equal(temporaryDirectory, root);
	}
}
