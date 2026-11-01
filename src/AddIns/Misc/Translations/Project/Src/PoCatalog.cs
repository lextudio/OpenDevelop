using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ICSharpCode.Translations
{
	/// <summary>One entry of a gettext catalogue (<c>.pot</c> template or <c>.po</c> translation). Framework-neutral
	/// so any designer that extracts translatable text can share the file format and the merge rules.</summary>
	public sealed class PoEntry
	{
		/// <summary><c>msgctxt</c>, or null.</summary>
		public string? Context { get; set; }
		public string MsgId { get; set; } = "";
		/// <summary><c>msgid_plural</c>, or null for a singular entry.</summary>
		public string? MsgIdPlural { get; set; }
		/// <summary><c>msgstr</c> of a singular entry.</summary>
		public string MsgStr { get; set; } = "";
		/// <summary><c>msgstr[n]</c> of a plural entry, in index order.</summary>
		public List<string> PluralStrings { get; } = new List<string>();
		/// <summary>Translator comments (<c>#</c> lines), written by people and kept by a merge.</summary>
		public List<string> TranslatorComments { get; } = new List<string>();
		/// <summary>Extracted comments (<c>#.</c> lines), from the source.</summary>
		public List<string> ExtractedComments { get; } = new List<string>();
		/// <summary>Source references (<c>#:</c>), <c>file:line</c>.</summary>
		public List<string> References { get; } = new List<string>();
		/// <summary>Flags (<c>#,</c>), e.g. <c>fuzzy</c>.</summary>
		public List<string> Flags { get; } = new List<string>();
		/// <summary>A translation whose source string is gone (<c>#~</c>); kept in case it comes back.</summary>
		public bool Obsolete { get; set; }

		/// <summary>Where the entry was read from, 1-based: its first line (comments included), and the lines of its
		/// <c>#,</c> flags, <c>msgid</c> and <c>msgstr</c>/<c>msgstr[0]</c>; 0 when it has none or was not parsed from text.
		/// The language service reports its problems against these.</summary>
		public int Line { get; set; }
		public int FlagsLine { get; set; }
		public int MsgIdLine { get; set; }
		public int MsgStrLine { get; set; }

		public bool IsTranslated => MsgIdPlural == null ? MsgStr.Length > 0 : PluralStrings.Any(s => s.Length > 0);
		public bool IsFuzzy => Flags.Contains("fuzzy");
		public string Key => (Context ?? "\u0001") + "\u0004" + MsgId;
	}

	/// <summary>A gettext catalogue: the header (<c>msgid ""</c>) and its entries. <see cref="Parse"/> and
	/// <see cref="Write"/> round-trip what the designers use; <see cref="Merge"/> is the <c>msgmerge</c> step
	/// that carries translations over to a regenerated template.</summary>
	public sealed class PoCatalog
	{
		/// <summary>The header entry (<c>msgid ""</c>), whose <c>msgstr</c> holds the <c>Key: value</c> lines.</summary>
		public PoEntry Header { get; set; } = new PoEntry { MsgStr = DefaultHeader("") };
		public List<PoEntry> Entries { get; } = new List<PoEntry>();

		public static string DefaultHeader(string project)
			=> (project.Length > 0 ? "Project-Id-Version: " + project + "\n" : "")
				+ "MIME-Version: 1.0\nContent-Type: text/plain; charset=UTF-8\nContent-Transfer-Encoding: 8bit\n";

		public static PoCatalog Parse(string text)
		{
			var catalog = new PoCatalog();
			PoEntry? entry = null;
			var section = "";
			var seenHeader = false;
			var pluralIndex = 0;
			void Flush()
			{
				if (entry == null) return;
				if (entry.MsgId.Length == 0 && entry.Context == null && !entry.Obsolete && !seenHeader) { catalog.Header = entry; seenHeader = true; }
				else catalog.Entries.Add(entry);
				entry = null; section = "";
			}
			var lineNumber = 0;
			foreach (var raw in text.Replace("\r", "").Split('\n')) {
				lineNumber++;
				var line = raw.Trim();
				if (line.Length == 0) { Flush(); continue; }
				var obsolete = false;
				if (line.StartsWith("#~", StringComparison.Ordinal)) { obsolete = true; line = line.Substring(2).TrimStart(); }
				if (line.StartsWith("#", StringComparison.Ordinal)) {
					// A comment after a msgstr belongs to the next entry.
					if (entry != null && section is "msgstr" or "plural") Flush();
					entry ??= new PoEntry { Obsolete = obsolete, Line = lineNumber };
					if (line.StartsWith("#.", StringComparison.Ordinal)) entry.ExtractedComments.Add(line.Substring(2).Trim());
					else if (line.StartsWith("#:", StringComparison.Ordinal)) entry.References.AddRange(line.Substring(2).Split(' ', StringSplitOptions.RemoveEmptyEntries));
					else if (line.StartsWith("#,", StringComparison.Ordinal) && SetFlagsLine(entry, lineNumber)) entry.Flags.AddRange(line.Substring(2).Split(',').Select(f => f.Trim()).Where(f => f.Length > 0));
					else if (!line.StartsWith("#|", StringComparison.Ordinal)) entry.TranslatorComments.Add(line.Substring(1).TrimStart());
					continue;
				}
				if (line.StartsWith("\"", StringComparison.Ordinal)) {
					if (entry == null) continue;
					Append(entry, section, pluralIndex, Unquote(line));
					continue;
				}
				var space = line.IndexOf(' ');
				var keyword = space < 0 ? line : line.Substring(0, space);
				var value = space < 0 ? "" : Unquote(line.Substring(space + 1).Trim());
				if (keyword == "msgctxt" || (keyword == "msgid" && entry != null && section is "msgstr" or "plural")) {
					if (section is "msgstr" or "plural") Flush();
				}
				entry ??= new PoEntry { Obsolete = obsolete, Line = lineNumber };
				if (obsolete) entry.Obsolete = true;
				switch (keyword) {
					case "msgctxt": entry.Context = value; section = "msgctxt"; break;
					case "msgid": entry.MsgId = value; entry.MsgIdLine = lineNumber; section = "msgid"; break;
					case "msgid_plural": entry.MsgIdPlural = value; section = "msgid_plural"; break;
					case "msgstr": entry.MsgStr = value; entry.MsgStrLine = lineNumber; section = "msgstr"; break;
					default:
						if (keyword.StartsWith("msgstr[", StringComparison.Ordinal) && int.TryParse(keyword.Substring(7).TrimEnd(']'), out var index)) {
							pluralIndex = index;
							if (entry.MsgStrLine == 0) entry.MsgStrLine = lineNumber;
							while (entry.PluralStrings.Count <= index) entry.PluralStrings.Add("");
							entry.PluralStrings[index] = value; section = "plural";
						}
						break;
				}
			}
			Flush();
			return catalog;
		}

		static bool SetFlagsLine(PoEntry entry, int lineNumber) { if (entry.FlagsLine == 0) entry.FlagsLine = lineNumber; return true; }

		static void Append(PoEntry entry, string section, int pluralIndex, string value)
		{
			switch (section) {
				case "msgctxt": entry.Context += value; break;
				case "msgid": entry.MsgId += value; break;
				case "msgid_plural": entry.MsgIdPlural += value; break;
				case "msgstr": entry.MsgStr += value; break;
				case "plural": entry.PluralStrings[pluralIndex] += value; break;
			}
		}

		static string Unquote(string quoted)
		{
			if (quoted.Length < 2 || quoted[0] != '"' || quoted[quoted.Length - 1] != '"') return quoted;
			var sb = new StringBuilder();
			for (var i = 1; i < quoted.Length - 1; i++) {
				var c = quoted[i];
				if (c != '\\' || i + 1 >= quoted.Length - 1) { sb.Append(c); continue; }
				switch (quoted[++i]) {
					case 'n': sb.Append('\n'); break;
					case 't': sb.Append('\t'); break;
					case 'r': sb.Append('\r'); break;
					case '"': sb.Append('"'); break;
					case '\\': sb.Append('\\'); break;
					default: sb.Append('\\').Append(quoted[i]); break;
				}
			}
			return sb.ToString();
		}

		static string Quote(string value)
		{
			var sb = new StringBuilder("\"");
			foreach (var c in value) {
				switch (c) {
					case '\n': sb.Append("\\n"); break;
					case '\t': sb.Append("\\t"); break;
					case '\r': sb.Append("\\r"); break;
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					default: sb.Append(c); break;
				}
			}
			return sb.Append('"').ToString();
		}

		/// <summary>gettext's layout: a string with line breaks is written as an empty first line followed by one line each.</summary>
		static void WriteString(StringBuilder sb, string prefix, string keyword, string value)
		{
			if (value.IndexOf('\n') < 0 || value.IndexOf('\n') == value.Length - 1 && value.Length == 1) {
				sb.Append(prefix).Append(keyword).Append(' ').Append(Quote(value)).Append('\n');
				return;
			}
			sb.Append(prefix).Append(keyword).Append(" \"\"\n");
			var start = 0;
			while (start < value.Length) {
				var end = value.IndexOf('\n', start);
				end = end < 0 ? value.Length : end + 1;
				sb.Append(prefix).Append(Quote(value.Substring(start, end - start))).Append('\n');
				start = end;
			}
		}

		public string Write()
		{
			var sb = new StringBuilder();
			WriteEntry(sb, Header, true);
			foreach (var entry in Entries.Where(e => !e.Obsolete)) { sb.Append('\n'); WriteEntry(sb, entry, false); }
			foreach (var entry in Entries.Where(e => e.Obsolete)) { sb.Append('\n'); WriteEntry(sb, entry, false); }
			return sb.ToString();
		}

		static void WriteEntry(StringBuilder sb, PoEntry entry, bool header)
		{
			var prefix = entry.Obsolete ? "#~ " : "";
			if (!entry.Obsolete) {
				foreach (var comment in entry.TranslatorComments) sb.Append(comment.Length == 0 ? "#" : "# " + comment).Append('\n');
				foreach (var comment in entry.ExtractedComments) sb.Append("#. ").Append(comment).Append('\n');
				if (entry.References.Count > 0) sb.Append("#: ").Append(string.Join(" ", entry.References)).Append('\n');
				if (entry.Flags.Count > 0) sb.Append("#, ").Append(string.Join(", ", entry.Flags)).Append('\n');
			}
			if (entry.Context != null) WriteString(sb, prefix, "msgctxt", entry.Context);
			WriteString(sb, prefix, "msgid", header ? "" : entry.MsgId);
			if (entry.MsgIdPlural != null) {
				WriteString(sb, prefix, "msgid_plural", entry.MsgIdPlural);
				for (var i = 0; i < Math.Max(entry.PluralStrings.Count, 1); i++)
					WriteString(sb, prefix, "msgstr[" + i + "]", i < entry.PluralStrings.Count ? entry.PluralStrings[i] : "");
			} else WriteString(sb, prefix, "msgstr", entry.MsgStr);
		}

		/// <summary>The <c>msgmerge</c> step: a new catalogue with <paramref name="template"/>'s entries, comments and
		/// references, carrying over each translation (and its fuzzy flag and translator comments) from
		/// <paramref name="existing"/> by context and msgid. A translation whose string left the template becomes
		/// obsolete rather than being thrown away, and an obsolete one whose string came back is revived. The
		/// existing header is kept.</summary>
		public static PoCatalog Merge(PoCatalog template, PoCatalog? existing)
		{
			var result = new PoCatalog { Header = existing?.Header ?? template.Header };
			var old = new Dictionary<string, PoEntry>(StringComparer.Ordinal);
			if (existing != null) foreach (var entry in existing.Entries) old[entry.Key] = entry;
			var used = new HashSet<string>(StringComparer.Ordinal);
			foreach (var entry in template.Entries.Where(e => !e.Obsolete)) {
				var merged = new PoEntry { Context = entry.Context, MsgId = entry.MsgId, MsgIdPlural = entry.MsgIdPlural };
				merged.ExtractedComments.AddRange(entry.ExtractedComments);
				merged.References.AddRange(entry.References);
				if (old.TryGetValue(entry.Key, out var previous) && previous.MsgIdPlural == entry.MsgIdPlural) {
					used.Add(entry.Key);
					merged.MsgStr = previous.MsgStr;
					merged.PluralStrings.AddRange(previous.PluralStrings);
					merged.TranslatorComments.AddRange(previous.TranslatorComments);
					merged.Flags.AddRange(previous.Flags);
				} else merged.Flags.AddRange(entry.Flags);
				result.Entries.Add(merged);
			}
			if (existing != null)
				foreach (var entry in existing.Entries.Where(e => !used.Contains(e.Key))) {
					if (!entry.IsTranslated) continue;
					if (!entry.Obsolete) {
						var kept = new PoEntry { Context = entry.Context, MsgId = entry.MsgId, MsgIdPlural = entry.MsgIdPlural, MsgStr = entry.MsgStr, Obsolete = true };
						kept.PluralStrings.AddRange(entry.PluralStrings);
						result.Entries.Add(kept);
					} else result.Entries.Add(entry);
				}
			return result;
		}
	}
}
