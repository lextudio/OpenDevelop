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
using System.Windows.Documents;
using ICSharpCode.TypeSystem;

namespace ICSharpCode.SharpDevelop.Editor
{
	/// <summary>
	/// Provides helper methods to create nicely formatted FlowDocuments from NRefactory XmlDoc.
	/// </summary>
	public static class XmlDocFormatter
	{
		public static FlowDocument CreateTooltip(IType type, bool useFullyQualifiedMemberNames = true)
		{
			string header;
			if (type is ITypeDefinition)
				header = useFullyQualifiedMemberNames ? ((ITypeDefinition)type).FullName : type.Name;
			else
				header = useFullyQualifiedMemberNames ? type.FullName : type.Name;
			
			DocumentationUIBuilder b = new DocumentationUIBuilder();
			b.AddCodeBlock(header, keepLargeMargin: true);
			
			ITypeDefinition entity = type.GetDefinition();
			if (entity != null) {
				var documentation = XmlDocumentationElement.Get(entity);
				if (documentation != null) {
					foreach (var child in documentation.Children) {
						b.AddDocumentationElement(child);
					}
				}
			}
			return b.CreateFlowDocument();
		}
		
		public static FlowDocument CreateTooltip(IEntity entity, bool useFullyQualifiedMemberNames = true)
		{
			string header = useFullyQualifiedMemberNames ? entity.FullName : entity.Name;
			var documentation = XmlDocumentationElement.Get(entity);
			
			DocumentationUIBuilder b = new DocumentationUIBuilder();
			b.AddCodeBlock(header, keepLargeMargin: true);
			if (documentation != null) {
				foreach (var child in documentation.Children) {
					b.AddDocumentationElement(child);
				}
			}
			return b.CreateFlowDocument();
		}
		
		public static FlowDocument CreateTooltip(ISymbol symbol)
		{
			string header = symbol.Name;
			
			if (symbol is IParameter) {
				header = "parameter " + header;
			} else if (symbol is IVariable) {
				header = "local variable " + header;
			}
			
			DocumentationUIBuilder b = new DocumentationUIBuilder();
			b.AddCodeBlock(header, keepLargeMargin: true);
			return b.CreateFlowDocument();
		}
	}
}
