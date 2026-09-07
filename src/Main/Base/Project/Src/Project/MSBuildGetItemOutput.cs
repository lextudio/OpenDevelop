// Copyright (c) 2026 LeXtudio Inc.
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
using System.Text.Json;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Reads the JSON that `dotnet msbuild -getItem:&lt;name&gt;` prints on stdout.
	///
	/// `-getItem` runs the requested targets and then emits the named item list instead of normal
	/// build output, so a caller gets structured results without parsing a build log. The shape is:
	///
	/// <code>
	/// { "Items": { "ReferencePath": [ { "Identity": "/path/to.dll", "HintPath": "...", ... } ] } }
	/// </code>
	///
	/// Kept here, in Base and separate from any engine, for two reasons: it is a pure function over
	/// MSBuild's output format rather than anything specific to one caller, and the engine that uses
	/// it lives in the application assembly, which the unit-test project deliberately does not
	/// reference.
	/// </summary>
	public static class MSBuildGetItemOutput
	{
		/// <summary>
		/// The `Identity` of every entry in <paramref name="itemName"/>, in the order MSBuild
		/// emitted them. Returns an empty list for empty input, for output that is not the expected
		/// shape, and for output that simply contains no such item - all three are ordinary
		/// outcomes (an unrestored project produces the last one) and none of them is an error the
		/// caller can act on differently.
		/// </summary>
		/// <exception cref="JsonException">
		/// The text is not JSON at all. That means MSBuild printed something unexpected - a crash,
		/// or a diagnostic ahead of the payload - and is worth surfacing rather than swallowing.
		/// </exception>
		public static IReadOnlyList<string> ParseItemIdentities(string msbuildJson, string itemName)
		{
			if (itemName == null)
				throw new ArgumentNullException(nameof(itemName));

			var identities = new List<string>();
			if (string.IsNullOrWhiteSpace(msbuildJson))
				return identities;

			using var document = JsonDocument.Parse(msbuildJson);
			if (document.RootElement.ValueKind != JsonValueKind.Object
				|| !document.RootElement.TryGetProperty("Items", out var items)
				|| items.ValueKind != JsonValueKind.Object
				|| !items.TryGetProperty(itemName, out var entries)
				|| entries.ValueKind != JsonValueKind.Array)
				return identities;

			foreach (var entry in entries.EnumerateArray()) {
				if (entry.ValueKind == JsonValueKind.Object
					&& entry.TryGetProperty("Identity", out var identity)
					&& identity.ValueKind == JsonValueKind.String
					&& identity.GetString() is { Length: > 0 } value)
					identities.Add(value);
			}
			return identities;
		}
	}
}
