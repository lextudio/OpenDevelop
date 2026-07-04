namespace SampleTestProject;

public class SkipTests
{
    [Xunit.Fact(Skip = "intentionally skipped for integration test verification")]
    public void AlwaysSkipped()
    {
    }
}
