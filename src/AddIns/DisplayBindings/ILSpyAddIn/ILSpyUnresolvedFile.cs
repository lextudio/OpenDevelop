// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;

using ICSharpCode.TypeSystem;

namespace ICSharpCode.ILSpyAddIn
{
	public sealed class ILSpyUnresolvedFile : IUnresolvedFile
	{
		readonly DecompiledTypeReference name;
		
		public ILSpyUnresolvedFile(DecompiledTypeReference name)
		{
			this.name = name ?? throw new ArgumentNullException("name");
		}
		
		public string FileName {
			get { return name.ToFileName(); }
		}
		
		public DateTime? LastWriteTime { get; set; }
		
		public IList<IUnresolvedTypeDefinition> TopLevelTypeDefinitions {
			get { return new List<IUnresolvedTypeDefinition>(); }
		}
		
		public IList<IUnresolvedAttribute> AssemblyAttributes {
			get { return new List<IUnresolvedAttribute>(); }
		}
		
		public IList<IUnresolvedAttribute> ModuleAttributes {
			get { return new List<IUnresolvedAttribute>(); }
		}
		
		public IUnresolvedTypeDefinition GetTopLevelTypeDefinition(TextLocation location)
		{
			return null;
		}
		
		public IUnresolvedTypeDefinition GetInnermostTypeDefinition(TextLocation location)
		{
			return null;
		}
		
		public IUnresolvedMember GetMember(TextLocation location)
		{
			return null;
		}
		
		public IList<Error> Errors {
			get { return new List<Error>(); }
		}
	}
}
