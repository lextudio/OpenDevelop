// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
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

using System.Linq;
using ICSharpCode.TypeSystem;

namespace ICSharpCode.FormsDesigner.Services
{
	/// <summary>
	/// ICSharpCode.TypeSystem.Abstractions' <see cref="ICompilation"/> only exposes
	/// <c>FindType(KnownTypeCode)</c> - real NRefactory/Roslyn type systems also let you look a type up
	/// by its full name, which several designer services need (finding the resource-designer base
	/// class, checking whether a type derives from <c>System.ComponentModel.IComponent</c>, and so on).
	/// Same lookup shape as WpfDesign.AddIn's AbstractEventHandlerService.FindType.
	/// </summary>
	static class TypeSystemHelper
	{
		public static ITypeDefinition FindType(this ICompilation compilation, FullTypeName fullTypeName)
		{
			if (compilation?.MainAssembly == null)
				return null;

			return fullTypeName.IsNested
				? compilation.MainAssembly.TopLevelTypeDefinitions.FirstOrDefault(t => t.FullTypeName == fullTypeName.GetDeclaringType())
				: compilation.MainAssembly.GetTypeDefinition(fullTypeName.TopLevelTypeName);
		}

		public static ITypeDefinition FindType(this ICompilation compilation, TopLevelTypeName topLevelTypeName)
		{
			return compilation?.MainAssembly?.GetTypeDefinition(topLevelTypeName);
		}
	}
}
