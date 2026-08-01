using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace ICSharpCode.ClassDiagram.Tests;

[TestFixture]
public sealed class ClassDiagramDocumentTests
{
    string directory;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "OpenDevelop-ClassDiagram-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Test]
    public void DiscoversTypesMembersAndRelationships()
    {
        var source = WriteSource("Model.cs", """
            using System.Collections.Generic;
            namespace Sample;
            interface IService { }
            class Part { }
            class Engine : IService
            {
                readonly Part owned = new Part();
                public List<Part> Parts { get; } = new();
                public Part Find(IService service) => owned;
            }
            """);

        var document = ClassDiagramDocument.Create(new[] { source });

        Assert.That(document.Types.Select(type => type.Name), Is.EquivalentTo(new[] { "Engine", "IService", "Part" }));
        var engine = document.Types.Single(type => type.Name == "Engine");
        Assert.That(engine.BaseTypes, Does.Contain("IService"));
        Assert.That(engine.Members.Any(member => member.Kind == ClassDiagramMemberKind.Field), Is.True);
        Assert.That(engine.Members.Any(member => member.Kind == ClassDiagramMemberKind.Property), Is.True);
        Assert.That(engine.Members.Any(member => member.Kind == ClassDiagramMemberKind.Method), Is.True);
        Assert.That(document.Relationships, Has.Some.Matches<ClassDiagramRelationship>(relationship =>
            relationship.SourceType == "Sample.Engine" && relationship.TargetType == "Sample.Part"
            && relationship.Kind == ClassDiagramRelationshipKind.Composition));
        Assert.That(document.Relationships, Has.Some.Matches<ClassDiagramRelationship>(relationship =>
            relationship.SourceType == "Sample.Engine" && relationship.TargetType == "Sample.Part"
            && relationship.Kind == ClassDiagramRelationshipKind.Aggregation));
    }

    [Test]
    public void SemanticRelationshipsDistinguishTypesWithTheSameSimpleName()
    {
        var source = WriteSource("Names.cs", """
            namespace A { class Widget { } }
            namespace B { class Widget { } }
            namespace Model { class Owner { A.Widget Value { get; set; } } }
            """);

        var document = ClassDiagramDocument.Create(new[] { source });

        Assert.That(document.Relationships, Has.Some.Matches<ClassDiagramRelationship>(relationship =>
            relationship.SourceType == "Model.Owner" && relationship.TargetType == "A.Widget"));
        Assert.That(document.Relationships, Has.None.Matches<ClassDiagramRelationship>(relationship =>
            relationship.SourceType == "Model.Owner" && relationship.TargetType == "B.Widget"));
    }

    [Test]
    public void SemanticRelationshipsResolveGenericAndArrayElementTypes()
    {
        var source = WriteSource("Collections.cs", """
            using System.Collections.Generic;
            namespace Model;
            class Part { }
            class Owner { List<Part> Parts { get; } = new(); Part[] More { get; set; } }
            """);

        var document = ClassDiagramDocument.Create(new[] { source });

        Assert.That(document.Relationships, Has.Some.Matches<ClassDiagramRelationship>(relationship =>
            relationship.SourceType == "Model.Owner" && relationship.TargetType == "Model.Part"
            && relationship.Kind == ClassDiagramRelationshipKind.Aggregation));
    }

    [Test]
    public void CreateAsyncHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.CatchAsync(async () =>
            await ClassDiagramDocument.CreateAsync(Array.Empty<string>(), cancellation.Token));
        Assert.That(exception, Is.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task BackgroundRefreshUsesLatestSourceAndPreservesUserState()
    {
        var source = WriteSource("Refresh.cs", "namespace Model; class Widget { }");
        var previous = ClassDiagramDocument.Create(new[] { source });
        var oldType = previous.Types.Single();
        previous.NodeStates[ClassDiagramDocument.GetNodeId(oldType)].X = 321;
        File.WriteAllText(source, "namespace Model; class Widget { public int Value { get; set; } }");

        var refreshed = await ClassDiagramDocument.CreateAsync(new[] { source }, CancellationToken.None);
        refreshed.CopyUserStateFrom(previous);

        var newType = refreshed.Types.Single();
        Assert.That(newType.Members, Has.Some.Matches<ClassDiagramMember>(member => member.DisplayText.Contains("Value")));
        Assert.That(refreshed.NodeStates[ClassDiagramDocument.GetNodeId(newType)].X, Is.EqualTo(321));
    }

    [Test]
    public void RoundTripsLayoutAndMemberGroupState()
    {
        var source = WriteSource("Widget.cs", "class Widget { public int Value { get; set; } }");
        var document = ClassDiagramDocument.Create(new[] { source });
        var type = document.Types.Single();
        var state = document.NodeStates[ClassDiagramDocument.GetNodeId(type)];
        state.X = 123.5;
        state.Y = 456.25;
        state.Collapsed = true;
        state.PropertiesCollapsed = true;
        document.Notes.Add(new ClassDiagramNote { Text = "Remember this", X = 12, Y = 34 });
        var fileName = Path.Combine(directory, "Diagram.cd");
        document.Save(fileName);

        using var stream = File.OpenRead(fileName);
        var loaded = ClassDiagramDocument.Load(stream, directory);
        var loadedType = loaded.Types.Single();
        var loadedState = loaded.NodeStates[ClassDiagramDocument.GetNodeId(loadedType)];
        Assert.That(loadedState.X, Is.EqualTo(123.5));
        Assert.That(loadedState.Y, Is.EqualTo(456.25));
        Assert.That(loadedState.Collapsed, Is.True);
        Assert.That(loadedState.PropertiesCollapsed, Is.True);
        Assert.That(loaded.Notes.Single().Text, Is.EqualTo("Remember this"));
        Assert.That(loaded.Notes.Single().X, Is.EqualTo(12));
    }

    [Test]
    public void ImportsLegacyComment()
    {
        const string xml = "<ClassDiagram><Comment CommentText=\"Legacy note\"><Position X=\"1.5\" Y=\"2.5\" Width=\"3\" Height=\"1.2\" /></Comment></ClassDiagram>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var document = ClassDiagramDocument.Load(stream, directory);

        var note = document.Notes.Single();
        Assert.That(note.Text, Is.EqualTo("Legacy note"));
        Assert.That(note.X, Is.EqualTo(150));
        Assert.That(note.Y, Is.EqualTo(250));
        Assert.That(note.Width, Is.EqualTo(300));
        Assert.That(note.Height, Is.EqualTo(120));
    }

    [Test]
    public void PreservesUnknownRootMetadata()
    {
        const string xml = "<ClassDiagram Zoom=\"1.25\"><VendorData Name=\"keep-me\" /></ClassDiagram>";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var document = ClassDiagramDocument.Load(input, directory);
        using var output = new MemoryStream();

        document.Save(output, directory);

        var saved = Encoding.UTF8.GetString(output.ToArray());
        Assert.That(saved, Does.Contain("Zoom=\"1.25\""));
        Assert.That(saved, Does.Contain("VendorData"));
        Assert.That(saved, Does.Contain("keep-me"));
    }

    [Test]
    public void ImportsLegacyClassItemStateAfterSourcesAreResolved()
    {
        var source = WriteSource("Widget.cs", "namespace Sample { class Widget { } }");
        const string xml = "<ClassDiagram><ClassItem Type=\"Sample.Widget\" X=\"40\" Y=\"80\" Collapsed=\"True\"><Methods Collapsed=\"True\" /></ClassItem></ClassDiagram>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var document = ClassDiagramDocument.Load(stream, directory);
        document.SourceFiles.Add(source);
        document.Refresh();

        var type = document.Types.Single();
        var state = document.NodeStates[ClassDiagramDocument.GetNodeId(type)];
        Assert.That(state.X, Is.EqualTo(40));
        Assert.That(state.Y, Is.EqualTo(80));
        Assert.That(state.Collapsed, Is.True);
        Assert.That(state.MethodsCollapsed, Is.True);
    }

    [Test]
    public void MsaglLayoutProducesDistinctFinitePositions()
    {
        var source = WriteSource("Graph.cs", "class A { } class B : A { } class C { B Value { get; set; } }");
        var document = ClassDiagramDocument.Create(new[] { source });

        var routes = new MsaglClassDiagramLayoutEngine().Arrange(document);

        var positions = document.Types.Select(type => document.NodeStates[ClassDiagramDocument.GetNodeId(type)])
            .Select(state => (state.X, state.Y)).ToList();
        Assert.That(positions.All(position => double.IsFinite(position.X) && double.IsFinite(position.Y)), Is.True);
        Assert.That(positions.Distinct().Count(), Is.EqualTo(document.Types.Count));
        Assert.That(routes, Is.Not.Empty);
        Assert.That(routes.All(route => route.Points.Count == 25), Is.True);
    }

    string WriteSource(string name, string contents)
    {
        var fileName = Path.Combine(directory, name);
        File.WriteAllText(fileName, contents);
        return fileName;
    }
}
