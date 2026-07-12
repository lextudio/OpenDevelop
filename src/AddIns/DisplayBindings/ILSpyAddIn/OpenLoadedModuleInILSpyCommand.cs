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
using System.Reflection;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.ILSpyAddIn
{
	public sealed class OpenLoadedModuleInILSpyCommand : AbstractMenuCommand
	{
		public override bool IsEnabled {
			get { return !string.IsNullOrEmpty(GetModulePath(Owner)); }
			set { }
		}
		
		public override void Run()
		{
			string path = GetModulePath(Owner);
			if (string.IsNullOrEmpty(path))
				return;
			
			SD.FileService.OpenFile(new DecompiledTypeReference(FileName.Create(path), default(ICSharpCode.TypeSystem.TopLevelTypeName)).ToFileName());
		}
		
		static string GetModulePath(object owner)
		{
			if (owner == null)
				return null;
			
			PropertyInfo pathProperty = owner.GetType().GetProperty("Path", BindingFlags.Instance | BindingFlags.Public);
			return pathProperty != null ? pathProperty.GetValue(owner, null) as string : null;
		}
	}
}
