// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

using System;
using System.Security.Cryptography;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>Validates the common authenticated DDP host handshake.</summary>
	public static class DesignerHostHandshakeValidator
	{
		public static void Validate(string expectedToken, string token, int protocolVersion)
		{
			if (!TokensMatch(expectedToken, token))
				throw new UnauthorizedAccessException("Invalid designer-host token.");
			if (protocolVersion != DesignerProtocol.Version)
				throw new NotSupportedException($"Protocol {protocolVersion} is not supported.");
		}

		static bool TokensMatch(string expectedToken, string token)
		{
			if (string.IsNullOrWhiteSpace(expectedToken) || string.IsNullOrWhiteSpace(token))
				return false;
			try {
				var expected = Convert.FromHexString(expectedToken);
				var actual = Convert.FromHexString(token);
				return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
			} catch (FormatException) {
				return false;
			}
		}
	}
}
