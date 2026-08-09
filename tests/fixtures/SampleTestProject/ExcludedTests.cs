namespace SampleTestProject;

public sealed class ExcludedTests
{
    [Xunit.Fact]
    public void NotPartOfTheBuiltTestAssembly()
    {
    }
}
