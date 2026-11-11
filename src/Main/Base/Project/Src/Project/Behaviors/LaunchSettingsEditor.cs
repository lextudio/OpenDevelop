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
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>A launchSettings.json that cannot be edited safely; the file is left untouched.</summary>
	public sealed class LaunchSettingsEditException : Exception
	{
		public LaunchSettingsEditException(string message) : base(message) { }
	}

	/// <summary>
	/// Adds a profile to <c>Properties/launchSettings.json</c> without rewriting the file: the new
	/// member is inserted into the original text at the end of <c>profiles</c>, so comments,
	/// indentation, line endings, trailing commas, the BOM and every other profile (including the
	/// ones and fields this IDE does not understand) stay byte for byte as they were.
	/// System.Text.Json cannot write a document back with its comments, and Newtonsoft.Json would
	/// re-indent the whole file; positions from <see cref="Utf8JsonReader"/> make an insertion exact.
	/// </summary>
	public static class LaunchSettingsEditor
	{
		const string SchemaUrl = "http://json.schemastore.org/launchsettings.json";

		static readonly JsonReaderOptions ReaderOptions = new JsonReaderOptions {
			CommentHandling = JsonCommentHandling.Allow,
			AllowTrailingCommas = true
		};

		/// <summary>
		/// Returns <paramref name="existingText"/> with a <c>Project</c> profile added (or a new file's
		/// text when there is none). <paramref name="compatibleTargetFramework"/> is Uno's platform
		/// name for the profile (<c>desktop</c>, <c>ios</c>, ...); null or empty omits it.
		/// </summary>
		/// <exception cref="LaunchSettingsEditException">The text is not valid JSON, has no
		/// <c>profiles</c> object, or already has a profile of that name.</exception>
		public static string AddProfile(string existingText, string name, string compatibleTargetFramework)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ArgumentException("A profile needs a name.", nameof(name));
			if (string.IsNullOrWhiteSpace(existingText))
				return NewFile(name, compatibleTargetFramework);

			// A BOM that reached us as a character is kept in front of the edited text.
			if (existingText[0] == '\uFEFF')
				return "\uFEFF" + AddProfile(existingText.Substring(1), name, compatibleTargetFramework);
			string text = existingText;
			string newline = text.Contains("\r\n") ? "\r\n" : "\n";
			var bytes = Encoding.UTF8.GetBytes(text);
			var layout = Scan(bytes, name);

			string unit = layout.ProfilesIndent.Length > 0 ? layout.ProfilesIndent : "  ";
			string memberIndent = layout.FirstMemberIndent ?? layout.ProfilesIndent + unit;
			string member = FormatMember(name, compatibleTargetFramework, memberIndent, unit, newline);

			string insertion;
			int position;
			int removeLength = 0;
			if (layout.LastMemberEnd >= 0) {
				// After the last member; its own trailing comma, if any, stays valid after the new one.
				position = layout.LastMemberEnd;
				insertion = "," + newline + memberIndent + member;
			} else {
				position = layout.OpenBraceEnd;
				string between = Encoding.UTF8.GetString(bytes, layout.OpenBraceEnd, layout.CloseBraceStart - layout.OpenBraceEnd);
				if (string.IsNullOrWhiteSpace(between)) {
					// An empty "profiles": {} (any whitespace inside): replace the whitespace.
					removeLength = layout.CloseBraceStart - layout.OpenBraceEnd;
					insertion = newline + memberIndent + member + newline + layout.ProfilesIndent;
				} else {
					// Only comments inside: keep them after the new member.
					insertion = newline + memberIndent + member;
				}
			}

			var before = Encoding.UTF8.GetString(bytes, 0, position);
			var after = Encoding.UTF8.GetString(bytes, position + removeLength, bytes.Length - position - removeLength);
			return before + insertion + after;
		}

		/// <summary>
		/// <see cref="AddProfile"/> on a file: creates it (and its directory) when it does not exist,
		/// keeps a UTF-8 byte order mark, and writes nothing if the edit is refused or the file cannot be used.
		/// </summary>
		/// <exception cref="LaunchSettingsEditException">The edit is refused, or the file cannot be read or
		/// written (read-only, locked by another editor, no space): the original file is left as it was.</exception>
		public static void AddProfileToFile(string path, string name, string compatibleTargetFramework)
		{
			try {
				bool exists = File.Exists(path);
				byte[] raw = exists ? File.ReadAllBytes(path) : Array.Empty<byte>();
				bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
				string existing = Encoding.UTF8.GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
				string updated = AddProfile(existing, name, compatibleTargetFramework);
				string directory = Path.GetDirectoryName(Path.GetFullPath(path));
				Directory.CreateDirectory(directory);
				if (exists && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
					throw new IOException("the file is read-only");
				// Write beside it and replace: File.WriteAllText truncates first, so a failure halfway (disk full,
				// media gone) would leave the user's launchSettings.json empty.
				string temp = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
				try {
					File.WriteAllText(temp, updated, new UTF8Encoding(bom));
					File.Move(temp, path, overwrite: true);
				} finally {
					try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
				}
			} catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				throw new LaunchSettingsEditException("Could not update " + path + ": " + ex.Message);
			}
		}

		static string NewFile(string name, string compatibleTargetFramework)
		{
			const string nl = "\n";
			return "{" + nl
				+ "  \"$schema\": \"" + SchemaUrl + "\"," + nl
				+ "  \"profiles\": {" + nl
				+ "    " + FormatMember(name, compatibleTargetFramework, "    ", "  ", nl) + nl
				+ "  }" + nl
				+ "}" + nl;
		}

		static string FormatMember(string name, string compatibleTargetFramework, string indent, string unit, string newline)
		{
			var builder = new StringBuilder();
			builder.Append(Quote(name)).Append(": {").Append(newline);
			builder.Append(indent).Append(unit).Append("\"commandName\": \"Project\"");
			if (!string.IsNullOrEmpty(compatibleTargetFramework)) {
				builder.Append(',').Append(newline);
				builder.Append(indent).Append(unit).Append("\"compatibleTargetFramework\": ").Append(Quote(compatibleTargetFramework));
			}
			builder.Append(newline).Append(indent).Append('}');
			return builder.ToString();
		}

		static string Quote(string value)
		{
			return JsonSerializer.Serialize(value, QuoteOptions);
		}

		static readonly JsonSerializerOptions QuoteOptions = new JsonSerializerOptions {
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};

		sealed class Layout
		{
			public int OpenBraceEnd;
			public int CloseBraceStart;
			public int LastMemberEnd = -1;
			public string ProfilesIndent = string.Empty;
			public string FirstMemberIndent;
		}

		static Layout Scan(byte[] bytes, string newName)
		{
			var layout = new Layout();
			bool foundProfiles = false;
			bool inProfiles = false;
			bool closed = false;
			try {
				var reader = new Utf8JsonReader(bytes, ReaderOptions);
				if (!ReadSkippingComments(ref reader) || reader.TokenType != JsonTokenType.StartObject)
					throw new LaunchSettingsEditException("launchSettings.json is not a JSON object; it was not changed.");
				// Keep reading after "profiles" closes: the reader throws on a missing root '}' or on
				// anything after it, so a file LaunchSettingsProfile.Load would reject is refused here too.
				while (reader.Read()) {
					if (closed)
						continue;
					if (!inProfiles) {
						if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1
						    && !foundProfiles && reader.ValueTextEquals("profiles")) {
							layout.ProfilesIndent = LineIndent(bytes, (int)reader.TokenStartIndex) ?? string.Empty;
							if (!ReadSkippingComments(ref reader) || reader.TokenType != JsonTokenType.StartObject)
								throw new LaunchSettingsEditException("\"profiles\" in launchSettings.json is not an object; it was not changed.");
							layout.OpenBraceEnd = (int)reader.BytesConsumed;
							foundProfiles = true;
							inProfiles = true;
						} else if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray) {
							reader.Skip();
						}
						continue;
					}
					switch (reader.TokenType) {
						case JsonTokenType.PropertyName when reader.CurrentDepth == 2:
							if (reader.GetString() == newName)
								throw new LaunchSettingsEditException("launchSettings.json already has a profile named '" + newName + "'.");
							layout.FirstMemberIndent ??= LineIndent(bytes, (int)reader.TokenStartIndex);
							break;
						case JsonTokenType.EndObject when reader.CurrentDepth == 1:
							layout.CloseBraceStart = (int)reader.TokenStartIndex;
							inProfiles = false;
							closed = true;
							break;
						case JsonTokenType.EndObject when reader.CurrentDepth == 2:
						case JsonTokenType.EndArray when reader.CurrentDepth == 2:
						case JsonTokenType.String when reader.CurrentDepth == 2:
						case JsonTokenType.Number when reader.CurrentDepth == 2:
						case JsonTokenType.True when reader.CurrentDepth == 2:
						case JsonTokenType.False when reader.CurrentDepth == 2:
						case JsonTokenType.Null when reader.CurrentDepth == 2:
							layout.LastMemberEnd = (int)reader.BytesConsumed;
							break;
					}
				}
			} catch (JsonException ex) {
				throw new LaunchSettingsEditException("launchSettings.json is not valid JSON (" + ex.Message + "); it was not changed.");
			}
			if (closed)
				return layout;
			throw new LaunchSettingsEditException(foundProfiles
				? "launchSettings.json ends inside \"profiles\"; it was not changed."
				: "launchSettings.json has no \"profiles\" object; it was not changed.");
		}

		/// <summary>Reads the next token that is not a comment; the reader keeps comments as tokens.</summary>
		static bool ReadSkippingComments(ref Utf8JsonReader reader)
		{
			while (reader.Read()) {
				if (reader.TokenType != JsonTokenType.Comment)
					return true;
			}
			return false;
		}

		/// <summary>The whitespace that starts the line holding the token at <paramref name="tokenStart"/>,
		/// or null when something else precedes the token on that line.</summary>
		static string LineIndent(byte[] bytes, int tokenStart)
		{
			int lineStart = tokenStart;
			while (lineStart > 0 && bytes[lineStart - 1] != (byte)'\n')
				lineStart--;
			for (int i = lineStart; i < tokenStart; i++) {
				if (bytes[i] != (byte)' ' && bytes[i] != (byte)'\t')
					return null;
			}
			return Encoding.UTF8.GetString(bytes, lineStart, tokenStart - lineStart);
		}
	}
}
