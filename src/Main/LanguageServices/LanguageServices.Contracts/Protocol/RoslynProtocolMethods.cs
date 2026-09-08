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

namespace ICSharpCode.SharpDevelop.LanguageServices.Protocol
{
	/// <summary>
	/// The wire method names, in one place so the client and the host cannot disagree about them.
	///
	/// LSP's own names are used verbatim where the request maps onto LSP, so the traffic is
	/// readable to anyone who knows LSP; the <c>roslyn/</c> prefix marks the documented extensions
	/// (doc/technotes/roslyn-host-process.md §4). Keeping them as constants rather than nameof() is
	/// deliberate: the wire name must not change just because a C# method was renamed.
	/// </summary>
	public static class RoslynProtocolMethods
	{
		public const string DidChange = "textDocument/didChange";
		public const string SemanticTokens = "textDocument/semanticTokens/full";
		public const string SymbolName = "roslyn/symbolName";
		public const string SymbolKind = "roslyn/symbolKind";
		public const string ValidIdentifier = "roslyn/validIdentifier";
		public const string DocumentStatus = "roslyn/document/status";
		public const string Definition = "textDocument/definition";
		public const string References = "textDocument/references";
		public const string Completion = "textDocument/completion";
		public const string Hover = "textDocument/hover";
		public const string Diagnostics = "textDocument/diagnostic";
		public const string DocumentSymbol = "textDocument/documentSymbol";
		public const string Formatting = "textDocument/formatting";
		public const string Rename = "textDocument/rename";
		public const string CodeAction = "textDocument/codeAction";
		public const string CodeActionApply = "codeAction/apply";
		public const string Supertypes = "typeHierarchy/supertypes";
		public const string Subtypes = "typeHierarchy/subtypes";

		public const string Status = "roslyn/status";
		public const string LensDocument = "roslyn/lens/document";
		public const string FindMember = "roslyn/findMember";
		public const string HelpKeyword = "roslyn/helpKeyword";
		public const string ContainingType = "roslyn/containingType";
		public const string ExtractInterfaceInfo = "roslyn/extractInterface/info";
		public const string ExtractInterfaceApply = "roslyn/extractInterface/apply";

		/// <summary>
		/// <c>roslyn/project/load</c>: the IDE pushes the evaluated project graph. The host does NOT
		/// discover projects itself - MSBuild evaluation stays in the IDE - which is the second
		/// reason a stock LSP server is the wrong answer here (§4).
		/// </summary>
		public const string ProjectLoad = "roslyn/project/load";

		/// <summary><c>roslyn/solution/closed</c>: drop all project state.</summary>
		public const string SolutionClosed = "roslyn/solution/closed";
	}
}
