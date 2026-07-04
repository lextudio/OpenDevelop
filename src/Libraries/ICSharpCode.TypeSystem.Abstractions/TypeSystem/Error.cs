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
	/// <summary>
	/// Enum that describes the type of an error.
	/// </summary>
	public enum ErrorType
	{
		Unknown,
		Error,
		Warning
	}

	/// <summary>
	/// Describes an error during parsing.
	/// </summary>
	[Serializable]
	public class Error
	{
		readonly ErrorType errorType;
		readonly string message;
		readonly DomRegion region;

		/// <summary>
		/// The type of the error.
		/// </summary>
		public ErrorType ErrorType { get { return errorType; } }

		/// <summary>
		/// The error description.
		/// </summary>
		public string Message { get { return message; } }

		/// <summary>
		/// The region of the error.
		/// </summary>
		public DomRegion Region { get { return region; } }

		public Error(ErrorType errorType, string message, DomRegion region)
		{
			this.errorType = errorType;
			this.message = message;
			this.region = region;
		}

		public Error(ErrorType errorType, string message, TextLocation location)
		{
			this.errorType = errorType;
			this.message = message;
			this.region = new DomRegion(location, location);
		}

		public Error(ErrorType errorType, string message, int line, int col) : this(errorType, message, new TextLocation(line, col))
		{
		}

		public Error(ErrorType errorType, string message)
		{
			this.errorType = errorType;
			this.message = message;
			this.region = DomRegion.Empty;
		}
	}
}
