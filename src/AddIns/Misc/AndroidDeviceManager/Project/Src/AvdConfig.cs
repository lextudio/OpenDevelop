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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ICSharpCode.AndroidDeviceManager
{
	/// <summary>
	/// Reads/writes an AVD's config.ini: plain "key=value" lines (avdmanager/emulator's own
	/// format, not real INI sections). Preserves key order and any keys this tool doesn't know
	/// about so editing hw.* properties never drops unrelated settings.
	/// </summary>
	public sealed class AvdConfig
	{
		readonly List<string> orderedKeys = new List<string>();
		readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public IReadOnlyList<string> Keys => orderedKeys;

		public string Get(string key) => values.TryGetValue(key, out var value) ? value : null;

		public void Set(string key, string value)
		{
			if (!values.ContainsKey(key))
				orderedKeys.Add(key);
			values[key] = value;
		}

		public void Remove(string key)
		{
			if (values.Remove(key))
				orderedKeys.Remove(key);
		}

		public static AvdConfig Load(string path)
		{
			var config = new AvdConfig();
			if (!File.Exists(path))
				return config;

			foreach (var rawLine in File.ReadAllLines(path)) {
				var line = rawLine.Trim();
				if (line.Length == 0 || line.StartsWith("#"))
					continue;
				var separatorIndex = line.IndexOf('=');
				if (separatorIndex < 0)
					continue;
				var key = line.Substring(0, separatorIndex).Trim();
				var value = line.Substring(separatorIndex + 1).Trim();
				config.Set(key, value);
			}
			return config;
		}

		public void Save(string path)
		{
			var lines = orderedKeys.Select(key => key + "=" + values[key]);
			File.WriteAllLines(path, lines);
		}
	}
}
