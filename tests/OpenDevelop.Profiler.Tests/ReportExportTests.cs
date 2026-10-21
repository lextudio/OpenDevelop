using OpenDevelop.Profiler.Cpu;
using OpenDevelop.Profiler.Heap;
using Xunit;

namespace OpenDevelop.Profiler.Tests;

public class ReportExportTests
{
    static CpuProfile Profile() => new(
        [new("App.Main()", "App"), new("App.Run(int, string)", "App"), new("App.\"Quoted\"()", "App")],
        [
            new CpuSample(0, 1, SampleState.Managed, [0, 1]),
            new CpuSample(1, 1, SampleState.Managed, [0, 1]),
            new CpuSample(2, 1, SampleState.Managed, [0, 2]),
            new CpuSample(3, 1, SampleState.Managed, [0]),
        ],
        durationMs: 3);

    static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void FunctionsCsv_QuotesNamesWithCommasAndQuotesAndNamesTheUnit()
    {
        var lines = Lines(ReportExport.FunctionsCsv(Profile(), ProfileFilter.Default));

        Assert.Equal("Function,Module,Total (ms),Total (%),Self (ms),Self (%)", lines[0]);
        Assert.Contains("\"App.Run(int, string)\",App,2,50,2,50", lines);
        Assert.Contains("\"App.\"\"Quoted\"\"()\",App,1,25,1,25", lines);
        Assert.Contains("App.Main(),App,4,100,1,25", lines);
    }

    [Fact]
    public void CallTreeCsv_ListsNodesInTreeOrderWithTheirDepth()
    {
        var lines = Lines(ReportExport.CallTreeCsv(Profile(), ProfileFilter.Default));

        Assert.StartsWith("Depth,Function,Module", lines[0]);
        // The root, Main below it, then Main's heaviest callee first.
        Assert.StartsWith("1,App.Main(),App,4,100,1,25", lines[2]);
        Assert.StartsWith("2,\"App.Run(int, string)\"", lines[3]);
    }

    [Fact]
    public void AllocationProfilesAreLabelledInKilobytes()
    {
        var profile = new CpuProfile(
            [new("App.Alloc()", "App")], [new CpuSample(0, 1, SampleState.Managed, [0])], 1, sampleIntervalMs: 100, ProfileUnit.Kilobytes);

        var lines = Lines(ReportExport.FunctionsCsv(profile, ProfileFilter.Default));

        Assert.Equal("Function,Module,Total (KB),Total (%),Self (KB),Self (%)", lines[0]);
        Assert.Equal("App.Alloc(),App,100,100,100,100", lines[1]);
    }

    [Fact]
    public void HeapTypesCsv_ListsTypesLargestFirst()
    {
        var snapshot = new HeapSnapshot([new("Small", 3, 30), new("Big, generic<T>", 2, 200)]);

        var lines = Lines(ReportExport.HeapTypesCsv(snapshot));

        Assert.Equal("Type,Count,Size (bytes),Size (%),Retained size (bytes)", lines[0]);
        Assert.StartsWith("\"Big, generic<T>\",2,200,", lines[1]);
        Assert.StartsWith("Small,3,30,", lines[2]);
    }
}
