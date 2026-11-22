#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace ICSharpCode.SharpDevelop.LanguageServices.Roslyn;

/// <summary>
/// Host-owned first-generation EnC producer. The baseline is keyed by project/TFM and is created
/// from the PE bytes selected by the IDE launch path, never from a second emit in the IDE.
/// </summary>
public sealed class HotReloadDeltaProducer
{
	static readonly Guid EncLocalSlotMap = new("755F52A8-91C5-45BE-B4B8-209571E552BD");
	static readonly Guid EncLambdaAndClosureMap = new("A643004C-0240-496F-A783-30D64F4979DE");

	sealed class Baseline : IDisposable
	{
		public Baseline(Compilation compilation, EmitBaseline emitBaseline, ModuleMetadata module,
			MemoryStream peStream, PEReader peReader, MetadataReaderProvider pdbProvider, Guid mvid,
			IReadOnlyDictionary<string, int> typeTokens)
		{
			Compilation = compilation;
			EmitBaseline = emitBaseline;
			Module = module;
			this.peStream = peStream;
			this.peReader = peReader;
			this.pdbProvider = pdbProvider;
			Mvid = mvid;
			TypeTokens = typeTokens;
		}

		readonly MemoryStream peStream;
		readonly PEReader peReader;
		readonly MetadataReaderProvider pdbProvider;
		public Compilation Compilation { get; }
		public EmitBaseline EmitBaseline { get; }
		public ModuleMetadata Module { get; }
		public Guid Mvid { get; }
		public IReadOnlyDictionary<string, int> TypeTokens { get; }

		public void Dispose()
		{
			pdbProvider.Dispose();
			peReader.Dispose();
			peStream.Dispose();
			Module.Dispose();
		}
	}

	readonly CSharpVBLanguageService languageService;
	readonly Dictionary<HotReloadProjectId, Baseline> baselines = new();
	readonly object sync = new();

	public HotReloadDeltaProducer(CSharpVBLanguageService languageService) =>
		this.languageService = languageService ?? throw new ArgumentNullException(nameof(languageService));

	public async Task<HotReloadDeltaResponse> CaptureBaselineAsync(HotReloadBaselineRequest request,
		CancellationToken cancellationToken)
	{
		if (request is null)
			throw new ArgumentNullException(nameof(request));
		if (request.ModuleImage is null || request.ModuleImage.Length == 0)
			return Failed("The launch module image is empty.");
		if (request.PdbImage is null || request.PdbImage.Length == 0)
			return Failed("The launch portable PDB image is empty.");
		var compilation = await languageService.GetProjectCompilationAsync(request.Project.ProjectFileName,
			request.Project.TargetFramework, cancellationToken).ConfigureAwait(false);
		if (compilation is null)
			return Failed("The Roslyn project is not loaded for this launch.");
		var errors = Errors(compilation);
		if (errors.Length != 0)
			return new HotReloadDeltaResponse(HotReloadDeltaStatus.CompilationError, null, errors);
		ModuleMetadata? module = null;
		MemoryStream? peStream = null;
		PEReader? peReader = null;
		MetadataReaderProvider? pdbProvider = null;
		try {
			// EmitBaseline retains metadata from its initial module. Keep that module alive until this
			// launch baseline is replaced or cleared; disposing it here makes the later EmitDifference
			// operate on a released image.
			module = ModuleMetadata.CreateFromImage(request.ModuleImage);
			var reader = module.GetMetadataReader();
			// The callbacks passed to CreateInitialBaseline are evaluated later by EmitDifference.
			// Retain their readers (and the PE stream) alongside ModuleMetadata for the complete
			// lifetime of the baseline instead of disposing them at the end of this request.
			peStream = new MemoryStream(request.ModuleImage, writable: false);
			peReader = new PEReader(peStream);
			pdbProvider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(request.PdbImage));
			var pdbReader = pdbProvider.GetMetadataReader();
			var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
			var baseline = new Baseline(compilation,
				EmitBaseline.CreateInitialBaseline(compilation, module,
					method => GetMethodDebugInformation(pdbReader, method),
					method => GetLocalSignature(reader, peReader, method), hasPortableDebugInformation: true),
				module, peStream, peReader, pdbProvider, mvid, ReadTypeTokens(reader));
			lock (sync) {
				if (baselines.Remove(request.Project, out var previous))
					previous.Dispose();
				baselines[request.Project] = baseline;
			}
			module = null;
			peStream = null;
			peReader = null;
			pdbProvider = null;
			return new HotReloadDeltaResponse(HotReloadDeltaStatus.Applied, null, "Baseline captured.");
		} catch (Exception ex) when (ex is BadImageFormatException || ex is ArgumentException || ex is InvalidOperationException) {
			return Failed("Could not read the launch module/PDB images: " + ex.Message);
		} finally {
			pdbProvider?.Dispose();
			peReader?.Dispose();
			peStream?.Dispose();
			module?.Dispose();
		}
	}

	/// <summary>Releases PE metadata retained by baselines when the loaded solution closes.</summary>
	public void Clear()
	{
		lock (sync) {
			foreach (var baseline in baselines.Values)
				baseline.Dispose();
			baselines.Clear();
		}
	}

	public async Task<HotReloadDeltaResponse> ProduceAsync(HotReloadDeltaRequest request, CancellationToken cancellationToken)
	{
		if (request is null)
			throw new ArgumentNullException(nameof(request));
		var compilation = await languageService.GetProjectCompilationAsync(request.Project.ProjectFileName,
			request.Project.TargetFramework, cancellationToken).ConfigureAwait(false);
		if (compilation is null)
			return Failed("The Roslyn project is no longer loaded.");
		var errors = Errors(compilation);
		if (errors.Length != 0)
			return new HotReloadDeltaResponse(HotReloadDeltaStatus.CompilationError, null, errors);
		using var metadata = new MemoryStream();
		using var il = new MemoryStream();
		using var pdb = new MemoryStream();
		lock (sync) {
			// Capture/Clear dispose the module and PDB readers. Keep the complete EnC operation in
			// this critical section so a concurrent launch or solution-close cannot release a
			// baseline while EmitDifference evaluates its lazy debug-information callbacks.
			if (!baselines.TryGetValue(request.Project, out var baseline))
				return Failed("No launch baseline exists for this project.");
			var edits = GetEdits(baseline.Compilation, compilation, out var updatedTypes, out var addedSymbols, out var rudeEdit);
			if (rudeEdit is not null)
				return new HotReloadDeltaResponse(HotReloadDeltaStatus.RudeEdit, null, rudeEdit);
			if (edits.Count == 0)
				return new HotReloadDeltaResponse(HotReloadDeltaStatus.NoChanges, null, "The compilation has no applicable changes.");
			var difference = compilation.EmitDifference(baseline.EmitBaseline, edits,
				symbol => addedSymbols.Contains(symbol), metadata, il, pdb);
			if (!difference.Success)
				return new HotReloadDeltaResponse(HotReloadDeltaStatus.RudeEdit, null, Diagnostics(difference.Diagnostics));
			var typeKeys = updatedTypes.Select(TypeKey).Distinct().ToArray();
			var missingTypeKeys = typeKeys.Where(key => !baseline.TypeTokens.ContainsKey(key)).ToArray();
			if (missingTypeKeys.Length != 0)
				return Failed("The launch module does not contain updated type(s): " + string.Join(", ", missingTypeKeys));
			var tokens = typeKeys.Select(key => baseline.TypeTokens[key]).ToArray();
			var updatedMethodTokens = difference.UpdatedMethods.Select(method => MetadataTokens.GetToken(method)).ToArray();
			return new HotReloadDeltaResponse(HotReloadDeltaStatus.Applied,
				new HotReloadModuleDelta(baseline.Mvid, metadata.ToArray(), il.ToArray(), pdb.ToArray(), tokens, updatedMethodTokens),
				$"Produced a delta for {difference.UpdatedMethods.Length} method(s).");
		}
	}

	static HotReloadDeltaResponse Failed(string message) => new(HotReloadDeltaStatus.Failed, null, message);

	static EditAndContinueMethodDebugInformation GetMethodDebugInformation(MetadataReader pdbReader, MethodDefinitionHandle method)
	{
		var localSlots = GetCustomDebugInformation(pdbReader, method, EncLocalSlotMap);
		var lambdas = GetCustomDebugInformation(pdbReader, method, EncLambdaAndClosureMap);
		return EditAndContinueMethodDebugInformation.Create(localSlots, lambdas);
	}

	static ImmutableArray<byte> GetCustomDebugInformation(MetadataReader reader, EntityHandle parent, Guid kind)
	{
		foreach (var handle in reader.GetCustomDebugInformation(parent)) {
			var information = reader.GetCustomDebugInformation(handle);
			if (reader.GetGuid(information.Kind) == kind)
				return reader.GetBlobBytes(information.Value).ToImmutableArray();
		}
		return ImmutableArray<byte>.Empty;
	}

	static StandaloneSignatureHandle GetLocalSignature(MetadataReader reader, PEReader peReader, MethodDefinitionHandle method)
	{
		var rva = reader.GetMethodDefinition(method).RelativeVirtualAddress;
		return rva == 0 ? default : peReader.GetMethodBody(rva).LocalSignature;
	}
	static string Errors(Compilation compilation) => Diagnostics(compilation.GetDiagnostics().Where(d => d.Severity == RoslynDiagnosticSeverity.Error));
	static string Diagnostics(IEnumerable<Diagnostic> diagnostics) => string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString()));

	static List<SemanticEdit> GetEdits(Compilation previous, Compilation current, out List<INamedTypeSymbol> updatedTypes,
		out HashSet<ISymbol> addedSymbols, out string? rudeEdit)
	{
		updatedTypes = new List<INamedTypeSymbol>();
		addedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
		rudeEdit = null;
		var oldSymbols = Members(previous.Assembly.GlobalNamespace).ToDictionary(SymbolKey);
		var newSymbols = Members(current.Assembly.GlobalNamespace).ToDictionary(SymbolKey);
		var oldStaticConstructors = StaticConstructors(previous.Assembly.GlobalNamespace).ToDictionary(SymbolKey);
		var newStaticConstructors = StaticConstructors(current.Assembly.GlobalNamespace).ToDictionary(SymbolKey);
		foreach (var pair in oldStaticConstructors) {
			if (!newStaticConstructors.TryGetValue(pair.Key, out var replacement)
				|| !string.Equals(DeclarationText(pair.Value), DeclarationText(replacement), StringComparison.Ordinal)) {
				rudeEdit = $"Changing static constructor '{pair.Key}' requires restarting the application.";
				return new List<SemanticEdit>();
			}
		}
		foreach (var pair in newStaticConstructors) {
			if (!oldStaticConstructors.ContainsKey(pair.Key)) {
				rudeEdit = $"Changing static constructor '{pair.Key}' requires restarting the application.";
				return new List<SemanticEdit>();
			}
		}
		var edits = new List<SemanticEdit>();
		foreach (var pair in oldSymbols) {
			if (!newSymbols.TryGetValue(pair.Key, out var replacement)) {
				rudeEdit = $"Removing '{pair.Key}' requires restarting the application.";
				return edits;
			}
			if (pair.Value.Kind != replacement.Kind || !string.Equals(Signature(pair.Value), Signature(replacement), StringComparison.Ordinal)) {
				rudeEdit = $"Changing the signature of '{pair.Key}' requires restarting the application.";
				return edits;
			}
			if (pair.Value is IMethodSymbol oldMethod && replacement is IMethodSymbol newMethod
				&& !string.Equals(DeclarationText(oldMethod), DeclarationText(newMethod), StringComparison.Ordinal)) {
				edits.Add(new SemanticEdit(SemanticEditKind.Update, oldMethod, newMethod));
				updatedTypes.Add(newMethod.ContainingType);
			} else if ((pair.Value is IFieldSymbol || pair.Value is IEventSymbol)
				&& !string.Equals(DeclarationText(pair.Value), DeclarationText(replacement), StringComparison.Ordinal)) {
				rudeEdit = $"Changing field '{pair.Key}' requires restarting the application.";
				return edits;
			}
		}
		foreach (var pair in newSymbols) {
			if (oldSymbols.ContainsKey(pair.Key)) continue;
			if (pair.Value is IFieldSymbol field) {
				edits.Add(new SemanticEdit(SemanticEditKind.Insert, null, field));
				addedSymbols.Add(field);
				updatedTypes.Add(field.ContainingType);
			} else {
				rudeEdit = $"Adding '{pair.Key}' requires restarting the application.";
				return edits;
			}
		}
		return edits;
	}

	static IEnumerable<ISymbol> Members(INamespaceSymbol ns)
	{
		foreach (var type in ns.GetTypeMembers()) foreach (var member in Members(type)) yield return member;
		foreach (var child in ns.GetNamespaceMembers()) foreach (var member in Members(child)) yield return member;
	}
	static IEnumerable<ISymbol> Members(INamedTypeSymbol type)
	{
		foreach (var member in type.GetMembers()) {
			// Lambda/display-class methods are implementation details of the source method that
			// changed. They are supplied to EmitDifference via isAddedSymbol, not classified as
			// independent user edits that could turn a valid lambda addition into a rude edit.
			if (member is IMethodSymbol method && method.MethodKind != MethodKind.StaticConstructor && !method.IsImplicitlyDeclared) yield return method;
			else if (member is IFieldSymbol field && !field.IsImplicitlyDeclared) yield return field;
			else if (member is IEventSymbol eventSymbol && !eventSymbol.IsImplicitlyDeclared) yield return eventSymbol;
			else if (member is INamedTypeSymbol nested) foreach (var nestedMember in Members(nested)) yield return nestedMember;
		}
	}
	static IEnumerable<IMethodSymbol> StaticConstructors(INamespaceSymbol ns)
	{
		foreach (var type in ns.GetTypeMembers()) foreach (var method in StaticConstructors(type)) yield return method;
		foreach (var child in ns.GetNamespaceMembers()) foreach (var method in StaticConstructors(child)) yield return method;
	}
	static IEnumerable<IMethodSymbol> StaticConstructors(INamedTypeSymbol type)
	{
		foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
			if (method.MethodKind == MethodKind.StaticConstructor) yield return method;
		foreach (var nested in type.GetTypeMembers()) foreach (var method in StaticConstructors(nested)) yield return method;
	}
	static string SymbolKey(ISymbol symbol)
	{
		if (symbol is IMethodSymbol method) {
			// MetadataName alone aliases overloads, causing ToDictionary to throw before EmitDifference
			// can return an ordinary rude-edit diagnostic. Parameters are identity here; return type
			// stays in Signature so a return-type change remains a rejected signature change.
			return TypeKey(method.ContainingType) + ":Method:" + method.MetadataName + "`" + method.Arity + "("
				+ string.Join(",", method.Parameters.Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))) + ")";
		}
		return TypeKey(symbol.ContainingType) + ":" + symbol.Kind + ":" + symbol.MetadataName;
	}

	static string TypeKey(INamedTypeSymbol? type)
	{
		if (type is null)
			return string.Empty;
		var names = new Stack<string>();
		for (var current = type; current is not null; current = current.ContainingType)
			names.Push(current.MetadataName);
		var prefix = type.ContainingNamespace?.IsGlobalNamespace == false
			? type.ContainingNamespace.ToDisplayString() + "."
			: string.Empty;
		return prefix + string.Join("+", names);
	}
	static string Signature(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
	static string DeclarationText(ISymbol symbol) => string.Concat(symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax().ToFullString()));
	static IReadOnlyDictionary<string, int> ReadTypeTokens(MetadataReader reader)
	{
		var result = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var handle in reader.TypeDefinitions) {
			var type = reader.GetTypeDefinition(handle);
			result[MetadataTypeKey(reader, handle)] = MetadataTokens.GetToken(handle);
		}
		return result;
	}
	static string MetadataTypeKey(MetadataReader reader, TypeDefinitionHandle handle)
	{
		var names = new Stack<string>();
		var current = handle;
		while (!current.IsNil) {
			var type = reader.GetTypeDefinition(current);
			names.Push(reader.GetString(type.Name));
			current = type.GetDeclaringType();
		}
		// A nested TypeDef has an empty namespace; obtain the namespace from its outermost owner.
		var outer = handle;
		while (!reader.GetTypeDefinition(outer).GetDeclaringType().IsNil)
			outer = reader.GetTypeDefinition(outer).GetDeclaringType();
		var ns = reader.GetString(reader.GetTypeDefinition(outer).Namespace);
		return string.IsNullOrEmpty(ns) ? string.Join("+", names) : ns + "." + string.Join("+", names);
	}
}
