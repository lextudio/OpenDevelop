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
using System.Linq;

using Debugger.AddIn.Service.Dap;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.SharpDevelop.Services;

namespace Debugger.AddIn.TreeModel
{
	/// <summary>
	/// Tree node which represents a DAP variable (local/parameter/field/watch expression).
	/// Unlike the old ICorDebug-backed version, there is no live reflection over a client-side
	/// type system: values, types and the "has children" flag all come pre-formatted from the
	/// adapter (SharpDbg), and children are lazily fetched on demand via the DAP "variables"
	/// request using <c>variablesReference</c>. This means several old features are gone:
	/// live-writable values, DebuggerDisplayAttribute formatting, custom IEnumerable/IList
	/// flattening, hex/decimal integer display options - the adapter decides how a value prints.
	/// </summary>
	public class ValueNode : TreeNode
	{
		int variablesReference;
		string evaluateExpression;

		public string FullText {
			get { return this.Value; }
		}

		public string ErrorMessage { get; private set; }

		/// <summary>Wraps an already-fetched DAP variable (used for locals/parameters/children).</summary>
		public ValueNode(IImage image, DapVariableInfo variable)
			: base(image, variable.Name, variable.Value ?? string.Empty, variable.Type ?? string.Empty, null)
		{
			this.variablesReference = variable.VariablesReference;
			this.evaluateExpression = !string.IsNullOrEmpty(variable.EvaluateName) ? variable.EvaluateName : variable.Name;
			this.ContextMenuAddInTreeEntry = "/AddIns/Debugger/Tooltips/ContextMenu/ValueNode";
			if (variablesReference > 0) {
				this.GetChildren = LoadChildren;
			}
			UpdateVisualizerCommands();
		}

		/// <summary>Evaluates an arbitrary expression against the current stack frame (Watch, tooltips).</summary>
		public ValueNode(IImage image, string name, string expression)
			: base(image, name, string.Empty, string.Empty, null)
		{
			this.evaluateExpression = expression;
			Refresh();
		}

		public void Refresh()
		{
			if (string.IsNullOrWhiteSpace(evaluateExpression)) {
				this.Value = string.Empty;
				this.Type = string.Empty;
				this.GetChildren = null;
				return;
			}

			try {
				var result = WindowsDebugger.EvaluateAsync(evaluateExpression, "watch").GetAwaiter().GetResult();
				this.Value = result.Value ?? string.Empty;
				this.Type = result.Type ?? string.Empty;
				this.variablesReference = result.VariablesReference;
				this.GetChildren = variablesReference > 0 ? (System.Func<IEnumerable<TreeNode>>)LoadChildren : null;
				this.ErrorMessage = null;
				this.ContextMenuAddInTreeEntry = "/AddIns/Debugger/Tooltips/ContextMenu/ValueNode";
				UpdateVisualizerCommands();
			} catch (System.Exception ex) {
				this.ErrorMessage = ex.Message;
				this.Value = ex.Message;
				this.Type = string.Empty;
				this.GetChildren = null;
				this.ContextMenuAddInTreeEntry = "/AddIns/Debugger/Tooltips/ContextMenu/ErrorNode";
			}
		}

		IEnumerable<TreeNode> LoadChildren()
		{
			return GetChildVariables().Select(v => (TreeNode)new ValueNode(SD.ResourceService.GetImage("Icons.16x16.Field"), v));
		}

		IEnumerable<DapVariableInfo> GetChildVariables()
		{
			var session = WindowsDebugger.CurrentSession;
			if (session == null || variablesReference == 0) {
				return Enumerable.Empty<DapVariableInfo>();
			}
			return session.GetVariablesAsync(variablesReference).GetAwaiter().GetResult();
		}

		void UpdateVisualizerCommands()
		{
			bool hasChildren = variablesReference > 0;
			this.VisualizerCommands = Debugger.AddIn.Visualizers.VisualizerDescriptors.GetAllDescriptors()
				.Where(d => d.IsVisualizerAvailable(this.Type, this.Value, hasChildren))
				.Select(d => d.CreateVisualizerCommand(this.Name, this.Type, this.Value, GetChildVariables))
				.ToList();
		}

		/// <summary>
		/// The root of any node evaluation is a valid, paused stack frame.
		/// </summary>
		static void CheckCanEvaluate()
		{
			if (WindowsDebugger.CurrentSession == null)
				throw new GetValueException("Debugger is not running");
			if (!WindowsDebugger.CurrentSession.IsPaused)
				throw new GetValueException("Process is not paused");
			if (WindowsDebugger.CurrentStackFrame == null)
				throw new GetValueException("No stack frame selected");
		}

		public static IEnumerable<TreeNode> GetLocalVariables()
		{
			CheckCanEvaluate();

			var session = WindowsDebugger.CurrentSession;
			var frame = WindowsDebugger.CurrentStackFrame;
			var scopes = session.GetScopesAsync(frame.Id).GetAwaiter().GetResult();
			foreach (var scope in scopes) {
				if (scope.Expensive) {
					// e.g. "Globals" in some adapters - skip by default, matching the old
					// "external methods" style behavior of hiding rarely-useful noise.
					continue;
				}
				var variables = session.GetVariablesAsync(scope.VariablesReference).GetAwaiter().GetResult();
				foreach (var variable in variables) {
					yield return new ValueNode(SD.ResourceService.GetImage("Icons.16x16.Local"), variable);
				}
			}
		}
	}
}
