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
	/// Represents a method, constructor, destructor or operator.
	/// </summary>
	public interface IMethod : IParameterizedMember
	{
		/// <summary>
		/// Gets the unresolved method parts.
		/// </summary>
		IList<IUnresolvedMethod> Parts { get; }

		/// <summary>
		/// Gets the attributes associated with the return type.
		/// </summary>
		IList<IAttribute> ReturnTypeAttributes { get; }

		/// <summary>
		/// Gets the type parameters of this method; or an empty list if the method is not generic.
		/// </summary>
		IList<ITypeParameter> TypeParameters { get; }

		/// <summary>
		/// Gets whether this is a generic method that has been parameterized.
		/// </summary>
		bool IsParameterized { get; }

		/// <summary>
		/// Gets the type arguments passed to this method.
		/// </summary>
		IList<IType> TypeArguments { get; }

		bool IsExtensionMethod { get; }
		bool IsConstructor { get; }
		bool IsDestructor { get; }
		bool IsOperator { get; }

		/// <summary>
		/// Gets whether the method is a C#-style partial method.
		/// </summary>
		bool IsPartial { get; }

		/// <summary>
		/// Gets whether the method is a C#-style async method.
		/// </summary>
		bool IsAsync { get; }

		/// <summary>
		/// Gets whether the method has a body.
		/// </summary>
		bool HasBody { get; }

		/// <summary>
		/// Gets whether the method is a property/event accessor.
		/// </summary>
		bool IsAccessor { get; }

		/// <summary>
		/// If this method is an accessor, returns the corresponding property/event.
		/// Otherwise, returns null.
		/// </summary>
		IMember AccessorOwner { get; }

		/// <summary>
		/// If this method is reduced from an extension method return the original method, <c>null</c> otherwise.
		/// </summary>
		IMethod ReducedFrom { get; }

		/// <summary>
		/// Specializes this method with the given substitution.
		/// </summary>
		new IMethod Specialize(TypeParameterSubstitution substitution);
	}
}
