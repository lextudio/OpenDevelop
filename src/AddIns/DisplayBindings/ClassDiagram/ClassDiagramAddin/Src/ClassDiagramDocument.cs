using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ICSharpCode.ClassDiagram;

public sealed record ClassDiagramType(
    string Name,
    string QualifiedName,
    string Kind,
    string SourceFile,
    int SourceLine,
    IReadOnlyList<string> BaseTypes,
    IReadOnlyList<string> BaseTypeIdentities,
    IReadOnlyList<ClassDiagramMember> Members);

public enum ClassDiagramMemberKind
{
    Field,
    Property,
    Event,
    Method
}

public sealed record ClassDiagramMember(
    ClassDiagramMemberKind Kind,
    string DisplayText,
    int SourceLine);

public enum ClassDiagramRelationshipKind
{
    Association,
    Aggregation,
    Composition,
    Dependency
}

public sealed record ClassDiagramRelationship(
    string SourceType,
    string TargetType,
    ClassDiagramRelationshipKind Kind);

public sealed class ClassDiagramNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 220;
    public double Height { get; set; } = 120;
}

public sealed class ClassDiagramNodeState
{
    public double X { get; set; }
    public double Y { get; set; }
    public bool Collapsed { get; set; }
    public bool FieldsCollapsed { get; set; }
    public bool PropertiesCollapsed { get; set; }
    public bool EventsCollapsed { get; set; }
    public bool MethodsCollapsed { get; set; }
}

public sealed class ClassDiagramDocument
{
    public List<string> SourceFiles { get; } = new();
    public List<ClassDiagramType> Types { get; } = new();
    public List<ClassDiagramRelationship> Relationships { get; } = new();
    public List<ClassDiagramNote> Notes { get; } = new();
    public Dictionary<string, ClassDiagramNodeState> NodeStates { get; } =
        new Dictionary<string, ClassDiagramNodeState>(StringComparer.Ordinal);
    readonly Dictionary<string, ClassDiagramNodeState> legacyNodeStates =
        new Dictionary<string, ClassDiagramNodeState>(StringComparer.Ordinal);
    public List<XElement> ExtensionElements { get; } = new();
    public Dictionary<XName, string> ExtensionAttributes { get; } = new();

    public static ClassDiagramDocument Create(IEnumerable<string> sourceFiles)
    {
        var document = new ClassDiagramDocument();
        document.SourceFiles.AddRange(sourceFiles.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase));
        document.Refresh();
        return document;
    }

    public static Task<ClassDiagramDocument> CreateAsync(
        IEnumerable<string> sourceFiles,
        CancellationToken cancellationToken)
    {
        var snapshot = sourceFiles.ToArray();
        return Task.Run(() => {
            cancellationToken.ThrowIfCancellationRequested();
            var document = new ClassDiagramDocument();
            document.SourceFiles.AddRange(snapshot.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase));
            document.Refresh(cancellationToken);
            return document;
        }, cancellationToken);
    }

    public static ClassDiagramDocument Load(Stream stream, string directory)
    {
        var document = new ClassDiagramDocument();
        var root = XDocument.Load(stream).Root;
        if (root is null)
            return document;
        foreach (var attribute in root.Attributes().Where(attribute => attribute.Name != "Version"))
            document.ExtensionAttributes[attribute.Name] = attribute.Value;
        foreach (var source in root.Elements("Source")) {
            var path = (string)source.Attribute("File");
            if (!string.IsNullOrWhiteSpace(path))
                document.SourceFiles.Add(Path.GetFullPath(Path.Combine(directory, path)));
        }
        foreach (var node in root.Elements("Node")) {
            var id = (string)node.Attribute("Id");
            if (string.IsNullOrEmpty(id))
                continue;
            var state = new ClassDiagramNodeState {
                X = (double?)node.Attribute("X") ?? 0,
                Y = (double?)node.Attribute("Y") ?? 0,
                Collapsed = (bool?)node.Attribute("Collapsed") ?? false,
                FieldsCollapsed = (bool?)node.Attribute("FieldsCollapsed") ?? false,
                PropertiesCollapsed = (bool?)node.Attribute("PropertiesCollapsed") ?? false,
                EventsCollapsed = (bool?)node.Attribute("EventsCollapsed") ?? false,
                MethodsCollapsed = (bool?)node.Attribute("MethodsCollapsed") ?? false
            };
            document.NodeStates[id] = state;
            var separator = id.LastIndexOf('|');
            if (separator >= 0 && separator + 1 < id.Length)
                document.legacyNodeStates[id.Substring(separator + 1)] = state;
        }
        foreach (var node in root.Elements().Where(element =>
            element.Name.LocalName.EndsWith("Item", StringComparison.Ordinal)
            || element.Name.LocalName is "Class" or "Struct" or "Enum" or "Interface" or "Delegate")) {
            var typeName = (string)node.Attribute("Type") ?? (string)node.Attribute("Name");
            if (string.IsNullOrEmpty(typeName))
                continue;
            document.legacyNodeStates[typeName] = new ClassDiagramNodeState {
                X = (double?)node.Attribute("X") ?? 0,
                Y = (double?)node.Attribute("Y") ?? 0,
                Collapsed = (bool?)node.Attribute("Collapsed") ?? false,
                FieldsCollapsed = (bool?)node.Element("Fields")?.Attribute("Collapsed") ?? false,
                PropertiesCollapsed = (bool?)node.Element("Properties")?.Attribute("Collapsed") ?? false,
                EventsCollapsed = (bool?)node.Element("Events")?.Attribute("Collapsed") ?? false,
                MethodsCollapsed = (bool?)node.Element("Methods")?.Attribute("Collapsed") ?? false
            };
        }
        foreach (var note in root.Elements("Note"))
            document.Notes.Add(new ClassDiagramNote {
                Id = (string)note.Attribute("Id") ?? Guid.NewGuid().ToString("N"),
                Text = (string)note.Attribute("Text") ?? note.Value,
                X = (double?)note.Attribute("X") ?? 30,
                Y = (double?)note.Attribute("Y") ?? 30,
                Width = (double?)note.Attribute("Width") ?? 220,
                Height = (double?)note.Attribute("Height") ?? 120
            });
        foreach (var comment in root.Elements("Comment")) {
            var position = comment.Element("Position");
            document.Notes.Add(new ClassDiagramNote {
                Text = (string)comment.Attribute("CommentText") ?? string.Empty,
                X = 100 * ((double?)position?.Attribute("X") ?? 0.3),
                Y = 100 * ((double?)position?.Attribute("Y") ?? 0.3),
                Width = 100 * ((double?)position?.Attribute("Width") ?? 2.2),
                Height = 100 * ((double?)position?.Attribute("Height") ?? 1.2)
            });
        }
        var knownElementNames = new HashSet<string>(StringComparer.Ordinal) {
            "Source", "Node", "Note", "Comment", "Class", "Struct", "Enum", "Interface", "Delegate"
        };
        foreach (var element in root.Elements().Where(element =>
                     !knownElementNames.Contains(element.Name.LocalName)
                     && !element.Name.LocalName.EndsWith("Item", StringComparison.Ordinal)))
            document.ExtensionElements.Add(new XElement(element));
        document.Refresh();
        return document;
    }

    public void Save(string fileName)
    {
        using var stream = File.Create(fileName);
        Save(stream, Path.GetDirectoryName(fileName) ?? string.Empty);
    }

    public void Save(Stream stream, string directory)
    {
        new XDocument(new XElement("ClassDiagram",
            new XAttribute("Version", "2"),
            ExtensionAttributes.Select(pair => new XAttribute(pair.Key, pair.Value)),
            SourceFiles.Select(path => new XElement("Source",
                new XAttribute("File", Path.GetRelativePath(directory, path)))),
            NodeStates.Select(pair => new XElement("Node",
                new XAttribute("Id", pair.Key),
                new XAttribute("X", pair.Value.X),
                new XAttribute("Y", pair.Value.Y),
                new XAttribute("Collapsed", pair.Value.Collapsed),
                new XAttribute("FieldsCollapsed", pair.Value.FieldsCollapsed),
                new XAttribute("PropertiesCollapsed", pair.Value.PropertiesCollapsed),
                new XAttribute("EventsCollapsed", pair.Value.EventsCollapsed),
                new XAttribute("MethodsCollapsed", pair.Value.MethodsCollapsed))),
            Notes.Select(note => new XElement("Note",
                new XAttribute("Id", note.Id),
                new XAttribute("Text", note.Text ?? string.Empty),
                new XAttribute("X", note.X),
                new XAttribute("Y", note.Y),
                new XAttribute("Width", note.Width),
                new XAttribute("Height", note.Height))),
            ExtensionElements.Select(element => new XElement(element)))).Save(stream);
    }

    public void Refresh() => Refresh(CancellationToken.None);

    public void Refresh(CancellationToken cancellationToken)
    {
        Types.Clear();
        Relationships.Clear();
        var trees = new List<SyntaxTree>();
        foreach (var file in SourceFiles.Where(File.Exists)) {
            cancellationToken.ThrowIfCancellationRequested();
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file, cancellationToken: cancellationToken));
        }
        var compilation = CSharpCompilation.Create("ClassDiagramAnalysis", trees,
            GetFrameworkReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var declarations = new List<(TypeDeclarationSyntax Declaration, INamedTypeSymbol Symbol)>();
        foreach (var tree in trees) {
            cancellationToken.ThrowIfCancellationRequested();
            var root = tree.GetRoot();
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {
                cancellationToken.ThrowIfCancellationRequested();
                var symbol = model.GetDeclaredSymbol(declaration, cancellationToken);
                if (declaration is TypeDeclarationSyntax typeDeclaration && symbol is not null)
                    declarations.Add((typeDeclaration, symbol));
                Types.Add(CreateType(tree, tree.FilePath, declaration, symbol));
            }
            foreach (var declaration in root.DescendantNodes().OfType<DelegateDeclarationSyntax>()) {
                var line = tree.GetLineSpan(declaration.Identifier.Span).StartLinePosition.Line + 1;
                var symbol = model.GetDeclaredSymbol(declaration, cancellationToken);
                var qualifiedName = symbol is null ? GetSyntacticQualifiedName(declaration, declaration.Identifier.ValueText) : GetSymbolIdentity(symbol);
                Types.Add(new ClassDiagramType(declaration.Identifier.ValueText, qualifiedName, "delegate", tree.FilePath, line, Array.Empty<string>(), Array.Empty<string>(),
                    new[] { new ClassDiagramMember(ClassDiagramMemberKind.Method, $"{declaration.ReturnType} Invoke({string.Join(", ", declaration.ParameterList.Parameters)})", line) }));
            }
        }
        Types.Sort((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name));
        AnalyzeRelationships(declarations, compilation, cancellationToken);
        for (var index = 0; index < Types.Count; index++) {
            var type = Types[index];
            var id = GetNodeId(type);
            if (NodeStates.ContainsKey(id))
                continue;
            var legacy = legacyNodeStates.FirstOrDefault(pair =>
                GetSimpleName(pair.Key) == GetSimpleName(type.Name));
            NodeStates.Add(id, legacy.Value ?? new ClassDiagramNodeState());
        }
    }

    public void CopyUserStateFrom(ClassDiagramDocument previous)
    {
        foreach (var type in Types) {
            var previousType = previous.Types.FirstOrDefault(candidate =>
                candidate.QualifiedName == type.QualifiedName);
            if (previousType is null)
                continue;
            var state = NodeStates[GetNodeId(type)];
            var oldState = previous.NodeStates[GetNodeId(previousType)];
            state.X = oldState.X;
            state.Y = oldState.Y;
            state.Collapsed = oldState.Collapsed;
            state.FieldsCollapsed = oldState.FieldsCollapsed;
            state.PropertiesCollapsed = oldState.PropertiesCollapsed;
            state.EventsCollapsed = oldState.EventsCollapsed;
            state.MethodsCollapsed = oldState.MethodsCollapsed;
        }
        Notes.AddRange(previous.Notes);
        foreach (var pair in previous.ExtensionAttributes)
            ExtensionAttributes[pair.Key] = pair.Value;
        ExtensionElements.AddRange(previous.ExtensionElements.Select(element => new XElement(element)));
    }

    public static string GetNodeId(ClassDiagramType type) =>
        type.SourceFile + "|" + type.SourceLine + "|" + type.QualifiedName;

    void AnalyzeRelationships(
        IEnumerable<(TypeDeclarationSyntax Declaration, INamedTypeSymbol Symbol)> declarations,
        CSharpCompilation compilation,
        CancellationToken cancellationToken)
    {
        var knownTypes = Types.ToDictionary(type => type.QualifiedName, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in declarations) {
            cancellationToken.ThrowIfCancellationRequested();
            var declaration = item.Declaration;
            var sourceName = GetSymbolIdentity(item.Symbol);
            if (!knownTypes.ContainsKey(sourceName))
                continue;
            var model = compilation.GetSemanticModel(declaration.SyntaxTree);
            var baseTypes = new HashSet<string>(knownTypes.Keys.Where(identity =>
                IsBaseType(item.Symbol, identity)), StringComparer.Ordinal);
            foreach (var member in declaration.Members) {
                switch (member) {
                    case FieldDeclarationSyntax field:
                        AddRelationships(sourceName, model.GetTypeInfo(field.Declaration.Type, cancellationToken).Type,
                            GetOwnershipKind(field.Declaration.Type, field.Declaration.Variables.Select(variable => variable.Initializer?.Value)),
                            baseTypes, knownTypes, seen);
                        break;
                    case PropertyDeclarationSyntax property:
                        AddRelationships(sourceName, model.GetTypeInfo(property.Type, cancellationToken).Type,
                            GetOwnershipKind(property.Type, new ExpressionSyntax[] { property.Initializer?.Value, property.ExpressionBody?.Expression }),
                            baseTypes, knownTypes, seen);
                        break;
                    case EventDeclarationSyntax @event:
                        AddRelationships(sourceName, model.GetTypeInfo(@event.Type, cancellationToken).Type, ClassDiagramRelationshipKind.Association, baseTypes, knownTypes, seen);
                        break;
                    case EventFieldDeclarationSyntax @event:
                        AddRelationships(sourceName, model.GetTypeInfo(@event.Declaration.Type, cancellationToken).Type, ClassDiagramRelationshipKind.Association, baseTypes, knownTypes, seen);
                        break;
                    case MethodDeclarationSyntax method:
                        AddRelationships(sourceName, model.GetTypeInfo(method.ReturnType, cancellationToken).Type, ClassDiagramRelationshipKind.Dependency, baseTypes, knownTypes, seen);
                        foreach (var parameter in method.ParameterList.Parameters)
                            AddRelationships(sourceName, parameter.Type is null ? null : model.GetTypeInfo(parameter.Type, cancellationToken).Type, ClassDiagramRelationshipKind.Dependency, baseTypes, knownTypes, seen);
                        break;
                    case ConstructorDeclarationSyntax constructor:
                        foreach (var parameter in constructor.ParameterList.Parameters)
                            AddRelationships(sourceName, parameter.Type is null ? null : model.GetTypeInfo(parameter.Type, cancellationToken).Type, ClassDiagramRelationshipKind.Dependency, baseTypes, knownTypes, seen);
                        break;
                }
            }
        }
    }

    void AddRelationships(
        string sourceName,
        ITypeSymbol symbol,
        ClassDiagramRelationshipKind kind,
        HashSet<string> baseTypes,
        IReadOnlyDictionary<string, ClassDiagramType> knownTypes,
        HashSet<string> seen)
    {
        if (symbol is null)
            return;
        foreach (var identity in GetReferencedTypeIdentities(symbol).Distinct(StringComparer.Ordinal)) {
            if (identity == sourceName || baseTypes.Contains(identity) || !knownTypes.ContainsKey(identity))
                continue;
            var key = sourceName + "|" + identity + "|" + kind;
            if (seen.Add(key))
                Relationships.Add(new ClassDiagramRelationship(sourceName, identity, kind));
        }
    }

    static IEnumerable<string> GetReferencedTypeIdentities(ITypeSymbol symbol)
    {
        if (symbol is IArrayTypeSymbol array) {
            foreach (var identity in GetReferencedTypeIdentities(array.ElementType))
                yield return identity;
            yield break;
        }
        if (symbol is not INamedTypeSymbol named)
            yield break;
        yield return GetSymbolIdentity(named);
        foreach (var argument in named.TypeArguments)
            foreach (var identity in GetReferencedTypeIdentities(argument))
                yield return identity;
    }

    static bool IsBaseType(INamedTypeSymbol symbol, string identity) =>
        symbol.BaseType is not null && GetSymbolIdentity(symbol.BaseType) == identity
        || symbol.Interfaces.Any(item => GetSymbolIdentity(item) == identity);

    static IEnumerable<MetadataReference> GetFrameworkReferences()
    {
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        return string.IsNullOrEmpty(trustedAssemblies)
            ? new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }
            : trustedAssemblies.Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
    }

    static ClassDiagramRelationshipKind GetOwnershipKind(
        TypeSyntax type,
        IEnumerable<ExpressionSyntax> initializers)
    {
        if (IsCollectionType(type))
            return ClassDiagramRelationshipKind.Aggregation;
        if (initializers.Where(initializer => initializer is not null).Any(initializer =>
                initializer is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
                || initializer.DescendantNodes().Any(node =>
                    node is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)))
            return ClassDiagramRelationshipKind.Composition;
        return ClassDiagramRelationshipKind.Association;
    }

    static bool IsCollectionType(TypeSyntax type)
    {
        if (type is ArrayTypeSyntax)
            return true;
        var outerName = type switch {
            GenericNameSyntax generic => generic.Identifier.ValueText,
            QualifiedNameSyntax qualified when qualified.Right is GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => string.Empty
        };
        return outerName is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection"
            or "IReadOnlyList" or "List" or "HashSet" or "Collection" or "ObservableCollection";
    }

    static string GetSimpleName(string name)
    {
        var dot = name.LastIndexOf('.');
        var simple = dot >= 0 ? name.Substring(dot + 1) : name;
        var generic = simple.IndexOf('<');
        return generic >= 0 ? simple.Substring(0, generic) : simple;
    }

    static string GetSyntacticQualifiedName(SyntaxNode declaration, string name)
    {
        var namespaces = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(item => item.Name.ToString());
        var containingTypes = declaration.Ancestors().OfType<TypeDeclarationSyntax>()
            .Reverse().Select(item => item.Identifier.ValueText);
        return string.Join(".", namespaces.Concat(containingTypes).Append(GetSimpleName(name)));
    }

    static ClassDiagramType CreateType(SyntaxTree tree, string file, BaseTypeDeclarationSyntax declaration, INamedTypeSymbol symbol)
    {
        var name = declaration.Identifier.ValueText;
        var typeDeclaration = declaration as TypeDeclarationSyntax;
        if (typeDeclaration?.TypeParameterList is { Parameters.Count: > 0 } parameters)
            name += "<" + string.Join(", ", parameters.Parameters.Select(p => p.Identifier.ValueText)) + ">";
        var line = tree.GetLineSpan(declaration.Identifier.Span).StartLinePosition.Line + 1;
        var members = typeDeclaration is not null
            ? typeDeclaration.Members
            : ((EnumDeclarationSyntax)declaration).Members.Cast<MemberDeclarationSyntax>();
        var baseTypes = declaration.BaseList?.Types.Select(type => type.Type.ToString()).ToArray() ?? Array.Empty<string>();
        var baseIdentities = symbol is null ? baseTypes : GetDirectBaseTypes(symbol).Select(GetSymbolIdentity).ToArray();
        return new ClassDiagramType(name, symbol is null ? GetSyntacticQualifiedName(declaration, name) : GetSymbolIdentity(symbol), GetKind(declaration), file, line,
            baseTypes,
            baseIdentities,
            members.Select(member => FormatMember(tree, member)).Where(member => member is not null).ToArray());
    }

    static IEnumerable<INamedTypeSymbol> GetDirectBaseTypes(INamedTypeSymbol symbol)
    {
        if (symbol.BaseType is not null && symbol.BaseType.SpecialType != SpecialType.System_Object)
            yield return symbol.BaseType;
        foreach (var item in symbol.Interfaces)
            yield return item;
    }

    static string GetSymbolIdentity(INamedTypeSymbol symbol) =>
        symbol.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);

    static string GetKind(BaseTypeDeclarationSyntax declaration) => declaration switch {
        InterfaceDeclarationSyntax => "interface",
        StructDeclarationSyntax => "struct",
        EnumDeclarationSyntax => "enum",
        RecordDeclarationSyntax record => record.ClassOrStructKeyword.ValueText == "struct" ? "record struct" : "record",
        _ => "class"
    };

    static ClassDiagramMember FormatMember(SyntaxTree tree, MemberDeclarationSyntax member)
    {
        var line = tree.GetLineSpan(member.Span).StartLinePosition.Line + 1;
        return member switch {
            MethodDeclarationSyntax method => new(ClassDiagramMemberKind.Method, $"{Visibility(method.Modifiers)} {method.Identifier.ValueText}({string.Join(", ", method.ParameterList.Parameters.Select(p => p.Type))}) : {method.ReturnType}", line),
            PropertyDeclarationSyntax property => new(ClassDiagramMemberKind.Property, $"{Visibility(property.Modifiers)} {property.Identifier.ValueText} : {property.Type}", line),
            EventDeclarationSyntax @event => new(ClassDiagramMemberKind.Event, $"{Visibility(@event.Modifiers)} {@event.Identifier.ValueText} : {@event.Type}", line),
            EventFieldDeclarationSyntax @event => new(ClassDiagramMemberKind.Event, $"{Visibility(@event.Modifiers)} {string.Join(", ", @event.Declaration.Variables)} : {@event.Declaration.Type}", line),
            FieldDeclarationSyntax field => new(ClassDiagramMemberKind.Field, $"{Visibility(field.Modifiers)} {string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText))} : {field.Declaration.Type}", line),
            ConstructorDeclarationSyntax constructor => new(ClassDiagramMemberKind.Method, $"{Visibility(constructor.Modifiers)} {constructor.Identifier.ValueText}({string.Join(", ", constructor.ParameterList.Parameters.Select(p => p.Type))})", line),
            EnumMemberDeclarationSyntax value => new(ClassDiagramMemberKind.Field, value.Identifier.ValueText, line),
            _ => null
        };
    }

    static string Visibility(SyntaxTokenList modifiers) =>
        modifiers.Any(SyntaxKind.PublicKeyword) ? "+" :
        modifiers.Any(SyntaxKind.ProtectedKeyword) ? "#" :
        modifiers.Any(SyntaxKind.PrivateKeyword) ? "-" : "~";
}
