using ICSharpCode.SharpDevelop.Project.HotReload;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Classification of an Apply whose launch text may be unknown (nothing accepted yet and the file saved
/// after the launch): HotReloadWorkflow.ClassifyApply.
/// </summary>
public class HotReloadApplyClassificationTests
{
	const string Edited = "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{cs: 1 + 2}\" /></Window>";
	const string Launched = "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{cs: 1 + 1}\" /></Window>";

	[Fact]
	public void UnknownLaunchText_WithTheXamlSourceGenerator_TakesTheCodeDeltaPath()
		=> Assert.Equal(HotReloadChangeKind.CodeDelta, HotReloadWorkflow.ClassifyApply(null, Edited, generatesXamlCode: true));

	[Fact]
	public void UnknownLaunchText_WithoutTheGenerator_StaysAFullTextPush()
		=> Assert.Equal(HotReloadChangeKind.FullDocument, HotReloadWorkflow.ClassifyApply(null, Edited, generatesXamlCode: false));

	[Fact]
	public void KnownLaunchText_IsClassifiedAgainstIt()
		=> Assert.Equal(HotReloadChangeKind.CodeDelta, HotReloadWorkflow.ClassifyApply(Launched, Edited, generatesXamlCode: true));
}
