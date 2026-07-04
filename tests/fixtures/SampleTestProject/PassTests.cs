namespace SampleTestProject;

public class PassTests
{
    [Xunit.Fact]
    public void AlwaysPasses()
    {
        Xunit.Assert.Equal(4, 2 + 2);
    }
}
