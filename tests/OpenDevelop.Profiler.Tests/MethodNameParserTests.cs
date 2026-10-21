using OpenDevelop.Profiler.Cpu;
using Xunit;

namespace OpenDevelop.Profiler.Tests;

public class MethodNameParserTests
{
    [Theory]
    [InlineData("MyApp.Services.Worker.Run(string, int)", "MyApp.Services.Worker", "Run", 2)]
    [InlineData("MyApp.Worker.Run()", "MyApp.Worker", "Run", 0)]
    [InlineData("MyApp.Worker.Run(System.Collections.Generic.Dictionary`2[string,int], int)", "MyApp.Worker", "Run", 2)]
    [InlineData("System.Collections.Generic.List`1[System.__Canon].Add(!0)", "System.Collections.Generic.List`1", "Add", 1)]
    [InlineData("Dictionary`2[System.String,System.Int32].TryGetValue(!0, byref)", "Dictionary`2", "TryGetValue", 2)]
    [InlineData("MyApp.Worker..ctor()", "MyApp.Worker", ".ctor", 0)]
    [InlineData("MyApp.Worker..cctor()", "MyApp.Worker", ".cctor", 0)]
    public void Parse_SplitsTypeMethodAndParameterCount(string frame, string type, string method, int parameters)
    {
        var reference = MethodNameParser.Parse(frame);

        Assert.Equal(new MemberReference(type, method, parameters), reference);
    }

    [Theory]
    [InlineData("Program.<<Main>$>g__HotLeaf|0_1(int64)", "Program", "Main")]
    [InlineData("MyApp.Worker.<Run>b__3_0(object)", "MyApp.Worker", "Run")]
    [InlineData("MyApp.Worker.<>c.<Start>b__2_0()", "MyApp.Worker.<>c", "Start")]
    [InlineData("MyApp.Worker.<LoadAsync>d__5.MoveNext()", "MyApp.Worker.<LoadAsync>d__5", "MoveNext")]
    public void Parse_MapsGeneratedMethodsToTheMethodThatContainsThem(string frame, string type, string method)
    {
        var reference = MethodNameParser.Parse(frame);

        Assert.NotNull(reference);
        Assert.Equal(type, reference.TypeName);
        Assert.Equal(method, reference.MethodName);
    }

    [Fact]
    public void Parse_DoesNotTrustTheParameterCountOfAGeneratedMethod()
    {
        Assert.Null(MethodNameParser.Parse("MyApp.Worker.<Run>b__3_0(object)")!.ParameterCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NoDotHere()")]
    [InlineData("UNMANAGED_CODE_TIME")]
    [InlineData("0x7FF812345678")]
    public void Parse_ReturnsNullForNamesItCannotUse(string frame) =>
        Assert.Null(MethodNameParser.Parse(frame));
}
