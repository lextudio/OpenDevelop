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
using System.Linq;
using System.Text;

using ICSharpCode.TypeSystem;

namespace ICSharpCode.ILSpyAddIn
{
	static class OpenDevelopIdStringProvider
	{
		public static string GetIdString(IEntity entity)
		{
			if (entity == null)
				throw new ArgumentNullException("entity");
			
			switch (entity.SymbolKind) {
				case SymbolKind.TypeDefinition:
					return "T:" + GetTypeName((IType)entity);
				case SymbolKind.Field:
					return "F:" + GetMemberPrefix(entity) + GetMemberName(entity);
				case SymbolKind.Property:
				case SymbolKind.Indexer:
					return "P:" + GetMemberPrefix(entity) + GetMemberName(entity) + GetParameterList(entity);
				case SymbolKind.Event:
					return "E:" + GetMemberPrefix(entity) + GetMemberName(entity);
				default:
					return "M:" + GetMemberPrefix(entity) + GetMemberName(entity) + GetGenericMethodSuffix(entity) + GetParameterList(entity) + GetConversionReturnType(entity);
			}
		}
		
		static string GetMemberPrefix(IEntity entity)
		{
			var declaringType = entity.DeclaringTypeDefinition;
			return declaringType != null ? GetTypeName(declaringType) + "." : string.Empty;
		}
		
		static string GetMemberName(IEntity entity)
		{
			return entity.Name.Replace('.', '#').Replace('<', '{').Replace('>', '}');
		}
		
		static string GetGenericMethodSuffix(IEntity entity)
		{
			var method = entity as IMethod;
			return method != null && method.TypeParameters.Count > 0 ? "``" + method.TypeParameters.Count : string.Empty;
		}
		
		static string GetParameterList(IEntity entity)
		{
			var parameterizedMember = entity as IParameterizedMember;
			if (parameterizedMember == null || parameterizedMember.Parameters.Count == 0)
				return string.Empty;
			
			StringBuilder builder = new StringBuilder();
			builder.Append('(');
			for (int i = 0; i < parameterizedMember.Parameters.Count; i++) {
				if (i > 0)
					builder.Append(',');
				builder.Append(GetTypeName(parameterizedMember.Parameters[i].Type));
			}
			builder.Append(')');
			return builder.ToString();
		}
		
		static string GetConversionReturnType(IEntity entity)
		{
			var method = entity as IMethod;
			if (method == null || (method.Name != "op_Implicit" && method.Name != "op_Explicit"))
				return string.Empty;
			return "~" + GetTypeName(method.ReturnType);
		}
		
		static string GetTypeName(IType type)
		{
			if (type == null)
				throw new ArgumentNullException("type");
			
			if (type.Kind == TypeKind.Dynamic)
				return "System.Object";
			if (type.Kind == TypeKind.TypeParameter) {
				var typeParameter = (ITypeParameter)type;
				return (typeParameter.OwnerType == SymbolKind.Method ? "``" : "`") + typeParameter.Index;
			}
			
			string name = type.ReflectionName;
			if (type.TypeArguments.Count == 0)
				return NormalizeTypeName(name);
			
			return NormalizeTypeName(name) + "{" + string.Join(",", type.TypeArguments.Select(GetTypeName)) + "}";
		}
		
		static string NormalizeTypeName(string reflectionName)
		{
			return reflectionName.Replace('+', '.');
		}
	}
}
