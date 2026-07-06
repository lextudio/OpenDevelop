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
using System.Collections.Generic;
using Debugger.AddIn.Service.Dap;
using Debugger.AddIn.Visualizers.GridVisualizer;
using ICSharpCode.SharpDevelop.Debugging;

namespace Debugger.AddIn.Visualizers
{
	public class GridVisualizerDescriptor : IVisualizerDescriptor
	{
		public bool IsVisualizerAvailable(string typeName, string value, bool hasChildren)
		{
			// Under the old ICorDebug engine this checked for IEnumerable<T>/IList<T> specifically.
			// DAP only tells us "this variable has children" (variablesReference > 0) - not whether
			// they represent a collection or an arbitrary object's fields - so the grid visualizer
			// is offered for any expandable value; rows will just be its DAP children either way.
			return hasChildren;
		}

		public IVisualizerCommand CreateVisualizerCommand(string valueName, string typeName, string value, Func<IEnumerable<DapVariableInfo>> getChildren)
		{
			return new GridVisualizerCommand(valueName, typeName, value, getChildren);
		}
	}

	/// <summary>
	/// Shows a flat Name/Value/Type grid of a value's DAP children. Unlike the old ICorDebug-backed
	/// visualizer, this does not pivot object fields into columns per member - DAP does not expose
	/// enough type information client-side to do that generically. Known simplification.
	/// </summary>
	public class GridVisualizerCommand : ExpressionVisualizerCommand
	{
		public GridVisualizerCommand(string valueName, string typeName, string value, Func<IEnumerable<DapVariableInfo>> getChildren)
			: base(valueName, typeName, value, getChildren)
		{
		}

		public override string ToString()
		{
			return "Collection visualizer";
		}

		public override void Execute()
		{
			GridVisualizerWindow window = new GridVisualizerWindow(this.ValueName, this.GetChildren);
			window.ShowDialog();
		}
	}
}
