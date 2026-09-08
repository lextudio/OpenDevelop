using ICSharpCode.SharpDevelop.LanguageServices;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Covers the contract additions that make an out-of-process language service possible
/// (doc/technotes/roslyn-host-process.md §5.2, §5.3, §6). These are behaviours of the CONTRACT, so
/// they are asserted against the shipped no-op/LSP implementations rather than Roslyn: every
/// implementation has to obey them, and a new one that does not will fail here.
/// </summary>
public class LanguageServiceReadinessTests
{
    static readonly DocumentId AnyDocument = new DocumentId("/tmp/Any.cs");

    [Fact]
    public void NoOpService_ReportsUnknown_SoNothingIsEverCachedFromIt()
    {
        // The distinction that matters: a service which answers nothing must not look like a
        // service that authoritatively answered "zero". Unknown is what stops a caller caching it.
        Assert.Equal(DocumentReadiness.Unknown, NoOpLanguageService.Instance.GetDocumentReadiness(AnyDocument));
    }

    [Fact]
    public void NoOpService_HasNoWorkspaceDocumentInfo()
    {
        Assert.Null(NoOpLanguageService.Instance.GetWorkspaceDocumentInfo(AnyDocument));
    }

    [Fact]
    public async Task LensDocument_IsAlwaysAResult_NeverNull()
    {
        // Callers batch-render from this; "no anchors" must be an empty list they can iterate, not
        // a null they have to guard, and it must still carry a readiness.
        var result = await NoOpLanguageService.Instance.GetLensDocumentAsync(AnyDocument, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(result.Anchors);
        Assert.Empty(result.Anchors);
        Assert.Equal(DocumentReadiness.Unknown, result.Readiness);
    }

    [Fact]
    public async Task LensDocument_ReadinessMatchesTheService()
    {
        // One readiness for the whole batch is the correctness point of the batched API: it is what
        // stops some anchors being cached from a warm workspace and others from a cold one.
        var service = NoOpLanguageService.Instance;
        var result = await service.GetLensDocumentAsync(AnyDocument, TestContext.Current.CancellationToken);

        Assert.Equal(service.GetDocumentReadiness(AnyDocument), result.Readiness);
    }

    [Fact]
    public void StaleCodeAction_CarriesTheRejectedId_AndSaysWhatToDo()
    {
        // A stale id used to resolve to an empty edit map, so the UI applied nothing and reported
        // success. The whole point of the exception is that it is not silent.
        var exception = new StaleCodeActionException("v1|deadbeef|10:5|SomeKey");

        Assert.Equal("v1|deadbeef|10:5|SomeKey", exception.ActionId);
        Assert.Contains("document changed", exception.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaleExtractInterfaceSelection_CarriesTheRejectedMember_AndSaysWhatToDo()
    {
        var exception = new StaleExtractInterfaceException("v1|deadbeef|0|cafebabe");

        Assert.Equal("v1|deadbeef|0|cafebabe", exception.MemberId);
        Assert.Contains("document changed", exception.Message, System.StringComparison.OrdinalIgnoreCase);
    }
}
