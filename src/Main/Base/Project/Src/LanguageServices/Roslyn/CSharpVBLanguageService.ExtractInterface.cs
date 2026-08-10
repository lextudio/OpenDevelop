#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
// See CSharpVBLanguageService.cs's alias comment: disambiguates against the COM interop
// "Accessibility" namespace now visible via UseWindowsForms=true.
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;

namespace ICSharpCode.SharpDevelop.LanguageServices.Roslyn
{
    // Extract Interface (doc/technotes/csharp-vb-binding.md), ported from the old
    // RoslynWorkspaceHelper.ExtractInterfaceAsync/GetExtractInterfaceCandidateMembers (C# only, same
    // as before - the syntax-construction half uses the C# SyntaxFactory directly). Split into its
    // own partial-class file since it's a self-contained concern with its own cache, not because
    // CSharpVBLanguageService.cs was reorganized.
    public sealed partial class CSharpVBLanguageService
    {
        // Last computed candidate-member list per document, keyed by the opaque
        // ExtractInterfaceMember.Id GetExtractInterfaceInfoAsync handed out - same
        // "valid until the next call for this document" convention as _pendingCodeActionsByDocument.
        readonly Dictionary<DocumentId, Dictionary<string, ISymbol>> _pendingExtractInterfaceMembersByDocument = new();

        public async Task<ExtractInterfaceInfo?> GetExtractInterfaceInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            var found = await FindSymbolAtAsync(documentId, offset, cancellationToken);
            if (found is not { Symbol: INamedTypeSymbol { TypeKind: TypeKind.Class } type })
                return null;

            var candidates = type.GetMembers()
                .Where(m => m.DeclaredAccessibility == RoslynAccessibility.Public && !m.IsStatic)
                .Where(m => (m is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary) || m is IPropertySymbol || m is IEventSymbol)
                .ToArray();

            var cache = new Dictionary<string, ISymbol>();
            var members = new List<ExtractInterfaceMember>(candidates.Length);
            for (int i = 0; i < candidates.Length; i++)
            {
                var id = i.ToString();
                cache[id] = candidates[i];
                members.Add(new ExtractInterfaceMember(id, candidates[i].ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
            }
            _pendingExtractInterfaceMembersByDocument[documentId] = cache;

            return new ExtractInterfaceInfo(type.Name, members);
        }

        public async Task<ExtractInterfaceResult?> ExtractInterfaceAsync(
            DocumentId documentId, int offset, string interfaceName, IReadOnlyList<string> memberIds,
            bool addInterfaceToClass, bool includeComments, CancellationToken cancellationToken)
        {
            if (!_pendingExtractInterfaceMembersByDocument.TryGetValue(documentId, out var cache))
                return null;

            var found = await FindSymbolAtAsync(documentId, offset, cancellationToken);
            if (found is not { Symbol: INamedTypeSymbol { TypeKind: TypeKind.Class } classSymbol })
                return null;

            var chosenMembers = memberIds.Select(id => cache.TryGetValue(id, out var m) ? m : null).OfType<ISymbol>().ToArray();
            if (chosenMembers.Length == 0)
                return null;

            // Several AdhocWorkspace documents can declare the same type (same file name + same
            // namespace, e.g. two test temp copies of Widget.cs): Roslyn merges them into one
            // INamedTypeSymbol whose DeclaringSyntaxReferences lists EVERY declaration. The
            // merged symbol's First() reference may belong to a stale document, which would make
            // the class edit land in the wrong file - always pick the reference that belongs to
            // the document this request is actually about.
            var classSyntaxRef = classSymbol.DeclaringSyntaxReferences
                .FirstOrDefault(reference => string.Equals(reference.SyntaxTree.FilePath, documentId.FileName, StringComparison.OrdinalIgnoreCase))
                ?? classSymbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (classSyntaxRef is null)
                return null;
            var classNode = await classSyntaxRef.GetSyntaxAsync(cancellationToken) as Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax;
            var root = await classSyntaxRef.SyntaxTree.GetRootAsync(cancellationToken) as Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax;
            if (classNode is null || root is null)
                return null;

            var usings = root.Usings.Select(u => u.ToString());
            var interfaceText = BuildInterfaceSourceText(usings, classSymbol.ContainingNamespace, interfaceName, chosenMembers, includeComments);

            var edits = new Dictionary<string, IReadOnlyList<TextEdit>>(StringComparer.OrdinalIgnoreCase);
            if (addInterfaceToClass)
            {
                var newBaseType = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SimpleBaseType(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseTypeName(interfaceName));
                var newBaseList = classNode.BaseList is null
                    ? Microsoft.CodeAnalysis.CSharp.SyntaxFactory.BaseList(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SingletonSeparatedList<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeSyntax>(newBaseType))
                    : classNode.BaseList.AddTypes(newBaseType);
                var newClassNode = classNode.WithBaseList(newBaseList).NormalizeWhitespace();
                var newRoot = root.ReplaceNode(classNode, newClassNode);

                var oldText = await classSyntaxRef.SyntaxTree.GetTextAsync(cancellationToken);
                var newFullText = newRoot.ToFullString();
                edits[classSyntaxRef.SyntaxTree.FilePath] = new[] { WholeDocumentReplaceEdit(oldText, newFullText) };
            }

            return new ExtractInterfaceResult(interfaceText, edits);
        }

        static TextEdit WholeDocumentReplaceEdit(SourceText oldText, string newFullText)
        {
            var lastLine = oldText.Lines[oldText.Lines.Count - 1];
            var end = new TextPosition(oldText.Lines.Count, lastLine.End - lastLine.Start + 1);
            return new TextEdit(new TextSpan(new TextPosition(1, 1), end), newFullText);
        }

        static string BuildInterfaceSourceText(
            IEnumerable<string> usings, INamespaceSymbol? containingNamespace, string interfaceName, IReadOnlyList<ISymbol> members,
            bool includeComments)
        {
            var sb = new System.Text.StringBuilder();
            var usingsList = usings.ToArray();
            foreach (var u in usingsList)
                sb.AppendLine(u);
            if (usingsList.Length > 0)
                sb.AppendLine();

            bool hasNamespace = containingNamespace != null && !containingNamespace.IsGlobalNamespace;
            string indent = hasNamespace ? "\t" : "";
            if (hasNamespace)
            {
                sb.Append("namespace ").AppendLine(containingNamespace!.ToDisplayString());
                sb.AppendLine("{");
            }

            sb.Append(indent).Append("public interface ").AppendLine(interfaceName);
            sb.Append(indent).AppendLine("{");
            foreach (var member in members)
                sb.Append(indent).Append('\t').AppendLine(FormatInterfaceMember(member, includeComments));
            sb.Append(indent).AppendLine("}");

            if (hasNamespace)
                sb.AppendLine("}");

            return sb.ToString();
        }

        static string FormatInterfaceMember(ISymbol member, bool includeComments)
        {
            string signature;
            switch (member)
            {
                case IMethodSymbol method:
                {
                    var typeParams = method.TypeParameters.Length == 0
                        ? ""
                        : "<" + string.Join(", ", method.TypeParameters.Select(t => t.Name)) + ">";
                    var parameters = string.Join(", ", method.Parameters.Select(FormatParameter));
                    var constraints = string.Join(" ", method.TypeParameters
                        .Select(FormatTypeParameterConstraints)
                        .Where(c => c != null));
                    signature = $"{method.ReturnType.ToDisplayString()} {method.Name}{typeParams}({parameters})"
                        + (constraints.Length > 0 ? " " + constraints : "") + ";";
                    break;
                }
                case IPropertySymbol property:
                {
                    var accessors = property.GetMethod != null ? "get; " : "";
                    accessors += property.SetMethod != null ? "set; " : "";
                    signature = $"{property.Type.ToDisplayString()} {property.Name} {{ {accessors}}}";
                    break;
                }
                case IEventSymbol evt:
                    signature = $"event {evt.Type.ToDisplayString()} {evt.Name};";
                    break;
                default:
                    signature = "// unsupported member kind: " + member.Name;
                    break;
            }

            if (!includeComments)
                return signature;

            var comment = GetXmlDocComment(member);
            return comment == null ? signature : comment + "\n\t" + signature;
        }

        /// <summary>
        /// Builds a `where T : ...` clause for a generic method's type parameter, or null if the
        /// parameter has no constraints. Roslyn's <see cref="ITypeParameterSymbol"/> only exposes
        /// constraint flags/types, not source text, so this has to be assembled by hand rather than
        /// copied verbatim like <see cref="GetXmlDocComment"/> does for doc comments.
        /// </summary>
        static string? FormatTypeParameterConstraints(ITypeParameterSymbol typeParameter)
        {
            var constraints = new List<string>();
            if (typeParameter.HasReferenceTypeConstraint)
                constraints.Add("class");
            if (typeParameter.HasValueTypeConstraint)
                constraints.Add("struct");
            if (typeParameter.HasNotNullConstraint)
                constraints.Add("notnull");
            if (typeParameter.HasUnmanagedTypeConstraint)
                constraints.Add("unmanaged");
            constraints.AddRange(typeParameter.ConstraintTypes.Select(t => t.ToDisplayString()));
            if (typeParameter.HasConstructorConstraint)
                constraints.Add("new()");

            return constraints.Count == 0 ? null : $"where {typeParameter.Name} : {string.Join(", ", constraints)}";
        }

        /// <summary>
        /// Returns the member's original "///" doc comment block verbatim (not the semantically
        /// processed XML from GetDocumentationCommentXml()), so the extracted interface member keeps
        /// exactly what the author wrote on the class member.
        /// </summary>
        static string? GetXmlDocComment(ISymbol member)
        {
            var syntaxRef = member.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null)
                return null;
            var node = syntaxRef.GetSyntax();
            var docTrivia = node.GetLeadingTrivia()
                .Select(t => t.GetStructure())
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.DocumentationCommentTriviaSyntax>()
                .FirstOrDefault();
            return docTrivia?.ToFullString().Trim();
        }

        static string FormatParameter(IParameterSymbol parameter)
        {
            var modifier = parameter.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                RefKind.In => "in ",
                _ => parameter.IsParams ? "params " : "",
            };
            return $"{modifier}{parameter.Type.ToDisplayString()} {parameter.Name}";
        }
    }
}
