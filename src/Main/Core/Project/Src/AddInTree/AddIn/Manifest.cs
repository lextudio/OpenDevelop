// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
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
using System.Collections.ObjectModel;
using System.Xml;
using NuGet.Versioning;

namespace ICSharpCode.Core
{
	/// <summary>
	/// Stores information about the manifest of an AddIn.
	/// </summary>
	public class AddInManifest
	{
		List<AddInReference> dependencies = new List<AddInReference>();
		List<AddInReference> conflicts = new List<AddInReference>();
		Dictionary<string, Version> identities = new Dictionary<string, Version>();
		Version primaryVersion;
		SemanticVersion primarySemanticVersion;
		string primaryIdentity;
		
		public string PrimaryIdentity {
			get {
				return primaryIdentity;
			}
		}
		
		public Version PrimaryVersion {
			get {
				return primaryVersion;
			}
		}

		/// <summary>The primary Identity's version as a Semantic Version (with its pre-release), or null when it is not one.</summary>
		public SemanticVersion PrimarySemanticVersion {
			get {
				return primarySemanticVersion;
			}
		}
		
		public Dictionary<string, Version> Identities {
			get {
				return identities;
			}
		}
		
		public ReadOnlyCollection<AddInReference> Dependencies {
			get {
				return dependencies.AsReadOnly();
			}
		}
		
		public ReadOnlyCollection<AddInReference> Conflicts {
			get {
				return conflicts.AsReadOnly();
			}
		}
		
		void AddIdentity(string name, string version, string hintPath)
		{
			if (name.Length == 0)
				throw new AddInLoadException("Identity needs a name");
			foreach (char c in name) {
				if (!char.IsLetterOrDigit(c) && c != '.' && c != '_') {
					throw new AddInLoadException("Identity name contains invalid character: '" + c + "'");
				}
			}
			// A release tag is a Semantic Version (v1.2.3-rc.1+build) and the package manifest must equal the tag, so an Identity version can be one
			// too. The Version stays the numeric part (as before: it is what the add-in tree compares for dependencies); the full Semantic Version,
			// when the text is one, is kept next to it for the release logic (pre-release precedence). Other texts, such as the four-part versions
			// of the built-in add-ins, have no Semantic Version. This is the Identity only: in a Dependency or Conflict a '-' separates a version range
			// (5.9-5.10), see AddInReference.Create.
			SemanticVersion semVer = null;
			if (version != null && !version.StartsWith("@")) {
				SemanticVersion.TryParse(version, out semVer);
				int suffix = version.IndexOfAny(new[] { '-', '+' });
				if (suffix > 0)
					version = version.Substring(0, suffix);
			}
			Version v = AddInReference.ParseVersion(version, hintPath);
			if (primaryVersion == null) {
				primaryVersion = v;
				primarySemanticVersion = semVer;
			}
			if (primaryIdentity == null) {
				primaryIdentity = name;
			}
			identities.Add(name, v);
		}
		
		public void ReadManifestSection(XmlReader reader, string hintPath)
		{
			if (reader.AttributeCount != 0) {
				throw new AddInLoadException("Manifest node cannot have attributes.");
			}
			if (reader.IsEmptyElement) {
				throw new AddInLoadException("Manifest node cannot be empty.");
			}
			while (reader.Read()) {
				switch (reader.NodeType) {
					case XmlNodeType.EndElement:
						if (reader.LocalName == "Manifest") {
							return;
						}
						break;
					case XmlNodeType.Element:
						string nodeName = reader.LocalName;
						Properties properties = Properties.ReadFromAttributes(reader);
						switch (nodeName) {
							case "Identity":
								AddIdentity(properties["name"], properties["version"], hintPath);
								break;
							case "Dependency":
								dependencies.Add(AddInReference.Create(properties, hintPath));
								break;
							case "Conflict":
								conflicts.Add(AddInReference.Create(properties, hintPath));
								break;
							default:
								throw new AddInLoadException("Unknown node in Manifest section:" + nodeName);
						}
						break;
				}
			}
		}
	}
}
