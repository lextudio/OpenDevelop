using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerDiagnosticSetTests
{
	[Fact]
	public void Distinct_CollapsesOnlyExactDiagnosticDuplicates()
	{
		var first = new DesignerDiagnostic { Severity = "Error", Message = "Cannot load MainPage", FileName = "MainPage.xaml", Line = 7, Column = 3 };
		var duplicate = new DesignerDiagnostic { Severity = "Error", Message = "Cannot load MainPage", FileName = "MainPage.xaml", Line = 7, Column = 3 };
		var differentLocation = new DesignerDiagnostic { Severity = "Error", Message = "Cannot load MainPage", FileName = "MainPage.xaml", Line = 9, Column = 3 };
		var differentSeverity = new DesignerDiagnostic { Severity = "Warning", Message = "Cannot load MainPage", FileName = "MainPage.xaml", Line = 7, Column = 3 };

		var actual = DesignerDiagnosticSet.Distinct(new[] { first, duplicate, differentLocation, differentSeverity });

		Assert.Equal(new[] { first, differentLocation, differentSeverity }, actual);
	}
}
