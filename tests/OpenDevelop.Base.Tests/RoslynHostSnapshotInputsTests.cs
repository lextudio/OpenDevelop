using System.Collections.Immutable;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Compiler inputs a project snapshot carries beyond sources and references, as the real Roslyn host
/// process applies them: the SDK's global usings (written by a build target the language service never
/// runs) and AdditionalFiles read by source generators.
/// </summary>
public class RoslynHostSnapshotInputsTests
{
    /// <summary>
    /// An ImplicitUsings project compiles in the host only with its global usings, which the SDK
    /// writes from a build target the language service never runs; the snapshot carries them.
    /// </summary>
    [Fact]
    public async Task GlobalUsings_FromTheSnapshot_AreInTheProjectsCompilation()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new RoslynHostProcessTransport(LocateHost(), TimeSpan.FromSeconds(60));
        var protocol = new RemoteRoslynLanguageProtocol(transport);
        var directory = Directory.CreateTempSubdirectory("RoslynGlobalUsings-");
        try
        {
            async Task<IReadOnlyList<string>> Diagnostics(string name, string[] globalUsings)
            {
                var path = Path.Combine(directory.FullName, name, "Program.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "class Program { static void Main() { Console.WriteLine(new Builder().Append(Max(1, 2))); } }", token);
                await protocol.RoslynProjectLoadAsync(new LanguageServiceProjectSnapshot(
                    Path.Combine(directory.FullName, name, name + ".csproj"), "C#", new[] { path }, PlatformReferences(),
                    Array.Empty<string>(), Array.Empty<string>(), null, null) { GlobalUsings = globalUsings }, token);
                return await DiagnosticSampleAsync(protocol, path, token);
            }

            var without = await Diagnostics("Without", Array.Empty<string>());
            Assert.Contains(without, diagnostic => diagnostic.Contains("CS0103") || diagnostic.Contains("CS0246"));
            var with = await Diagnostics("With", new[] { "global::System", "static global::System.Math", "Builder = global::System.Text.StringBuilder" });
            Assert.DoesNotContain(with, diagnostic => diagnostic.Contains("CS0103") || diagnostic.Contains("CS0246"));
        }
        finally { directory.Delete(true); }
    }

    /// <summary>
    /// A source generator in the snapshot's analyzers sees the snapshot's AdditionalFiles (the XAML source
    /// generator reads XAML this way): a generator built here emits one type per additional file, and the
    /// project only compiles when that type exists.
    /// </summary>
    [Fact]
    public async Task AdditionalFiles_FromTheSnapshot_ReachTheProjectsSourceGenerators()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new RoslynHostProcessTransport(LocateHost(), TimeSpan.FromSeconds(60));
        var protocol = new RemoteRoslynLanguageProtocol(transport);
        var directory = Directory.CreateTempSubdirectory("RoslynAdditionalFiles-");
        try
        {
            var generator = BuildAdditionalFileGenerator(directory.FullName);
            var additional = Path.Combine(directory.FullName, "Greeting.txt");
            await File.WriteAllTextAsync(additional, "hello", token);

            async Task<IReadOnlyList<string>> Diagnostics(string name, string[] additionalFiles)
            {
                var path = Path.Combine(directory.FullName, name, "Program.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "class Program { string Value = Generated_Greeting.Text; }", token);
                await protocol.RoslynProjectLoadAsync(new LanguageServiceProjectSnapshot(
                    Path.Combine(directory.FullName, name, name + ".csproj"), "C#", new[] { path }, PlatformReferences(),
                    Array.Empty<string>(), Array.Empty<string>(), null, null, null, new[] { generator }, additionalFiles), token);
                return await DiagnosticSampleAsync(protocol, path, token);
            }

            var without = await Diagnostics("Without", Array.Empty<string>());
            Assert.Contains(without, diagnostic => diagnostic.Contains("CS0103") && diagnostic.Contains("Generated_Greeting"));
            var with = await Diagnostics("With", new[] { additional });
            Assert.DoesNotContain(with, diagnostic => diagnostic.Contains("Generated_Greeting"));
        }
        finally {
            // The host process has the generator DLL loaded; Windows refuses to delete a loaded file.
            transport.Dispose();
            directory.Delete(true);
        }
    }

    static string BuildAdditionalFileGenerator(string directory)
    {
        const string source = """
            using System.IO;
            using Microsoft.CodeAnalysis;
            [Generator]
            public sealed class AdditionalFileGenerator : IIncrementalGenerator
            {
                public void Initialize(IncrementalGeneratorInitializationContext context) =>
                    context.RegisterSourceOutput(context.AdditionalTextsProvider, static (output, file) =>
                    {
                        var name = Path.GetFileNameWithoutExtension(file.Path);
                        var text = file.GetText()?.ToString() ?? "";
                        output.AddSource("Generated_" + name + ".g.cs",
                            "static class Generated_" + name + " { public const string Text = @\"" + text.Replace("\"", "\"\"") + "\"; }");
                    });
            }
            """;
        var references = PlatformReferences().Append(typeof(IIncrementalGenerator).Assembly.Location)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("AdditionalFileGenerator", new[] { CSharpSyntaxTree.ParseText(source) },
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var path = Path.Combine(directory, "AdditionalFileGenerator.dll");
        var emitted = compilation.Emit(path);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return path;
    }

    static async Task<IReadOnlyList<string>> DiagnosticSampleAsync(RemoteRoslynLanguageProtocol protocol, string path, CancellationToken token)
    {
        var status = await protocol.RoslynDocumentStatusAsync(new TextDocumentIdentifier(path), token, includeDiagnostics: true);
        return status!.DiagnosticSample ?? Array.Empty<string>();
    }

    static string[] PlatformReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Console.dll"
                or "netstandard.dll" or "System.Collections.Immutable.dll" or "System.Text.Encoding.Extensions.dll")
            .ToArray();

    static string LocateHost()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "RoslynHost", "ICSharpCode.Roslyn.Host.dll");
        Assert.True(File.Exists(path), "The test build did not prepare its RoslynHost dependency: " + path);
        return path;
    }
}
