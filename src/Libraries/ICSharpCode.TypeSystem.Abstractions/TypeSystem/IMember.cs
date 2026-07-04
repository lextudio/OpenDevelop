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
	/// Method/field/property/event.
	/// </summary>
	public interface IMemberReference : ISymbolReference
	{
		/// <summary>
		/// Gets the declaring type reference for the member.
		/// </summary>
		ITypeReference DeclaringTypeReference { get; }

		/// <summary>
		/// Resolves the member.
		/// </summary>
		new IMember Resolve(ITypeResolveContext context);
	}

	/// <summary>
	/// Method/field/property/event.
	/// </summary>
	public interface IMember : IEntity
	{
		/// <summary>
		/// Gets the original member definition for this member.
		/// Returns <c>this</c> if this is not a specialized member.
		/// </summary>
		IMember MemberDefinition { get; }

		/// <summary>
		/// Gets the unresolved member instance from which this member was created.
		/// This property may return <c>null</c> for special members that do not have a corresponding unresolved member instance.
		/// </summary>
		IUnresolvedMember UnresolvedMember { get; }

		/// <summary>
		/// Gets the return type of this member.
		/// This property never returns <c>null</c>.
		/// </summary>
		IType ReturnType { get; }

		/// <summary>
		/// Gets the interface members implemented by this member (both implicitly and explicitly).
		/// </summary>
		IList<IMember> ImplementedInterfaceMembers { get; }

		/// <summary>
		/// Gets whether this member is explicitly implementing an interface.
		/// </summary>
		bool IsExplicitInterfaceImplementation { get; }

		/// <summary>
		/// Gets if the member is virtual.
		/// </summary>
		bool IsVirtual { get; }

		/// <summary>
		/// Gets whether this member is overriding another member.
		/// </summary>
		bool IsOverride { get; }

		/// <summary>
		/// Gets if the member can be overridden.
		/// </summary>
		bool IsOverridable { get; }

		/// <summary>
		/// Creates a member reference that can be used to rediscover this member in another compilation.
		/// </summary>
		[Obsolete("Use the ToReference method instead.")]
		IMemberReference ToMemberReference();

		/// <summary>
		/// Creates a member reference that can be used to rediscover this member in another compilation.
		/// </summary>
		new IMemberReference ToReference();

		/// <summary>
		/// Gets the substitution belonging to this specialized member.
		/// Returns TypeParameterSubstitution.Identity for not specialized members.
		/// </summary>
		TypeParameterSubstitution Substitution { get; }

		/// <summary>
		/// Specializes this member with the given substitution.
		/// </summary>
		IMember Specialize(TypeParameterSubstitution substitution);
	}
}
