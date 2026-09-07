// Copyright (c) 2026 LeXtudio Inc.
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

using ICSharpCode.ILSpyX.TreeView;

namespace ICSharpCode.SharpDevelop.Dom
{
	/// <summary>
	/// A tree node that stands for a model object, and can hand that object back.
	///
	/// This exists so OpenDevelop does not have to patch ILSpyX's <see cref="SharpTreeNode"/>.
	/// A fork of ILSpy used to add Model/GetModel() straight onto SharpTreeNode, which forced the
	/// whole ILSpyX/Decompiler pair to be consumed as submodule PROJECT references rather than as
	/// the published ICSharpCode.ILSpyX / ICSharpCode.Decompiler packages. Declaring the concept
	/// here instead keeps it on OpenDevelop's side of the boundary: nodes opt in, and code that
	/// reads a model does so through <see cref="ModelCollectionTreeNode.ModelOf"/>.
	///
	/// The partial-class route is not available for this - C# only merges partial declarations
	/// within one assembly, so a `partial class SharpTreeNode` in an OpenDevelop project shadows
	/// ILSpyX's type instead of extending it (CS0436).
	/// </summary>
	public interface ITreeNodeModelOwner
	{
		/// <summary>The model object this node represents; null when the node stands for nothing.</summary>
		object Model { get; }
	}
}
