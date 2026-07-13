using System;
using ICSharpCode.Core;

namespace Hornung.ResourceToolkit.Conditions
{
    public class SolutionContainsProjectOrReferenceConditionEvaluator : IConditionEvaluator
    {
        public bool IsValid(object parameter, Condition condition)
        {
            return true;
        }
    }
}
