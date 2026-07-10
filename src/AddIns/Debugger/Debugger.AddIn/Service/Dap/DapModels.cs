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

using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Debugger.AddIn.Service.Dap
{
	/// <summary>
	/// Parsed subset of the DAP "initialize" response's capabilities body, plus the raw JSON for
	/// callers that need a field this class doesn't model yet - future debug adapters (this
	/// engine isn't tied to SharpDbg specifically) may support capabilities we haven't seen.
	/// </summary>
	public sealed class DapCapabilities
	{
		public bool SupportsConditionalBreakpoints { get; set; }
		public bool SupportsHitConditionalBreakpoints { get; set; }
		public bool SupportsFunctionBreakpoints { get; set; }
		public bool SupportsLogPoints { get; set; }

		public JsonObject Raw { get; set; }

		/// <summary>Looks up an arbitrary boolean capability by its DAP name (e.g. "supportsDataBreakpoints").</summary>
		public bool Supports(string capabilityName)
		{
			return Raw != null && Raw[capabilityName] != null && Raw[capabilityName].GetValue<bool>();
		}
	}

	/// <summary>
	/// Lightweight, DAP-shaped replacements for the old Debugger.Core (ICorDebug) object model.
	/// Unlike Debugger.Core's Process/Thread/StackFrame/Value, these are plain snapshots fetched
	/// on demand from the adapter (SharpDbg) - there is no live, reflection-style navigation.
	/// </summary>
	public sealed class DapThreadInfo
	{
		public int Id { get; set; }
		public string Name { get; set; }
	}

	public sealed class DapStackFrameInfo
	{
		public int Id { get; set; }
		public int ThreadId { get; set; }
		public string Name { get; set; }
		public string FilePath { get; set; }
		public int Line { get; set; }
		public int Column { get; set; }
		public int EndLine { get; set; }
		public int EndColumn { get; set; }
	}

	public sealed class DapScopeInfo
	{
		public string Name { get; set; }
		public int VariablesReference { get; set; }
		public bool Expensive { get; set; }
	}

	public sealed class DapVariableInfo
	{
		public string Name { get; set; }
		public string Value { get; set; }
		public string Type { get; set; }
		public int VariablesReference { get; set; }
		public string EvaluateName { get; set; }
	}

	public sealed class DapModuleInfo
	{
		public string Id { get; set; }
		public string Name { get; set; }
		public string Path { get; set; }
	}

	public sealed class DapEvaluateResult
	{
		public string Value { get; set; }
		public string Type { get; set; }
		public int VariablesReference { get; set; }
	}

	public sealed class DapExceptionInfo
	{
		public string ExceptionId { get; set; }
		public string Description { get; set; }
		public string StackTrace { get; set; }
		public bool IsUnhandled { get; set; }
	}

	public sealed class DapBreakpointVerification
	{
		public int Line { get; set; }
		public bool Verified { get; set; }
		public string Message { get; set; }
	}

	public sealed class DapStoppedEventArgs
	{
		public int ThreadId { get; set; }
		public string Reason { get; set; }
		public IReadOnlyList<int> HitBreakpointIds { get; set; }
	}
}
