// Small inheritance-walking helpers over Microsoft.CodeAnalysis symbols, replacing the old
// ICSharpCode.NRefactory.TypeSystem.InheritanceHelper (see doc/technotes/csharp-roslyn.md).

using System.Linq;

using Microsoft.CodeAnalysis;

namespace ICSharpCode.SharpDevelop.Roslyn
{
	public static class RoslynSymbolHelper
	{
		/// <summary>
		/// Gets the member that <paramref name="member"/> overrides or (implicitly/explicitly) implements, if any.
		/// </summary>
		public static ISymbol GetBaseMember(ISymbol member)
		{
			var method = member as IMethodSymbol;
			if (method != null) {
				if (method.ExplicitInterfaceImplementations.Length > 0)
					return method.ExplicitInterfaceImplementations[0];
				if (method.OverriddenMethod != null)
					return method.OverriddenMethod;
			}

			var property = member as IPropertySymbol;
			if (property != null) {
				if (property.ExplicitInterfaceImplementations.Length > 0)
					return property.ExplicitInterfaceImplementations[0];
				if (property.OverriddenProperty != null)
					return property.OverriddenProperty;
			}

			var evt = member as IEventSymbol;
			if (evt != null) {
				if (evt.ExplicitInterfaceImplementations.Length > 0)
					return evt.ExplicitInterfaceImplementations[0];
				if (evt.OverriddenEvent != null)
					return evt.OverriddenEvent;
			}

			return GetImplicitlyImplementedInterfaceMember(member);
		}

		static ISymbol GetImplicitlyImplementedInterfaceMember(ISymbol member)
		{
			var containingType = member.ContainingType;
			if (containingType == null)
				return null;
			foreach (var iface in containingType.AllInterfaces) {
				foreach (var interfaceMember in iface.GetMembers()) {
					var implementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
					if (implementation != null && SymbolEqualityComparer.Default.Equals(implementation, member))
						return interfaceMember;
				}
			}
			return null;
		}

		/// <summary>
		/// Finds the member declared in <paramref name="derivedType"/> that overrides/implements <paramref name="baseMember"/>.
		/// </summary>
		public static ISymbol GetDerivedMember(ISymbol baseMember, INamedTypeSymbol derivedType)
		{
			if (baseMember.ContainingType != null && baseMember.ContainingType.TypeKind == TypeKind.Interface)
				return derivedType.FindImplementationForInterfaceMember(baseMember);

			return derivedType.GetMembers().FirstOrDefault(
				candidate => SymbolEqualityComparer.Default.Equals(GetBaseMember(candidate), baseMember));
		}
	}
}
