// Copyright (c) 2010-2013 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;

namespace ICSharpCode.TypeSystem
{
	/// <summary>
	/// This interface represents a resolved type in the type system.
	/// </summary>
	public interface IType : INamedElement, IEquatable<IType>
	{
		/// <summary>
		/// Gets the type kind.
		/// </summary>
		TypeKind Kind { get; }

		/// <summary>
		/// Gets whether the type is a reference type or value type.
		/// </summary>
		bool? IsReferenceType { get; }

		/// <summary>
		/// Gets the underlying type definition.
		/// Can return null for types which do not have a type definition.
		/// </summary>
		ITypeDefinition GetDefinition();

		/// <summary>
		/// Gets the parent type, if this is a nested type.
		/// Returns null for top-level types.
		/// </summary>
		IType DeclaringType { get; }

		/// <summary>
		/// Gets the number of type parameters.
		/// </summary>
		int TypeParameterCount { get; }

		/// <summary>
		/// Gets the type arguments passed to this type.
		/// </summary>
		IList<IType> TypeArguments { get; }

		/// <summary>
		/// If true the type represents an instance of a generic type.
		/// </summary>
		bool IsParameterized { get; }

		/// <summary>
		/// Calls ITypeVisitor.Visit for this type.
		/// </summary>
		IType AcceptVisitor(TypeVisitor visitor);

		/// <summary>
		/// Calls ITypeVisitor.Visit for all children of this type, and reconstructs this type with the children based
		/// on the return values of the visit calls.
		/// </summary>
		IType VisitChildren(TypeVisitor visitor);

		/// <summary>
		/// Gets the direct base types.
		/// </summary>
		IEnumerable<IType> DirectBaseTypes { get; }

		/// <summary>
		/// Creates a type reference that can be used to look up a type equivalent to this type in another compilation.
		/// </summary>
		ITypeReference ToTypeReference();

		/// <summary>
		/// Gets a type visitor that performs the substitution of class type parameters with the type arguments
		/// of this parameterized type.
		/// Returns TypeParameterSubstitution.Identity if the type is not parametrized.
		/// </summary>
		TypeParameterSubstitution GetSubstitution();

		/// <summary>
		/// Gets a type visitor that performs the substitution of class type parameters with the type arguments
		/// of this parameterized type,
		/// and also substitutes method type parameters with the specified method type arguments.
		/// Returns TypeParameterSubstitution.Identity if the type is not parametrized.
		/// </summary>
		TypeParameterSubstitution GetSubstitution(IList<IType> methodTypeArguments);

		/// <summary>
		/// Gets inner classes (including inherited inner classes).
		/// </summary>
		IEnumerable<IType> GetNestedTypes(Predicate<ITypeDefinition> filter = null, GetMemberOptions options = GetMemberOptions.None);

		/// <summary>
		/// Gets inner classes (including inherited inner classes)
		/// that have <c>typeArguments.Count</c> additional type parameters.
		/// </summary>
		IEnumerable<IType> GetNestedTypes(IList<IType> typeArguments, Predicate<ITypeDefinition> filter = null, GetMemberOptions options = GetMemberOptions.None);

		/// <summary>
		/// Gets all instance constructors for this type.
		/// </summary>
		IEnumerable<IMethod> GetConstructors(Predicate<IUnresolvedMethod> filter = null, GetMemberOptions options = GetMemberOptions.IgnoreInheritedMembers);

		/// <summary>
		/// Gets all methods that can be called on this type.
		/// </summary>
		IEnumerable<IMethod> GetMethods(Predicate<IUnresolvedMethod> filter = null, GetMemberOptions options = GetMemberOptions.None);

		/// <summary>
		/// Gets all generic methods that can be called on this type with the specified type arguments.
		/// </summary>
		IEnumerable<IMethod> GetMethods(IList<IType> typeArguments, Predicate<IUnresolvedMethod> filter = null, GetMemberOptions options = GetMemberOptions.None);

		/// <summary>
		/// Gets all properties that can be called on this type.
		/// </summary>
		IEnumerable<IProperty> GetProperties(Predicate<IUnresolvedProperty> filter = null, GetMemberOptions options = GetMemberOptions.None);

		/// <summary>
		/// Gets all fields that can be accessed on this type.
		/// </summary>
		IEnumerable<IField> GetFields(Predicate<IUnresolvedField> filter = null, GetMemberOptions options = GetMemberOptions.None);

		/// <summary>
		/// Gets all events that can be accessed on this type.
		/// </summary>
		IEnumerable<IEvent> GetEvents(Predicate<IUnresolvedEvent> filter = null, GetMemberOptions options = GetMemberOptions.None);

		/// <summary>
		/// Gets all members that can be called on this type.
		/// </summary>
		IEnumerable<IMember> GetMembers(Predicate<IUnresolvedMember> filter = null, GetMemberOptions options = GetMemberOptions.None);

		/// <summary>
		/// Gets all accessors belonging to properties or events on this type.
		/// </summary>
		IEnumerable<IMethod> GetAccessors(Predicate<IUnresolvedMethod> filter = null, GetMemberOptions options = GetMemberOptions.None);
	}

	[Flags]
	public enum GetMemberOptions
	{
		/// <summary>
		/// No options specified - this is the default.
		/// Members will be specialized, and inherited members will be included.
		/// </summary>
		None = 0x00,
		/// <summary>
		/// Do not specialize the returned members - directly return the definitions.
		/// </summary>
		ReturnMemberDefinitions = 0x01,
		/// <summary>
		/// Do not list inherited members - only list members defined directly on this type.
		/// </summary>
		IgnoreInheritedMembers = 0x02
	}
}
