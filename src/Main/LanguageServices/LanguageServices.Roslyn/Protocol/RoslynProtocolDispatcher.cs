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

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.LanguageServices.Protocol
{
	/// <summary>
	/// Host-side entry point: turns a method name plus arguments back into a call on an
	/// <see cref="IRoslynLanguageProtocol"/>.
	///
	/// This is the mirror of <see cref="RemoteRoslynLanguageProtocol"/>, and keeping the pair
	/// symmetrical is the point - both name their methods from
	/// <see cref="RoslynProtocolMethods"/>, so a method that exists on one side and not the other
	/// is a compile error on the interface rather than a runtime "method not found" discovered by a
	/// user.
	///
	/// The host process itself owns process lifetime, transport and the Roslyn workspace; all it
	/// needs from this assembly is this dispatcher plus an
	/// <see cref="InProcessRoslynLanguageProtocol"/> wrapping its own language service. That is
	/// what keeps the host thin: it runs the same adapter the IDE runs today.
	/// </summary>
	public sealed class RoslynProtocolDispatcher
	{
		readonly IRoslynLanguageProtocol protocol;

		public RoslynProtocolDispatcher(IRoslynLanguageProtocol protocol)
		{
			this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
		}

		/// <summary>Every method name this dispatcher answers.</summary>
		public static IReadOnlyList<string> SupportedMethods { get; } = new[]
		{
			RoslynProtocolMethods.DidChange,
			RoslynProtocolMethods.SemanticTokens,
			RoslynProtocolMethods.SymbolName,
			RoslynProtocolMethods.SymbolKind,
			RoslynProtocolMethods.ValidIdentifier,
			RoslynProtocolMethods.DocumentStatus,
			RoslynProtocolMethods.Definition,
			RoslynProtocolMethods.References,
			RoslynProtocolMethods.Completion,
			RoslynProtocolMethods.Hover,
			RoslynProtocolMethods.Diagnostics,
			RoslynProtocolMethods.DocumentSymbol,
			RoslynProtocolMethods.Formatting,
			RoslynProtocolMethods.Rename,
			RoslynProtocolMethods.CodeAction,
			RoslynProtocolMethods.CodeActionApply,
			RoslynProtocolMethods.Supertypes,
			RoslynProtocolMethods.Subtypes,
			RoslynProtocolMethods.ProjectLoad,
			RoslynProtocolMethods.SolutionClosed,
			RoslynProtocolMethods.Status,
			RoslynProtocolMethods.LensDocument,
			RoslynProtocolMethods.FindMember,
			RoslynProtocolMethods.HelpKeyword,
			RoslynProtocolMethods.ContainingType,
			RoslynProtocolMethods.ExtractInterfaceInfo,
			RoslynProtocolMethods.ExtractInterfaceApply,
		};

		public bool CanHandle(string method) => Array.IndexOf((string[])SupportedMethods, method) >= 0;

		/// <summary>
		/// Invokes one request. <paramref name="arguments"/> is the already-deserialised argument
		/// object; the host's RPC layer decides how it arrives on the wire, which is why this takes
		/// a reader rather than a JSON type - the dispatcher stays independent of the serialiser.
		/// </summary>
		public async Task<object?> DispatchAsync(string method, IRoslynProtocolArguments arguments, CancellationToken cancellationToken)
		{
			if (arguments == null)
				throw new ArgumentNullException(nameof(arguments));

			switch (method)
			{
				case RoslynProtocolMethods.DidChange:
					await protocol.TextDocumentDidChangeAsync(arguments.Document(), arguments.Text(), cancellationToken).ConfigureAwait(false);
					return null;
				case RoslynProtocolMethods.SemanticTokens:
					return await protocol.TextDocumentSemanticTokensAsync(arguments.Document(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.SymbolName:
					return await protocol.RoslynSymbolNameAsync(arguments.Document(), arguments.Offset(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.SymbolKind:
					return await protocol.RoslynSymbolKindAsync(arguments.Document(), arguments.Offset(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.ValidIdentifier:
					return await protocol.RoslynValidIdentifierAsync(arguments.Document(), arguments.NewName(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.DocumentStatus:
					return await protocol.RoslynDocumentStatusAsync(arguments.Document(), cancellationToken, arguments.IncludeDiagnostics()).ConfigureAwait(false);
				case RoslynProtocolMethods.Definition:
					return await protocol.TextDocumentDefinitionAsync(arguments.Document(), arguments.Offset(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.References:
					return await protocol.TextDocumentReferencesAsync(arguments.Document(), arguments.Offset(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.Completion:
					return await protocol.TextDocumentCompletionAsync(arguments.Document(), arguments.Offset(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.Hover:
					return await protocol.TextDocumentHoverAsync(arguments.Document(), arguments.Offset(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.Diagnostics:
					return await protocol.TextDocumentDiagnosticsAsync(arguments.Document(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.DocumentSymbol:
					return await protocol.TextDocumentSymbolAsync(arguments.Document(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.Formatting:
					return await protocol.TextDocumentFormattingAsync(arguments.Document(), arguments.Span(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.Rename:
					return await protocol.TextDocumentRenameAsync(arguments.Document(), arguments.Offset(), arguments.NewName(), cancellationToken,
						arguments.RenameOverloads(), arguments.RenameInStrings(), arguments.RenameInComments()).ConfigureAwait(false);
				case RoslynProtocolMethods.CodeAction:
					return await protocol.TextDocumentCodeActionAsync(arguments.Document(), arguments.Span() ?? default, cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.CodeActionApply:
					return await protocol.CodeActionApplyAsync(arguments.Document(), arguments.ActionId(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.Supertypes:
					return await protocol.TypeHierarchySupertypesAsync(arguments.Document(), arguments.Offset(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.Subtypes:
					return await protocol.TypeHierarchySubtypesAsync(arguments.Document(), arguments.Offset(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.ProjectLoad:
					await protocol.RoslynProjectLoadAsync(arguments.Snapshot(), cancellationToken).ConfigureAwait(false);
					return null;
				case RoslynProtocolMethods.SolutionClosed:
					await protocol.RoslynSolutionClosedAsync(cancellationToken).ConfigureAwait(false);
					return null;
				case RoslynProtocolMethods.Status:
					return await protocol.RoslynStatusAsync(cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.LensDocument:
					return await protocol.RoslynLensDocumentAsync(arguments.Document(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.FindMember:
					return await protocol.RoslynFindMemberAsync(arguments.TypeFullName(), arguments.MethodName(), arguments.ParameterCount(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.HelpKeyword:
					return await protocol.RoslynHelpKeywordAsync(arguments.Document(), arguments.Offset(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.ContainingType:
					return await protocol.RoslynContainingTypeAsync(arguments.Document(), arguments.Offset(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.ExtractInterfaceInfo:
					return await protocol.RoslynExtractInterfaceInfoAsync(arguments.Document(), arguments.Offset(), cancellationToken).ConfigureAwait(false);
				case RoslynProtocolMethods.ExtractInterfaceApply:
					return await protocol.RoslynExtractInterfaceApplyAsync(arguments.Document(), arguments.Offset(),
						arguments.InterfaceName(), arguments.MemberIds(), arguments.AddInterfaceToClass(), arguments.IncludeComments(), cancellationToken).ConfigureAwait(false);
				default:
					throw new NotSupportedException("Unknown protocol method '" + method + "'.");
			}
		}
	}

	/// <summary>
	/// Reads the arguments of one protocol request. An interface rather than a concrete DTO per
	/// method: the host's serialiser supplies the implementation, so the dispatcher above does not
	/// depend on how the payload was encoded.
	/// </summary>
	public interface IRoslynProtocolArguments
	{
		TextDocumentIdentifier Document();
		int Offset();
		string Text();
		string NewName();
		string ActionId();
		TextSpan? Span();
		LanguageServiceProjectSnapshot Snapshot();
		string TypeFullName();
		string MethodName();
		int? ParameterCount();
		string InterfaceName();
		IReadOnlyList<string> MemberIds();
		bool AddInterfaceToClass();
		bool IncludeComments();
		bool IncludeDiagnostics();
		bool RenameOverloads();
		bool RenameInStrings();
		bool RenameInComments();
	}
}
