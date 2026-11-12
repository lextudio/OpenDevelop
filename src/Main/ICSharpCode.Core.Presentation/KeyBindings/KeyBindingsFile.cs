using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>
	/// The user's keyboard shortcut overrides, as stored in <c>KeyBindings.xml</c>: only the DIFFERENCES from the .addin manifests, one
	/// <c>&lt;Command key="..." gesture="..."/&gt;</c> per command (see <see cref="CommandKey"/>). An empty <c>gesture</c> is the explicit
	/// "no shortcut". doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	/// <remarks>
	/// The file is edited in place as an XML document, so whatever this version does not understand (elements, attributes, a newer
	/// <c>version</c>) is written back as it was. A file that cannot be read is treated as empty, and the unreadable original is kept
	/// (as <c>.corrupt</c>) before the first save overwrites it.
	/// </remarks>
	public sealed class KeyBindingsFile
	{
		public const int CurrentVersion = 1;
		public const string RootName = "KeyBindings";
		public const string CommandName = "Command";

		readonly XDocument document;
		string unreadableOriginalPath;

		public KeyBindingsFile()
		{
			document = new XDocument(new XElement(RootName, new XAttribute("version", CurrentVersion)));
		}

		KeyBindingsFile(XDocument document)
		{
			this.document = document;
		}

		/// <summary>The <c>version</c> of the file as read (<see cref="CurrentVersion"/> for a new one, 1 when the attribute is missing).</summary>
		public int Version {
			get {
				string text = (string)document.Root.Attribute("version");
				int version;
				return int.TryParse(text, out version) && version > 0 ? version : 1;
			}
		}

		/// <summary>True for a file written by a newer version: what is understood is used, the rest is kept untouched.</summary>
		public bool IsFromNewerVersion {
			get { return Version > CurrentVersion; }
		}

		/// <summary>The command key -> gesture text pairs, in file order. An empty gesture means "no shortcut".</summary>
		public IReadOnlyDictionary<string, string> Overrides {
			get {
				var result = new Dictionary<string, string>(StringComparer.Ordinal);
				foreach (XElement element in CommandElements()) {
					string key = (string)element.Attribute("key");
					string gesture = (string)element.Attribute("gesture");
					if (string.IsNullOrWhiteSpace(key) || gesture == null)
						continue;
					result[key.Trim()] = gesture.Trim();
				}
				return result;
			}
		}

		IEnumerable<XElement> CommandElements()
		{
			return document.Root.Elements(CommandName);
		}

		XElement Find(string key)
		{
			return CommandElements().LastOrDefault(e => string.Equals(((string)e.Attribute("key"))?.Trim(), key, StringComparison.Ordinal));
		}

		/// <summary>Sets the override of a command; <paramref name="gesture"/> empty means "no shortcut".</summary>
		public void Set(string key, string gesture)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentException("A command key is required.", "key");
			// Duplicates are folded into one entry, the last one being the one that counted
			XElement[] all = CommandElements().Where(e => string.Equals(((string)e.Attribute("key"))?.Trim(), key, StringComparison.Ordinal)).ToArray();
			XElement element = all.LastOrDefault();
			foreach (XElement duplicate in all.Where(e => e != element).ToArray())
				duplicate.Remove();
			if (element == null) {
				element = new XElement(CommandName, new XAttribute("key", key));
				document.Root.Add(element);
			}
			element.SetAttributeValue("gesture", gesture ?? "");
		}

		/// <summary>Removes the override of a command (its default applies again). Returns false when there was none.</summary>
		public bool Remove(string key)
		{
			XElement[] all = CommandElements().Where(e => string.Equals(((string)e.Attribute("key"))?.Trim(), key, StringComparison.Ordinal)).ToArray();
			foreach (XElement element in all)
				element.Remove();
			return all.Length > 0;
		}

		/// <summary>Removes every override (what this version does not understand stays).</summary>
		public void Clear()
		{
			foreach (XElement element in CommandElements().ToArray())
				element.Remove();
		}

		/// <summary>
		/// Reads a file. A missing file is an empty one. A file that is not valid XML is reported through <paramref name="report"/> and
		/// treated as empty; it is kept as <c>.corrupt</c> when the next <see cref="Save"/> would overwrite it.
		/// </summary>
		public static KeyBindingsFile Load(string path, Action<string> report = null)
		{
			if (path == null || !File.Exists(path))
				return new KeyBindingsFile();
			try {
				XDocument document = XDocument.Load(path);
				if (document.Root == null || document.Root.Name != RootName)
					throw new XmlException("The root element is not <" + RootName + ">.");
				var file = new KeyBindingsFile(document);
				if (file.IsFromNewerVersion && report != null)
					report("Keyboard shortcuts file " + path + " has version " + file.Version + "; this version reads what it understands and keeps the rest.");
				return file;
			} catch (Exception ex) when (ex is XmlException || ex is IOException || ex is UnauthorizedAccessException) {
				if (report != null)
					report("Keyboard shortcuts file " + path + " could not be read and is ignored: " + ex.Message);
				return new KeyBindingsFile { unreadableOriginalPath = path };
			}
		}

		/// <summary>Writes the file atomically (a temporary file in the same directory, then a rename).</summary>
		public void Save(string path)
		{
			if (path == null)
				throw new ArgumentNullException("path");
			string directory = Path.GetDirectoryName(Path.GetFullPath(path));
			Directory.CreateDirectory(directory);
			if (unreadableOriginalPath != null && File.Exists(unreadableOriginalPath)) {
				// Never destroy what could not be read: the user may want to repair it
				File.Copy(unreadableOriginalPath, unreadableOriginalPath + ".corrupt", true);
				unreadableOriginalPath = null;
			}
			string temporary = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
			try {
				using (var stream = File.Create(temporary))
					document.Save(stream);
				File.Move(temporary, path, true);
			} finally {
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
		}
	}
}
