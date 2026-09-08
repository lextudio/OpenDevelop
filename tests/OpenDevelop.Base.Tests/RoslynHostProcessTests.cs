using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// End-to-end coverage of the out-of-process host: a real child process, real TCP transport, real
/// StreamJsonRpc (doc/technotes/roslyn-host-process.md Phase 3).
///
/// The loopback tests next door prove the client and dispatcher agree on names and shapes; only
/// this one proves the host actually starts, completes the handshake, serialises a reply and shuts
/// down. Those are separate failures - a protocol can be perfectly symmetrical and still never
/// connect - so both layers are worth having.
///
/// The test project builds and deploys its host dependency into an isolated output subdirectory.
/// </summary>
public class RoslynHostProcessTests
{
    [Fact]
    public async Task DocumentStatus_ReturnsRealReferencesAndOptInDiagnosticSamples()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60));
        var protocol = new RemoteRoslynLanguageProtocol(transport);
        var directory = Directory.CreateTempSubdirectory("RoslynDiagnostics-");
        try
        {
            var path = Path.Combine(directory.FullName, "Broken.cs");
            await File.WriteAllTextAsync(path, "class Broken { MissingType value; }", token);
            await protocol.RoslynProjectLoadAsync(new LanguageServiceProjectSnapshot(
                Path.Combine(directory.FullName, "Broken.csproj"), "C#", new[] { path },
                new[] { typeof(object).Assembly.Location }, Array.Empty<string>(), Array.Empty<string>(), null, null), token);
            var document = new TextDocumentIdentifier(path);
            var cheap = await protocol.RoslynDocumentStatusAsync(document, token);
            Assert.Equal(1, cheap!.MetadataReferenceCount);
            Assert.Null(cheap.DiagnosticSample);
            var detailed = await protocol.RoslynDocumentStatusAsync(document, token, includeDiagnostics: true);
            Assert.Equal(1, detailed!.MetadataReferenceCount);
            Assert.True(detailed.TrackedProjectCount > 0);
            Assert.Contains(detailed.DiagnosticSample!, diagnostic => diagnostic.Contains("CS0246") && diagnostic.Contains("Broken.cs"));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("class Outer { ctor }", "Outer")]
    [InlineData("class Outer { class Inner { ctor } }", "Inner")]
    public async Task ContainingType_ResolvesUnexpandedSnippetTrigger(string source, string expected)
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60));
        var protocol = new RemoteRoslynLanguageProtocol(transport);
        var document = new TextDocumentIdentifier(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"));
        await protocol.TextDocumentDidChangeAsync(document, source, token);
        var result = await protocol.RoslynContainingTypeAsync(document, source.IndexOf("ctor", StringComparison.Ordinal) + 4, token);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public async Task ProjectGraph_ReconcilesReferencesLoadedLaterAndReportsIncompleteDependencies()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60));
        var protocol = new RemoteRoslynLanguageProtocol(transport);
        var directory = Directory.CreateTempSubdirectory("RoslynGraph-");
        try
        {
            var caller = Path.Combine(directory.FullName, "Caller.cs");
            var target = Path.Combine(directory.FullName, "Target.cs");
            var callerProject = Path.Combine(directory.FullName, "Caller.csproj");
            var targetProject = Path.Combine(directory.FullName, "Target.csproj");
            const string source = "class Caller { void Run() { Target.Execute(); } }";
            await File.WriteAllTextAsync(caller, source, token);
            await File.WriteAllTextAsync(target, "public class Target { public static void Execute() {} }", token);
            var references = new[] { typeof(object).Assembly.Location };
            await protocol.RoslynProjectLoadAsync(new LanguageServiceProjectSnapshot(callerProject, "C#",
                new[] { caller }, references, new[] { targetProject }, Array.Empty<string>(), null, null), token);
            var document = new TextDocumentIdentifier(caller);
            Assert.Equal(DocumentReadiness.Loading, (await protocol.RoslynDocumentStatusAsync(document, token))!.Readiness);

            await protocol.RoslynProjectLoadAsync(new LanguageServiceProjectSnapshot(targetProject, "C#",
                new[] { target }, references, Array.Empty<string>(), Array.Empty<string>(), null, null), token);
            var result = await protocol.TextDocumentDefinitionAsync(document, source.IndexOf("Execute", StringComparison.Ordinal), token);
            Assert.Equal(DocumentReadiness.Ready, result.Readiness);
            Assert.Contains(result.Value, location => location.FileName == target);

            var emptyProject = Path.Combine(directory.FullName, "Empty.csproj");
            await protocol.RoslynProjectLoadAsync(new LanguageServiceProjectSnapshot(emptyProject, "C#",
                Array.Empty<string>(), references, Array.Empty<string>(), Array.Empty<string>(), null, null), token);
            var status = await protocol.RoslynStatusAsync(token);
            Assert.Equal(3, status.Projects.Count);
            Assert.All(status.Projects, project => Assert.Equal(DocumentReadiness.Ready, project.Readiness));
            await protocol.RoslynSolutionClosedAsync(token);
            Assert.Empty((await protocol.RoslynStatusAsync(token)).Projects);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ExplicitFrameworkQueries_SelectTheirOwnSliceWithoutChangingTheActiveSlice()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60));
        var protocol = new RemoteRoslynLanguageProtocol(transport);
        var directory = Directory.CreateTempSubdirectory("RoslynTfm-");
        try
        {
            var path = Path.Combine(directory.FullName, "Sample.cs");
            const string source = "#if FIRST\nclass First {}\n#else\nclass Second {}\n#endif";
            await File.WriteAllTextAsync(path, source, token);
            foreach (var tfm in new[] { "first", "second" })
                await protocol.RoslynProjectLoadAsync(new LanguageServiceProjectSnapshot(
                    Path.Combine(directory.FullName, "Sample.csproj"), "C#", new[] { path },
                    new[] { typeof(object).Assembly.Location }, Array.Empty<string>(),
                    tfm == "first" ? new[] { "FIRST" } : Array.Empty<string>(), null, null, tfm), token);

            foreach (var tfm in new[] { "second", "first", "second" })
            {
                var result = await protocol.TextDocumentSymbolAsync(new TextDocumentIdentifier(path, tfm), token);
                Assert.Equal(DocumentReadiness.Ready, result.Readiness);
                Assert.Equal(tfm == "first" ? "First" : "Second", Assert.Single(result.Value).Name);
            }
            var active = await protocol.TextDocumentSymbolAsync(new TextDocumentIdentifier(path), token);
            Assert.Equal("First", Assert.Single(active.Value).Name);
            await protocol.TextDocumentDidChangeAsync(new TextDocumentIdentifier(path, "second"),
                source.Replace("First", "UnsavedFirst").Replace("Second", "UnsavedSecond"), token);
            var unknown = await protocol.TextDocumentSymbolAsync(new TextDocumentIdentifier(path, "missing"), token);
            Assert.Equal(DocumentReadiness.Unknown, unknown.Readiness);
            Assert.Empty(unknown.Value);
            var unsaved = await protocol.TextDocumentSymbolAsync(new TextDocumentIdentifier(path, "first"), token);
            Assert.Equal("UnsavedFirst", Assert.Single(unsaved.Value).Name);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task RemoteService_UsesOneWorkspaceForEditorAndProtocolConsumers()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60));
        var protocol = new RemoteRoslynLanguageProtocol(transport);
        using var service = new RemoteLanguageService(protocol, (_, _) => Task.CompletedTask);
        var registry = new LanguageServiceRegistry();
        using var registration = registry.RegisterExtension(".cs", service);
        Assert.Same(service, registry.GetService("Sample.cs"));
        Assert.True(registry.TryGetProtocol("Sample.cs", out var registeredProtocol));
        Assert.Same(protocol, registeredProtocol);
        var directory = Directory.CreateTempSubdirectory("RemoteService-");
        try
        {
            var path = Path.Combine(directory.FullName, "Sample.cs");
            var helper = Path.Combine(directory.FullName, "Helper.cs");
            const string source = "class Sample { void Run() { Helper.Target(); } } // Target";
            await File.WriteAllTextAsync(path, source, token);
            await File.WriteAllTextAsync(helper, "class Helper { public static void Target() {} }", token);
            await protocol.RoslynProjectLoadAsync(new LanguageServiceProjectSnapshot(
                Path.Combine(directory.FullName, "Sample.csproj"), "C#", new[] { path, helper },
                new[] { typeof(object).Assembly.Location }, Array.Empty<string>(), Array.Empty<string>(), null, null), token);
            var id = new DocumentId(path);
            await service.UpsertDocumentAsync(id, source, token);
            var revisionAfterOpen = service.GetWorkspaceRevision();
            Assert.True(revisionAfterOpen > 0);
            Assert.Equal(DocumentReadiness.Ready, service.GetDocumentReadiness(id));
            Assert.Contains(helper, service.GetWorkspaceDocumentInfo(id)!.SiblingFilePaths);
            var offset = source.IndexOf("Target", StringComparison.Ordinal);
            Assert.Equal("Target", await service.GetSymbolNameAsync(id, offset, token));
            Assert.NotNull(await service.GetSymbolKindAsync(id, offset, token));
            Assert.NotEmpty(await service.GetSemanticTokensAsync(id, token));
            Assert.Contains(await service.GoToDefinitionAsync(id, offset, token), target => target.FileName == helper);
            Assert.False(await service.IsValidIdentifierAsync(id, "not valid", token));
            Assert.NotEmpty((await service.GetLensDocumentAsync(id, token)).Anchors);
            var plainRename = await service.RenameSymbolAsync(id, offset, "Updated", token);
            var commentRename = await service.RenameSymbolAsync(id, offset, "Updated", token, renameInComments: true);
            static string ApplyEdits(string text, IReadOnlyList<TextEdit> edits)
            {
                foreach (var edit in edits.OrderByDescending(edit => edit.Span.Start.Column))
                    text = text.Substring(0, edit.Span.Start.Column - 1) + edit.NewText + text.Substring(edit.Span.End.Column - 1);
                return text;
            }
            Assert.EndsWith("// Target", ApplyEdits(source, plainRename[path]));
            Assert.EndsWith("// Updated", ApplyEdits(source, commentRename[path]));

            // The synchronous editor notification queues a full-text update; the next query
            // must observe it, including when the source of truth has never been saved.
            int name = source.IndexOf("Sample", StringComparison.Ordinal);
            service.OnTextChanged(id, new TextChange(new TextSpan(new TextPosition(1, name + 1), new TextPosition(1, name + 7)), "Renamed"));
            Assert.Equal("Renamed", await service.GetSymbolNameAsync(id, name, token));
            Assert.True(service.GetWorkspaceRevision() > revisionAfterOpen);
            await service.CloseSolutionAsync(token);
            Assert.Null(service.GetWorkspaceDocumentInfo(id));
            Assert.Equal(DocumentReadiness.Unknown, service.GetDocumentReadiness(id));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task CodeAction_CanBeAppliedAfterHostReplacement()
    {
        var token = TestContext.Current.CancellationToken;
        var document = new TextDocumentIdentifier(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"));
        const string source = "class Sample { void Run() { Missing(); } }";
        string actionId;
        using (var first = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60)))
        {
            var protocol = new RemoteRoslynLanguageProtocol(first);
            await protocol.TextDocumentDidChangeAsync(document, source, token);
            var actions = await protocol.TextDocumentCodeActionAsync(document,
                new TextSpan(new TextPosition(1, 1), new TextPosition(1, source.Length + 1)), token);
            Assert.NotEmpty(actions.Value);
            actionId = actions.Value.First(a => a.Title.Contains("Generate method", StringComparison.OrdinalIgnoreCase)).Id;
        }
        using var replacement = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60));
        var restored = new RemoteRoslynLanguageProtocol(replacement);
        await restored.TextDocumentDidChangeAsync(document, source, token);
        var edits = await restored.CodeActionApplyAsync(document, actionId, token);
        Assert.NotEmpty(edits.Value);
        Assert.Contains(edits.Value.SelectMany(pair => pair.Value), edit => edit.NewText.Contains("Missing", StringComparison.Ordinal));
        await restored.TextDocumentDidChangeAsync(document, source + " // edited", token);
        await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() => restored.CodeActionApplyAsync(document, actionId, token));
    }

    [Fact]
    public async Task Recovery_BusinessErrorPreservesTheRunningWorkspace()
    {
        RoslynHostProcessTransport? child = null;
        var starts = 0;
        using var recovery = new RecoveringRoslynTransport(() => {
            starts++;
            return child = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60));
        });
        var protocol = new RemoteRoslynLanguageProtocol(recovery);
        var token = TestContext.Current.CancellationToken;
        var document = new TextDocumentIdentifier(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"));
        const string source = "class Sample { void Target() {} void Caller() { Target(); } }";
        await protocol.TextDocumentDidChangeAsync(document, source, token);
        var first = child!;
        var pid = first.ProcessId;

        await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() =>
            protocol.CodeActionApplyAsync(document, "invalid-action", token));

        Assert.True(first.IsAlive);
        var definition = await protocol.TextDocumentDefinitionAsync(
            document, source.LastIndexOf("Target", StringComparison.Ordinal), token);
        Assert.NotEmpty(definition.Value);
        Assert.Same(first, child);
        Assert.Equal(pid, child!.ProcessId);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Recovery_ReplaysUnsavedText_AndCloseEndsTheHostLifetime()
    {
        RoslynHostProcessTransport? child = null;
        using var recovery = new RecoveringRoslynTransport(() =>
            child = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60)));
        var protocol = new RemoteRoslynLanguageProtocol(recovery);
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("RoslynRecovery-");
        try
        {
            var file = Path.Combine(directory.FullName, "Sample.cs");
            await File.WriteAllTextAsync(file, "class OnDisk { }", token);
            var document = new TextDocumentIdentifier(file);
            var snapshot = new LanguageServiceProjectSnapshot(Path.Combine(directory.FullName, "Sample.csproj"),
                "C#", new[] { file }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, null);
            await protocol.RoslynProjectLoadAsync(snapshot, token);
            const string unsaved = "class Unsaved { void Target() {} void Caller() { Target(); } }";
            await protocol.TextDocumentDidChangeAsync(document, unsaved, token);
            var first = child!;
            using (var process = System.Diagnostics.Process.GetProcessById(first.ProcessId))
            {
                first.TerminateHost();
                await process.WaitForExitAsync(token);
            }
            var result = await protocol.TextDocumentDefinitionAsync(document, unsaved.LastIndexOf("Target", StringComparison.Ordinal), token);
            Assert.NotSame(first, child);
            Assert.Equal(DocumentReadiness.Ready, result.Readiness);
            Assert.NotEmpty(result.Value);
            var restored = child!;
            await protocol.RoslynSolutionClosedAsync(token);
            Assert.False(restored.IsAlive);
            Assert.Empty((await protocol.RoslynStatusAsync(token)).Projects);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ConcurrentFirstRequests_AwaitTheSameHandshake()
    {
        using var transport = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60));
        var protocol = new RemoteRoslynLanguageProtocol(transport);
        var replies = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ => {
            var status = await protocol.RoslynStatusAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(status);
            return transport.ProcessId;
        }));
        Assert.All(replies, pid => Assert.True(pid > 0));
        Assert.Single(replies.Distinct());
        Assert.Single(transport.ChildLog.Split('\n').Where(line => line.Contains("ROSLYN-HOST-READY")));
    }

    [Fact]
    public async Task ExtractInterface_SelectionSurvivesHostReplacement_AndRejectsChangedText()
    {
        var token = TestContext.Current.CancellationToken;
        var document = new TextDocumentIdentifier(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"));
        const string text = "public class Sample { public void Run() { } }";
        var offset = text.IndexOf("Sample", StringComparison.Ordinal);
        string[] memberIds;
        int originalProcessId;
        using (var original = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60)))
        {
            var protocol = new RemoteRoslynLanguageProtocol(original);
            await protocol.TextDocumentDidChangeAsync(document, text, token);
            var info = await protocol.RoslynExtractInterfaceInfoAsync(document, offset, token);
            Assert.NotNull(info.Value);
            memberIds = info.Value.Members.Select(member => member.Id).ToArray();
            Assert.Single(memberIds);
            originalProcessId = original.ProcessId;
        }

        using var replacement = new RoslynHostProcessTransport(TryLocateHost(), TimeSpan.FromSeconds(60));
        var restored = new RemoteRoslynLanguageProtocol(replacement);
        await restored.TextDocumentDidChangeAsync(document, text, token);
        Assert.NotEqual(originalProcessId, replacement.ProcessId);
        // No info request on the replacement host: the original selection must be re-derived.
        var applied = await restored.RoslynExtractInterfaceApplyAsync(
            document, offset, "ISample", memberIds, true, false, token);
        Assert.NotNull(applied.Value);
        Assert.Contains("void Run()", applied.Value.InterfaceFileContent);
        Assert.True(applied.Value.Edits.ContainsKey(document.Uri));

        await restored.TextDocumentDidChangeAsync(document, text.Replace("Run", "Walk"), token);
        var failure = await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() =>
            restored.RoslynExtractInterfaceApplyAsync(document, offset, "ISample", memberIds, true, false, token));
        Assert.Contains("document changed", failure.Message);
        await restored.TextDocumentDidChangeAsync(document, "// Class removed", token);
        var removedClassFailure = await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() =>
            restored.RoslynExtractInterfaceApplyAsync(document, offset, "ISample", memberIds, true, false, token));
        Assert.Contains("document changed", removedClassFailure.Message);
    }

    /// <summary>
    /// Resolve only the host prepared by this test build, including its matching dependencies.
    /// </summary>
    static string TryLocateHost()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "RoslynHost", "ICSharpCode.Roslyn.Host.dll");
        Assert.True(File.Exists(path), "The test build did not prepare its RoslynHost dependency: " + path);
        return path;
    }

    [Fact]
    public async Task Host_Starts_AnswersStatus_AndStops()
    {
        var hostPath = TryLocateHost();

        using var transport = new RoslynHostProcessTransport(hostPath!, TimeSpan.FromSeconds(60));
        var protocol = new RemoteRoslynLanguageProtocol(transport);
        var token = TestContext.Current.CancellationToken;

        // roslyn/status is the right first call: it is what a client polls while the host warms up,
        // so it must answer before any document or project has been pushed.
        var status = await protocol.RoslynStatusAsync(token);

        Assert.NotNull(status);
        Assert.NotNull(status.Projects);
        Assert.True(transport.IsAlive, "The host exited while answering. Child log:" + Environment.NewLine + transport.ChildLog);
        Assert.True(transport.ProcessId > 0);
    }

    [Fact]
    public async Task Host_LoadsProjectSnapshot_AndServesDocumentDefinition()
    {
        var hostPath = TryLocateHost();

        using var transport = new RoslynHostProcessTransport(hostPath!, TimeSpan.FromSeconds(60));
        var protocol = new RemoteRoslynLanguageProtocol(transport);
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "OpenDevelop-RoslynHost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var fileName = Path.Combine(directory, "RoundTrip.cs");
            var projectFileName = Path.Combine(directory, "RoundTrip.csproj");
            var text = "class RoundTrip { void Target() { } void Caller() { Target(); } }";
            await File.WriteAllTextAsync(fileName, text, token);
            var document = new TextDocumentIdentifier(fileName, "net10.0");
            var snapshot = new LanguageServiceProjectSnapshot(
                projectFileName, "C#", new[] { fileName }, Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), null, null, "net10.0");

            await protocol.RoslynProjectLoadAsync(snapshot, token);
            await protocol.TextDocumentDidChangeAsync(document, text, token);
            var status = await protocol.RoslynStatusAsync(token);
            var definition = await protocol.TextDocumentDefinitionAsync(document, text.LastIndexOf("Target", StringComparison.Ordinal), token);

            Assert.Contains(status.Projects, project => project.ProjectFilePath == projectFileName && project.Readiness == DocumentReadiness.Ready);
            Assert.NotEmpty(definition.Value);
            Assert.Equal(DocumentReadiness.Ready, definition.Readiness);
            Assert.True(transport.IsAlive, "The host exited while serving a document. Child log:" + Environment.NewLine + transport.ChildLog);

            await protocol.RoslynSolutionClosedAsync(token);
            Assert.Empty((await protocol.RoslynStatusAsync(token)).Projects);

            await File.WriteAllTextAsync(Path.Combine(directory, "Strings.resx"),
                "<root><data name=\"Greeting\"><value>Hello</value></data></root>", token);
            var resourceText = "class Resources { void M() { manager.GetString(\"\"); } }";
            var looseDocument = document with { TargetFramework = null };
            await protocol.TextDocumentDidChangeAsync(looseDocument, resourceText, token);
            var completion = await protocol.TextDocumentCompletionAsync(looseDocument,
                resourceText.IndexOf("\"\"", StringComparison.Ordinal) + 1, token);
            Assert.Contains(completion.Value.Items, item => item.DisplayText == "Greeting");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
