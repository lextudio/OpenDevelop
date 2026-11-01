using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.Translations
{
	/// <summary>What a designer or language addin contributes to the translation workflow: for the files it owns
	/// (GtkBuilder <c>.ui</c>, WinForms <c>.resx</c>, ...), the strings that need translating. The Translations
	/// addin owns everything else - the catalogue, the merge, the commands - so no designer carries a copy.</summary>
	public interface ITranslatableStringSource
	{
		/// <summary>A stable id for the source, used to register it once.</summary>
		string Name { get; }
		/// <summary>Whether this source extracts from <paramref name="filePath"/>.</summary>
		bool Handles(string filePath);
		/// <summary>The translatable strings of <paramref name="text"/>. <paramref name="referencePath"/> is the
		/// project-relative path written into each entry's <c>file:line</c> reference.</summary>
		IReadOnlyList<PoEntry> Extract(string text, string referencePath);
	}

	/// <summary>The sources addins have registered; an addin registers its own at startup (an autostart command).</summary>
	public static class TranslatableStringSources
	{
		static readonly object Gate = new object();
		static readonly List<ITranslatableStringSource> Registered = new List<ITranslatableStringSource>();

		/// <summary>Adds <paramref name="source"/> unless one with the same name is already registered.</summary>
		public static void Register(ITranslatableStringSource source)
		{
			lock (Gate) {
				if (!Registered.Any(s => s.Name == source.Name)) Registered.Add(source);
			}
		}

		public static IReadOnlyList<ITranslatableStringSource> All
		{
			get { lock (Gate) return Registered.ToArray(); }
		}
	}
}
