namespace SampleTestProject;

public class SlowTests
{
    [Xunit.Fact]
    public async Task FinishesLast()
    {
        await Task.Delay(5000);
    }
}
