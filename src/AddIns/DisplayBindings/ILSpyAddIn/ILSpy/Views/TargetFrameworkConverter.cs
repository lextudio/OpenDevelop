// Copyright (c) 2026 AlphaSierraPapa for the SharpDevelop Team
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

using ICSharpCode.ILSpy.Properties;

using NuGet.Frameworks;

namespace ICSharpCode.ILSpy.Views
{
	internal static class TargetFrameworkConverter
	{
		static readonly HashSet<string> SupportedIdentifiers = new(StringComparer.OrdinalIgnoreCase) {
			FrameworkConstants.FrameworkIdentifiers.Net,
			FrameworkConstants.FrameworkIdentifiers.NetCoreApp,
			FrameworkConstants.FrameworkIdentifiers.NetStandard,
		};

		public static bool TryParseToFrameworkName(string input, out string frameworkName, out string error)
		{
			frameworkName = null;
			error = null;
			if (string.IsNullOrWhiteSpace(input))
			{
				error = Resources.InvalidTargetFramework;
				return false;
			}

			var framework = TryParse(input.Trim());
			if (framework == null || framework.IsUnsupported || framework.IsAgnostic || framework.IsAny
				|| !SupportedIdentifiers.Contains(framework.Framework))
			{
				error = Resources.InvalidTargetFramework;
				return false;
			}

			frameworkName = framework.DotNetFrameworkName;
			return true;
		}

		public static string ToShortFolderName(string frameworkName)
		{
			if (string.IsNullOrWhiteSpace(frameworkName))
				return null;
			try
			{
				var framework = NuGetFramework.ParseFrameworkName(frameworkName, DefaultFrameworkNameProvider.Instance);
				if (framework.IsUnsupported)
					return null;
				return framework.GetShortFolderName();
			}
			catch (ArgumentException)
			{
				return null;
			}
		}

		static NuGetFramework TryParse(string input)
		{
			try
			{
				var framework = NuGetFramework.Parse(input);
				if (!framework.IsUnsupported)
					return framework;
			}
			catch (ArgumentException)
			{
			}
			try
			{
				return NuGetFramework.ParseFrameworkName(input, DefaultFrameworkNameProvider.Instance);
			}
			catch (ArgumentException)
			{
				return null;
			}
		}
	}
}
