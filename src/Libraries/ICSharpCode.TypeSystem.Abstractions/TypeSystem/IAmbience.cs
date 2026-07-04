// Copyright (c) 2010-2013 AlphaSierraPapa for the SharpDevelop Team
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

namespace ICSharpCode.TypeSystem
{
    [Flags]
    public enum ConversionFlags
    {
        None = 0,
        ShowParameterList = 1,
        ShowParameterNames = 2,
        ShowAccessibility = 4,
        ShowDefinitionKeyword = 8,
        ShowDeclaringType = 0x10,
        ShowModifiers = 0x20,
        ShowReturnType = 0x40,
        UseFullyQualifiedTypeNames = 0x80,
        ShowTypeParameterList = 0x100,
        ShowBody = 0x200,
        UseFullyQualifiedEntityNames = 0x400,

        StandardConversionFlags = ShowParameterNames |
            ShowAccessibility |
            ShowParameterList |
            ShowReturnType |
            ShowModifiers |
            ShowTypeParameterList |
            ShowDefinitionKeyword |
            ShowBody,

        All = 0x7ff,
    }

    public interface IAmbience
    {
        ConversionFlags ConversionFlags { get; set; }

        [Obsolete("Use ConvertSymbol() instead")]
        string ConvertEntity(IEntity entity);

        string ConvertSymbol(ISymbol symbol);
        string ConvertType(IType type);

        [Obsolete("Use ConvertSymbol() instead")]
        string ConvertVariable(IVariable variable);

        string ConvertConstantValue(object constantValue);
        string WrapComment(string comment);
    }
}
