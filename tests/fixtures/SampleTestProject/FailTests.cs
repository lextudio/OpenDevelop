namespace SampleTestProject;

public class FailTests
{
    [Xunit.Fact]
    public void AlwaysFails()
    {
        Xunit.Assert.Fail("intentional failure for integration test verification");
    }
}
