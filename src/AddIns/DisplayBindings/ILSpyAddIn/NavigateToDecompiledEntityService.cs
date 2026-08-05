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

using System;
using System.IO;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.TypeSystem;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.ILSpyAddIn
{
	public class NavigateToDecompiledEntityService : INavigateToEntityService
	{
		public bool NavigateToEntity(IEntity entity)
		{
			if (entity == null)
				throw new ArgumentNullException("entity");
			
			// Get the underlying entity for generic instance members
			if (entity is IMember)
				entity = ((IMember)entity).MemberDefinition;
			
			ITypeDefinition declaringType = (entity as ITypeDefinition) ?? entity.DeclaringTypeDefinition;
			if (declaringType == null)
				return false;
			// get the top-level type
			while (declaringType.DeclaringTypeDefinition != null)
				declaringType = declaringType.DeclaringTypeDefinition;
			
			FileName assemblyLocation = declaringType.ParentAssembly.GetRuntimeAssemblyLocation();
			if (assemblyLocation != null && File.Exists(assemblyLocation)) {
				NavigateTo(assemblyLocation, declaringType.ReflectionName, MemberLocationKey.Create(entity));
				return true;
			}
			return false;
		}
		
		public static System.Threading.Tasks.Task NavigateTo(FileName assemblyFile, string typeName, string memberKey)
		{
			if (assemblyFile == null)
				throw new ArgumentNullException("assemblyFile");
			if (string.IsNullOrEmpty(typeName))
				throw new ArgumentException("typeName is null or empty");

			return NavigateTo(new DecompiledTypeReference(assemblyFile, new TopLevelTypeName(typeName)), memberKey);
		}

		/// <summary>
		/// Opens/reuses a native document for the whole module (doc/technotes/ilspy.md "Unify C#
		/// document hosting" step 3 - the AssemblyTreeNode-selection counterpart of
		/// <see cref="NavigateTo(FileName, string, string)"/>'s single-type case) -
		/// <see cref="DecompiledTypeReference.IsWholeModule"/> already supports this, only
		/// <see cref="NavigateTo(FileName, string, string)"/>'s typeName-required guard didn't.
		/// </summary>
		public static System.Threading.Tasks.Task NavigateToModule(FileName assemblyFile)
		{
			if (assemblyFile == null)
				throw new ArgumentNullException("assemblyFile");

			return NavigateTo(new DecompiledTypeReference(assemblyFile, default(TopLevelTypeName)), null);
		}

		// Returns the target document's DecompilationTask - a fresh one if just created, or the
		// (possibly already-completed) one from the reused existing document - so callers like
		// IlSpyWorkspaceHost.OnSelectionChangedAsync can actually await decompile completion
		// instead of firing-and-forgetting into the workbench.
		static System.Threading.Tasks.Task NavigateTo(DecompiledTypeReference target, string memberKey)
		{
			foreach (var viewContent in SD.Workbench.ViewContentCollection.OfType<DecompiledViewContent>()) {
				if (viewContent.DecompiledTypeName.Equals(target)) {
					viewContent.WorkbenchWindow.SelectWindow();
					viewContent.JumpToMember(memberKey);
					return viewContent.DecompilationTask;
				}
			}
			var newViewContent = new DecompiledViewContent(target, memberKey);
			SD.Workbench.ShowView(newViewContent);
			return newViewContent.DecompilationTask;
		}
	}
}
