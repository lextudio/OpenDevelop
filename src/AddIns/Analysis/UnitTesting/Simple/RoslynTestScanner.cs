using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ICSharpCode.UnitTesting.Simple;

// A candidate test found by a syntax-only scan, before MTP confirms it exists and gives it a real
// Uid. See doc/technotes/unit-testing.md "Open idea: Roslyn-assisted discovery" for why this
// exists: MTP discovery is authoritative but requires a build + a ~30-60s test-host round trip;
// this gives an approximate answer in milliseconds so a caller isn't stuck looking at "no tests
// yet" for the entire round trip.
internal sealed record RoslynTestCandidate(string TypeFullName, string MethodName, string DisplayName);

// Syntax-tree-only (no semantic model, no compilation, no build) scan for attribute-decorated test
// methods. Deliberately approximate - it cannot expand a parameterized [Theory]/[TestCase] into its
// real per-data-row count, cannot produce the MTP Uid needed to run just one test, and can miss or
// over-report anything that depends on runtime/attribute-inheritance semantics a syntax walk can't
// see. It exists purely to seed TestService's cache fast; MtpDiscoveredTest/MTP confirmation is
// still the source of truth and replaces these candidates once it completes (TestService.cs).
internal static class RoslynTestScanner
{
    // Short attribute names (as they can appear in source, with or without the "Attribute" suffix)
    // across the test frameworks TestProjectDetector's package markers already recognize (xunit,
    // NUnit, MSTest, TUnit). Matching by bare name only (no semantic/using-alias resolution) is
    // exactly the same tradeoff as the rest of this scan: fast, approximate, good enough to seed a
    // cache that MTP will confirm or correct moments later.
    private static readonly HashSet<string> TestMethodAttributeNames = new(StringComparer.Ordinal)
    {
        "Fact", "FactAttribute",
        "Theory", "TheoryAttribute",
        "Test", "TestAttribute",
        "TestCase", "TestCaseAttribute",
        "TestCaseSource", "TestCaseSourceAttribute",
        "TestMethod", "TestMethodAttribute",
        "DataTestMethod", "DataTestMethodAttribute",
    };

    public static IReadOnlyList<RoslynTestCandidate> ScanProject(string? projectDirectory)
    {
        var results = new List<RoslynTestCandidate>();
        if (string.IsNullOrEmpty(projectDirectory) || !Directory.Exists(projectDirectory))
            return results;

        IEnumerable<string> sourceFiles;
        try
        {
            sourceFiles = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsInBuildOutputDirectory(path));
        }
        catch
        {
            return results;
        }

        foreach (var file in sourceFiles)
        {
            try
            {
                ScanFile(file, results);
            }
            catch
            {
                // Best-effort: a single unparsable file (e.g. mid-edit, encoding issue) shouldn't
                // abort the whole approximate scan - MTP confirmation will catch anything missed.
            }
        }

        return results;
    }

    private static bool IsInBuildOutputDirectory(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj");

    private static void ScanFile(string filePath, List<RoslynTestCandidate> results)
    {
        var text = File.ReadAllText(filePath);
        var tree = CSharpSyntaxTree.ParseText(text, path: filePath);
        var root = tree.GetRoot();

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var typeFullName = BuildTypeFullName(classDecl);

            foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!HasTestAttribute(method))
                    continue;

                var methodName = method.Identifier.Text;
                results.Add(new RoslynTestCandidate(typeFullName, methodName, $"{typeFullName}.{methodName}"));
            }
        }
    }

    private static bool HasTestAttribute(MethodDeclarationSyntax method)
        => method.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attribute => TestMethodAttributeNames.Contains(GetAttributeName(attribute)));

    private static string GetAttributeName(AttributeSyntax attribute)
        => attribute.Name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            SimpleNameSyntax simple => simple.Identifier.Text,
            _ => attribute.Name.ToString(),
        };

    private static string BuildTypeFullName(ClassDeclarationSyntax classDecl)
    {
        var parts = new List<string> { classDecl.Identifier.Text };
        for (var current = classDecl.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case ClassDeclarationSyntax outerClass:
                    parts.Add(outerClass.Identifier.Text);
                    break;
                case NamespaceDeclarationSyntax ns:
                    parts.Add(ns.Name.ToString());
                    break;
                case FileScopedNamespaceDeclarationSyntax fileNs:
                    parts.Add(fileNs.Name.ToString());
                    break;
            }
        }
        parts.Reverse();
        return string.Join(".", parts);
    }
}
