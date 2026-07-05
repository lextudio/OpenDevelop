// Minimal ICSharpCode.TypeSystem adapters backed by real Microsoft.CodeAnalysis symbols.
// See doc/technotes/csharp-roslyn.md, Phase 1: existing SD infrastructure (GoToDefinition,
// DefinitionViewPad, DeclaringTypeSubMenuBuilder, SymbolTypeAtCaretConditionEvaluator,
// EditorRefactoringContext, ...) is built around IEntity/IType/ITypeDefinition/IMember and
// ResolveResult; rewriting all of it to talk to Roslyn directly is out of scope for this pass.
// Instead these adapters give that existing (structurally sound) code REAL data for the first
// time, instead of the ICSharpCode.TypeSystem.Abstractions mocks always returning null/empty.
//
// Only members actually exercised by in-tree MVP consumers have real implementations; the long
// tail throws NotImplementedException, matching the "MVP mock" policy already used elsewhere in
// this codebase (see e.g. Mocks/MiscMocks.cs's DefaultUnresolvedMethod comment). New code should
// keep talking to Microsoft.CodeAnalysis directly (see FindBaseClasses.cs, GoToEntityAction.cs)
// rather than growing this adapter further.

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.TypeSystem;
using Microsoft.CodeAnalysis;

namespace ICSharpCode.SharpDevelop.Roslyn
{
	public static class RoslynEntityFactory
	{
		public static IEntity Create(Microsoft.CodeAnalysis.ISymbol symbol, ICompilation compilation)
		{
			if (symbol == null)
				return null;
			var namedType = symbol as INamedTypeSymbol;
			if (namedType != null)
				return new RoslynTypeDefinitionAdapter(namedType, compilation);
			if (symbol is IMethodSymbol || symbol is IFieldSymbol || symbol is IPropertySymbol || symbol is IEventSymbol)
				return new RoslynMemberAdapter(symbol, compilation);
			return null;
		}
	}

	abstract class RoslynSymbolAdapterBase
	{
		protected readonly Microsoft.CodeAnalysis.ISymbol Symbol;
		protected readonly ICompilation compilation;

		protected RoslynSymbolAdapterBase(Microsoft.CodeAnalysis.ISymbol symbol, ICompilation compilation)
		{
			this.Symbol = symbol;
			this.compilation = compilation;
		}

		public string Name { get { return Symbol.Name; } }
		public string FullName { get { return Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat); } }
		public string ReflectionName { get { return FullName; } }
		public string Namespace { get { return Symbol.ContainingNamespace != null ? Symbol.ContainingNamespace.ToDisplayString() : string.Empty; } }

		public ICompilation Compilation { get { return compilation; } }

		public TypeSystem.Accessibility Accessibility {
			get {
				switch (Symbol.DeclaredAccessibility) {
					case Microsoft.CodeAnalysis.Accessibility.Private: return TypeSystem.Accessibility.Private;
					case Microsoft.CodeAnalysis.Accessibility.Protected: return TypeSystem.Accessibility.Protected;
					case Microsoft.CodeAnalysis.Accessibility.Internal: return TypeSystem.Accessibility.Internal;
					case Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal: return TypeSystem.Accessibility.ProtectedOrInternal;
					case Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal: return TypeSystem.Accessibility.ProtectedAndInternal;
					case Microsoft.CodeAnalysis.Accessibility.Public: return TypeSystem.Accessibility.Public;
					default: return TypeSystem.Accessibility.None;
				}
			}
		}

		public bool IsPrivate { get { return Accessibility == TypeSystem.Accessibility.Private; } }
		public bool IsPublic { get { return Accessibility == TypeSystem.Accessibility.Public; } }
		public bool IsProtected { get { return Accessibility == TypeSystem.Accessibility.Protected; } }
		public bool IsInternal { get { return Accessibility == TypeSystem.Accessibility.Internal; } }
		public bool IsProtectedOrInternal { get { return Accessibility == TypeSystem.Accessibility.ProtectedOrInternal; } }
		public bool IsProtectedAndInternal { get { return Accessibility == TypeSystem.Accessibility.ProtectedAndInternal; } }

		public DomRegion Region {
			get {
				var location = Symbol.Locations.FirstOrDefault(l => l.IsInSource);
				if (location == null)
					return DomRegion.Empty;
				var span = location.GetLineSpan();
				return new DomRegion(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
					span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
			}
		}

		public DomRegion BodyRegion { get { return Region; } }

		public bool IsStatic { get { return Symbol.IsStatic; } }
		public bool IsAbstract { get { return Symbol.IsAbstract; } }
		public bool IsSealed { get { return Symbol.IsSealed; } }
		public bool IsShadowing { get { return false; } }
		public bool IsSynthetic { get { return Symbol.IsImplicitlyDeclared; } }

		public TypeSystem.SymbolKind SymbolKind {
			get {
				switch (Symbol.Kind) {
					case Microsoft.CodeAnalysis.SymbolKind.NamedType: return TypeSystem.SymbolKind.TypeDefinition;
					case Microsoft.CodeAnalysis.SymbolKind.Field: return TypeSystem.SymbolKind.Field;
					case Microsoft.CodeAnalysis.SymbolKind.Property: return ((IPropertySymbol)Symbol).IsIndexer ? TypeSystem.SymbolKind.Indexer : TypeSystem.SymbolKind.Property;
					case Microsoft.CodeAnalysis.SymbolKind.Event: return TypeSystem.SymbolKind.Event;
					case Microsoft.CodeAnalysis.SymbolKind.Method:
						var method = (IMethodSymbol)Symbol;
						switch (method.MethodKind) {
							case MethodKind.Constructor: return TypeSystem.SymbolKind.Constructor;
							case MethodKind.Destructor: return TypeSystem.SymbolKind.Destructor;
							case MethodKind.UserDefinedOperator:
							case MethodKind.Conversion: return TypeSystem.SymbolKind.Operator;
							case MethodKind.PropertyGet:
							case MethodKind.PropertySet:
							case MethodKind.EventAdd:
							case MethodKind.EventRemove: return TypeSystem.SymbolKind.Accessor;
							default: return TypeSystem.SymbolKind.Method;
						}
					case Microsoft.CodeAnalysis.SymbolKind.Namespace: return TypeSystem.SymbolKind.Namespace;
					case Microsoft.CodeAnalysis.SymbolKind.Parameter: return TypeSystem.SymbolKind.Parameter;
					case Microsoft.CodeAnalysis.SymbolKind.Local: return TypeSystem.SymbolKind.Variable;
					default: return TypeSystem.SymbolKind.None;
				}
			}
		}

		public ISymbolReference ToReference()
		{
			throw new NotImplementedException("RoslynSymbolAdapterBase.ToReference: no cross-compilation symbol references yet");
		}

		[Obsolete]
		public EntityType EntityType { get { throw new NotImplementedException(); } }
	}

	/// <summary>ITypeDefinition/IType backed by a real Microsoft.CodeAnalysis.INamedTypeSymbol.</summary>
	sealed class RoslynTypeDefinitionAdapter : RoslynSymbolAdapterBase, ITypeDefinition
	{
		readonly INamedTypeSymbol namedType;

		public RoslynTypeDefinitionAdapter(INamedTypeSymbol namedType, ICompilation compilation)
			: base(namedType, compilation)
		{
			this.namedType = namedType;
		}

		public INamedTypeSymbol Symbol2 { get { return namedType; } }

		public TypeSystem.TypeKind Kind {
			get {
				switch (namedType.TypeKind) {
					case Microsoft.CodeAnalysis.TypeKind.Interface: return TypeSystem.TypeKind.Interface;
					case Microsoft.CodeAnalysis.TypeKind.Struct: return TypeSystem.TypeKind.Struct;
					case Microsoft.CodeAnalysis.TypeKind.Enum: return TypeSystem.TypeKind.Enum;
					case Microsoft.CodeAnalysis.TypeKind.Delegate: return TypeSystem.TypeKind.Delegate;
					default: return TypeSystem.TypeKind.Class;
				}
			}
		}

		public bool? IsReferenceType { get { return namedType.IsReferenceType; } }

		public ITypeDefinition GetDefinition() { return this; }

		public bool Equals(IType other)
		{
			var otherAdapter = other as RoslynTypeDefinitionAdapter;
			return otherAdapter != null && SymbolEqualityComparer.Default.Equals(namedType, otherAdapter.namedType);
		}

		IType IType.DeclaringType { get { return namedType.ContainingType != null ? new RoslynTypeDefinitionAdapter(namedType.ContainingType, compilation) : null; } }
		public IType DeclaringType { get { return ((IType)this).DeclaringType; } }

		public ITypeDefinition DeclaringTypeDefinition { get { return namedType.ContainingType != null ? new RoslynTypeDefinitionAdapter(namedType.ContainingType, compilation) : null; } }

		public IAssembly ParentAssembly { get { return new RoslynAssemblyAdapter(namedType.ContainingAssembly, compilation); } }

		public IList<IAttribute> Attributes { get { return new List<IAttribute>(); } }

		public int TypeParameterCount { get { return namedType.TypeParameters.Length; } }
		public IList<IType> TypeArguments { get { return new List<IType>(); } }
		public bool IsParameterized { get { return namedType.IsGenericType; } }

		public IType AcceptVisitor(TypeVisitor visitor) { throw new NotImplementedException(); }
		public IType VisitChildren(TypeVisitor visitor) { throw new NotImplementedException(); }

		public IEnumerable<IType> DirectBaseTypes {
			get {
				var result = new List<IType>();
				if (namedType.BaseType != null)
					result.Add(new RoslynTypeDefinitionAdapter(namedType.BaseType, compilation));
				result.AddRange(namedType.Interfaces.Select(i => (IType)new RoslynTypeDefinitionAdapter(i, compilation)));
				return result;
			}
		}

		public ITypeReference ToTypeReference() { throw new NotImplementedException(); }
		public TypeParameterSubstitution GetSubstitution() { return TypeParameterSubstitution.Identity; }
		public TypeParameterSubstitution GetSubstitution(IList<IType> methodTypeArguments) { return TypeParameterSubstitution.Identity; }

		public IEnumerable<IType> GetNestedTypes(Predicate<ITypeDefinition> filter = null, GetMemberOptions options = GetMemberOptions.None)
		{
			return NestedTypes.Where(t => filter == null || filter(t)).Cast<IType>();
		}

		public IEnumerable<IType> GetNestedTypes(IList<IType> typeArguments, Predicate<ITypeDefinition> filter = null, GetMemberOptions options = GetMemberOptions.None)
		{
			return GetNestedTypes(filter, options);
		}

		public IEnumerable<IMethod> GetConstructors(Predicate<IUnresolvedMethod> filter = null, GetMemberOptions options = GetMemberOptions.IgnoreInheritedMembers)
		{
			return namedType.Constructors.Select(m => (IMethod)new RoslynMemberAdapter(m, compilation));
		}

		public IEnumerable<IMethod> GetMethods(Predicate<IUnresolvedMethod> filter = null, GetMemberOptions options = GetMemberOptions.None)
		{
			return Methods;
		}

		public IEnumerable<IMethod> GetMethods(IList<IType> typeArguments, Predicate<IUnresolvedMethod> filter = null, GetMemberOptions options = GetMemberOptions.None)
		{
			return Methods;
		}

		public IEnumerable<IProperty> GetProperties(Predicate<IUnresolvedProperty> filter = null, GetMemberOptions options = GetMemberOptions.None)
		{
			return Properties;
		}

		public IEnumerable<IField> GetFields(Predicate<IUnresolvedField> filter = null, GetMemberOptions options = GetMemberOptions.None)
		{
			return Fields;
		}

		public IEnumerable<IEvent> GetEvents(Predicate<IUnresolvedEvent> filter = null, GetMemberOptions options = GetMemberOptions.None)
		{
			return Events;
		}

		public IEnumerable<IMember> GetMembers(Predicate<IUnresolvedMember> filter = null, GetMemberOptions options = GetMemberOptions.None)
		{
			return Members;
		}

		public IEnumerable<IMethod> GetAccessors(Predicate<IUnresolvedMethod> filter = null, GetMemberOptions options = GetMemberOptions.None)
		{
			return Enumerable.Empty<IMethod>();
		}

		public IList<IUnresolvedTypeDefinition> Parts {
			get { return new List<IUnresolvedTypeDefinition> { new RoslynUnresolvedTypeDefinitionAdapter(namedType, compilation) }; }
		}

		public IList<ITypeParameter> TypeParameters { get { return new List<ITypeParameter>(); } }

		public IList<ITypeDefinition> NestedTypes {
			get { return namedType.GetTypeMembers().Select(t => (ITypeDefinition)new RoslynTypeDefinitionAdapter(t, compilation)).ToList(); }
		}

		public IList<IMember> Members {
			get { return namedType.GetMembers().Where(IsRegularMember).Select(m => (IMember)new RoslynMemberAdapter(m, compilation)).ToList(); }
		}

		static bool IsRegularMember(Microsoft.CodeAnalysis.ISymbol s)
		{
			return s is IMethodSymbol || s is IFieldSymbol || s is IPropertySymbol || s is IEventSymbol;
		}

		public IEnumerable<IField> Fields { get { return namedType.GetMembers().OfType<IFieldSymbol>().Select(m => (IField)new RoslynMemberAdapter(m, compilation)); } }
		public IEnumerable<IMethod> Methods { get { return namedType.GetMembers().OfType<IMethodSymbol>().Select(m => (IMethod)new RoslynMemberAdapter(m, compilation)); } }
		public IEnumerable<IProperty> Properties { get { return namedType.GetMembers().OfType<IPropertySymbol>().Select(m => (IProperty)new RoslynMemberAdapter(m, compilation)); } }
		public IEnumerable<IEvent> Events { get { return namedType.GetMembers().OfType<IEventSymbol>().Select(m => (IEvent)new RoslynMemberAdapter(m, compilation)); } }

		public KnownTypeCode KnownTypeCode { get { return KnownTypeCode.None; } }
		public IType EnumUnderlyingType { get { return namedType.EnumUnderlyingType != null ? new RoslynTypeDefinitionAdapter(namedType.EnumUnderlyingType, compilation) : null; } }
		public FullTypeName FullTypeName { get { return new FullTypeName(FullName); } }

		public bool HasExtensionMethods { get { return false; } }
		public bool IsPartial { get { return namedType.DeclaringSyntaxReferences.Length > 1; } }

		public IMember GetInterfaceImplementation(IMember interfaceMember) { throw new NotImplementedException(); }
		public IList<IMember> GetInterfaceImplementation(IList<IMember> interfaceMembers) { throw new NotImplementedException(); }
	}

	/// <summary>IUnresolvedTypeDefinition backed by the same Roslyn symbol - Roslyn has no separate
	/// "unresolved" phase, so Resolve() just returns the already-resolved adapter.</summary>
	sealed class RoslynUnresolvedTypeDefinitionAdapter : IUnresolvedTypeDefinition
	{
		readonly INamedTypeSymbol namedType;
		readonly ICompilation compilation;

		public RoslynUnresolvedTypeDefinitionAdapter(INamedTypeSymbol namedType, ICompilation compilation)
		{
			this.namedType = namedType;
			this.compilation = compilation;
		}

		public string Name { get { return namedType.Name; } }
		public string FullName { get { return namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat); } }
		public string ReflectionName { get { return FullName; } }
		public string Namespace { get { return namedType.ContainingNamespace != null ? namedType.ContainingNamespace.ToDisplayString() : string.Empty; } }

		public TypeSystem.Accessibility Accessibility { get { return TypeSystem.Accessibility.Public; } }
		public bool IsPrivate { get { return false; } }
		public bool IsPublic { get { return true; } }
		public bool IsProtected { get { return false; } }
		public bool IsInternal { get { return false; } }
		public bool IsProtectedOrInternal { get { return false; } }
		public bool IsProtectedAndInternal { get { return false; } }

		public TypeSystem.SymbolKind SymbolKind { get { return TypeSystem.SymbolKind.TypeDefinition; } }

		public DomRegion Region {
			get {
				var location = namedType.Locations.FirstOrDefault(l => l.IsInSource);
				if (location == null)
					return DomRegion.Empty;
				var span = location.GetLineSpan();
				return new DomRegion(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
					span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
			}
		}

		public DomRegion BodyRegion { get { return Region; } }

		public IUnresolvedTypeDefinition DeclaringTypeDefinition {
			get { return namedType.ContainingType != null ? new RoslynUnresolvedTypeDefinitionAdapter(namedType.ContainingType, compilation) : null; }
		}

		public IUnresolvedFile UnresolvedFile { get { throw new NotImplementedException(); } }
		public IList<IUnresolvedAttribute> Attributes { get { return new List<IUnresolvedAttribute>(); } }

		public bool IsStatic { get { return namedType.IsStatic; } }
		public bool IsAbstract { get { return namedType.IsAbstract; } }
		public bool IsSealed { get { return namedType.IsSealed; } }
		public bool IsShadowing { get { return false; } }
		public bool IsSynthetic { get { return namedType.IsImplicitlyDeclared; } }

		public TypeSystem.TypeKind Kind { get { return new RoslynTypeDefinitionAdapter(namedType, compilation).Kind; } }
		public FullTypeName FullTypeName { get { return new FullTypeName(FullName); } }
		public IList<ITypeReference> BaseTypes { get { return new List<ITypeReference>(); } }
		public IList<IUnresolvedTypeParameter> TypeParameters { get { return new List<IUnresolvedTypeParameter>(); } }
		public IList<IUnresolvedTypeDefinition> NestedTypes {
			get { return namedType.GetTypeMembers().Select(t => (IUnresolvedTypeDefinition)new RoslynUnresolvedTypeDefinitionAdapter(t, compilation)).ToList(); }
		}
		public IList<IUnresolvedMember> Members { get { throw new NotImplementedException(); } }
		public IEnumerable<IUnresolvedMethod> Methods { get { throw new NotImplementedException(); } }
		public IEnumerable<IUnresolvedProperty> Properties { get { throw new NotImplementedException(); } }
		public IEnumerable<IUnresolvedField> Fields { get { throw new NotImplementedException(); } }
		public IEnumerable<IUnresolvedEvent> Events { get { throw new NotImplementedException(); } }

		public bool? HasExtensionMethods { get { return false; } }
		public bool IsPartial { get { return namedType.DeclaringSyntaxReferences.Length > 1; } }
		public bool AddDefaultConstructorIfRequired { get { return false; } }

		IType IUnresolvedTypeDefinition.Resolve(ITypeResolveContext context) { return new RoslynTypeDefinitionAdapter(namedType, compilation); }
		public ITypeResolveContext CreateResolveContext(ITypeResolveContext parentContext) { return parentContext; }
		IType ITypeReference.Resolve(ITypeResolveContext context) { return new RoslynTypeDefinitionAdapter(namedType, compilation); }

		public ISymbolReference ToReference() { throw new NotImplementedException(); }
	}

	/// <summary>IMember (method/field/property/event) backed by a real Microsoft.CodeAnalysis.ISymbol.</summary>
	sealed class RoslynMemberAdapter : RoslynSymbolAdapterBase, IMethod, IField, IProperty, IEvent
	{
		public RoslynMemberAdapter(Microsoft.CodeAnalysis.ISymbol symbol, ICompilation compilation) : base(symbol, compilation)
		{
		}

		public IMember MemberDefinition { get { return this; } }
		public IUnresolvedMember UnresolvedMember { get { throw new NotImplementedException(); } }

		public IType ReturnType {
			get {
				ITypeSymbol type = null;
				var method = Symbol as IMethodSymbol;
				if (method != null) type = method.ReturnType;
				var fieldSymbol = Symbol as IFieldSymbol;
				if (fieldSymbol != null) type = fieldSymbol.Type;
				var property = Symbol as IPropertySymbol;
				if (property != null) type = property.Type;
				var evt = Symbol as IEventSymbol;
				if (evt != null) type = evt.Type;
				var named = type as INamedTypeSymbol;
				return named != null ? new RoslynTypeDefinitionAdapter(named, compilation) : null;
			}
		}

		public IList<IMember> ImplementedInterfaceMembers { get { return new List<IMember>(); } }
		public bool IsExplicitInterfaceImplementation { get { return false; } }
		public bool IsVirtual { get { return Symbol.IsVirtual; } }
		public bool IsOverride { get { return Symbol.IsOverride; } }
		public bool IsOverridable { get { return Symbol.IsVirtual || Symbol.IsAbstract || Symbol.IsOverride; } }

		[Obsolete]
		public IMemberReference ToMemberReference() { throw new NotImplementedException(); }
		IMemberReference IMember.ToReference() { throw new NotImplementedException(); }

		public TypeParameterSubstitution Substitution { get { return TypeParameterSubstitution.Identity; } }
		public IMember Specialize(TypeParameterSubstitution substitution) { return this; }

		public ITypeDefinition DeclaringTypeDefinition {
			get { return Symbol.ContainingType != null ? new RoslynTypeDefinitionAdapter(Symbol.ContainingType, compilation) : null; }
		}

		IType IEntity.DeclaringType { get { return DeclaringTypeDefinition; } }
		public IType DeclaringType { get { return DeclaringTypeDefinition; } }

		public IAssembly ParentAssembly { get { return new RoslynAssemblyAdapter(Symbol.ContainingAssembly, compilation); } }
		public IList<IAttribute> Attributes { get { return new List<IAttribute>(); } }

		// IMethod
		public IList<IUnresolvedMethod> Parts { get { return new List<IUnresolvedMethod>(); } }
		public IList<IAttribute> ReturnTypeAttributes { get { return new List<IAttribute>(); } }
		public IList<ITypeParameter> TypeParameters { get { return new List<ITypeParameter>(); } }
		public bool IsParameterized { get { return false; } }
		public IList<IType> TypeArguments { get { return new List<IType>(); } }
		public bool IsExtensionMethod { get { var m = Symbol as IMethodSymbol; return m != null && m.IsExtensionMethod; } }
		public bool IsConstructor { get { var m = Symbol as IMethodSymbol; return m != null && m.MethodKind == MethodKind.Constructor; } }
		public bool IsDestructor { get { var m = Symbol as IMethodSymbol; return m != null && m.MethodKind == MethodKind.Destructor; } }
		public bool IsOperator { get { var m = Symbol as IMethodSymbol; return m != null && m.MethodKind == MethodKind.UserDefinedOperator; } }
		public bool IsPartial { get { return false; } }
		public bool IsAsync { get { var m = Symbol as IMethodSymbol; return m != null && m.IsAsync; } }
		public bool HasBody { get { return true; } }
		public bool IsAccessor { get { var m = Symbol as IMethodSymbol; return m != null && m.MethodKind == MethodKind.PropertyGet || (m != null && m.MethodKind == MethodKind.PropertySet); } }
		public IMember AccessorOwner { get { var m = Symbol as IMethodSymbol; return m != null && m.AssociatedSymbol != null ? new RoslynMemberAdapter(m.AssociatedSymbol, compilation) : null; } }
		public IMethod ReducedFrom { get { var m = Symbol as IMethodSymbol; return m != null && m.ReducedFrom != null ? new RoslynMemberAdapter(m.ReducedFrom, compilation) : null; } }
		IMethod IMethod.Specialize(TypeParameterSubstitution substitution) { return this; }
		public IList<IParameter> Parameters { get { return new List<IParameter>(); } }

		// IField
		string IVariable.Name { get { return Name; } }
		DomRegion IVariable.Region { get { return Region; } }
		public IType Type { get { return ReturnType; } }
		public bool IsConst { get { var f = Symbol as IFieldSymbol; return f != null && f.IsConst; } }
		public object ConstantValue { get { var f = Symbol as IFieldSymbol; return f != null ? f.ConstantValue : null; } }
		public bool IsReadOnly { get { var f = Symbol as IFieldSymbol; return f != null && f.IsReadOnly; } }
		public bool IsVolatile { get { var f = Symbol as IFieldSymbol; return f != null && f.IsVolatile; } }
		public bool IsFixed { get { return false; } }
		IMemberReference IField.ToReference() { throw new NotImplementedException(); }

		// IProperty
		public bool CanGet { get { var p = Symbol as IPropertySymbol; return p != null && p.GetMethod != null; } }
		public bool CanSet { get { var p = Symbol as IPropertySymbol; return p != null && p.SetMethod != null; } }
		public IMethod Getter { get { var p = Symbol as IPropertySymbol; return p != null && p.GetMethod != null ? new RoslynMemberAdapter(p.GetMethod, compilation) : null; } }
		public IMethod Setter { get { var p = Symbol as IPropertySymbol; return p != null && p.SetMethod != null ? new RoslynMemberAdapter(p.SetMethod, compilation) : null; } }
		public bool IsIndexer { get { var p = Symbol as IPropertySymbol; return p != null && p.IsIndexer; } }

		// IEvent
		public bool CanAdd { get { var e = Symbol as IEventSymbol; return e != null && e.AddMethod != null; } }
		public bool CanRemove { get { var e = Symbol as IEventSymbol; return e != null && e.RemoveMethod != null; } }
		public bool CanInvoke { get { return false; } }
		public IMethod AddAccessor { get { var e = Symbol as IEventSymbol; return e != null && e.AddMethod != null ? new RoslynMemberAdapter(e.AddMethod, compilation) : null; } }
		public IMethod RemoveAccessor { get { var e = Symbol as IEventSymbol; return e != null && e.RemoveMethod != null ? new RoslynMemberAdapter(e.RemoveMethod, compilation) : null; } }
		public IMethod InvokeAccessor { get { return null; } }
	}

	sealed class RoslynAssemblyAdapter : IAssembly
	{
		readonly IAssemblySymbol assembly;
		readonly ICompilation compilation;

		public RoslynAssemblyAdapter(IAssemblySymbol assembly, ICompilation compilation)
		{
			this.assembly = assembly;
			this.compilation = compilation;
		}

		public ICompilation Compilation { get { return compilation; } }
		public IUnresolvedAssembly UnresolvedAssembly { get { throw new NotImplementedException(); } }
		public bool IsMainAssembly { get { return compilation.MainAssembly == this; } }
		public string AssemblyName { get { return assembly != null ? assembly.Name : string.Empty; } }
		public string FullAssemblyName { get { return assembly != null ? assembly.Identity.GetDisplayName() : string.Empty; } }
		public IList<IAttribute> AssemblyAttributes { get { return new List<IAttribute>(); } }
		public IList<IAttribute> ModuleAttributes { get { return new List<IAttribute>(); } }
		public bool InternalsVisibleTo(IAssembly other) { return false; }
		public INamespace RootNamespace { get { throw new NotImplementedException(); } }

		public ITypeDefinition GetTypeDefinition(TopLevelTypeName topLevelTypeName)
		{
			if (assembly == null)
				return null;
			var symbol = assembly.GetTypeByMetadataName(
				string.IsNullOrEmpty(topLevelTypeName.Namespace) ? topLevelTypeName.Name : topLevelTypeName.Namespace + "." + topLevelTypeName.Name);
			return symbol != null ? new RoslynTypeDefinitionAdapter(symbol, compilation) : null;
		}

		public IEnumerable<ITypeDefinition> TopLevelTypeDefinitions { get { return Enumerable.Empty<ITypeDefinition>(); } }
	}
}
