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

namespace ICSharpCode.Core
{
	public interface IOwnerState {
		System.Enum InternalState {
			get;
		}
	}
	
	/// <summary>
	/// Condition evaluator that compares the state of the parameter with a specified value.
	/// The parameter has to implement <see cref="IOwnerState"/>.
	/// </summary>
	public class OwnerStateConditionEvaluator : IConditionEvaluator
	{
		public bool IsValid(object parameter, Condition condition)
		{
			if (parameter is IOwnerState) {
				System.Enum state = ((IOwnerState)parameter).InternalState;
				string requestedState = condition.Properties["ownerstate"];
				object parsedState;
				if (state == null || string.IsNullOrWhiteSpace(requestedState)
				    || !Enum.TryParse(state.GetType(), requestedState, false, out parsedState)) {
					// AddIn conditions are configuration, not user input. A stale condition must simply
					// exclude/disable its codon; throwing here happens while a context menu is being
					// constructed and would otherwise terminate the entire IDE UI process.
					LoggingService.WarnFormatted(
						"Ownerstate condition '{0}' is not valid for enum {1}.",
						requestedState, state?.GetType().FullName ?? "<null>");
					return false;
				}

				ulong stateValue = Convert.ToUInt64(state);
				ulong conditionValue = Convert.ToUInt64(parsedState);
				return (stateValue & conditionValue) != 0;
			}
			return false;
		}
	}
}
