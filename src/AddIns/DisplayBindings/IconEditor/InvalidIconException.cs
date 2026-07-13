using System;

namespace ICSharpCode.IconEditor
{
	public class InvalidIconException : Exception
	{
		public InvalidIconException() : base()
		{
		}

		public InvalidIconException(string message) : base(message)
		{
		}

		public InvalidIconException(string message, Exception innerException) : base(message, innerException)
		{
		}
	}
}
