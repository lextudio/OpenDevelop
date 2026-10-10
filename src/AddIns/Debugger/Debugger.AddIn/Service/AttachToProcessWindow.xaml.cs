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
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace ICSharpCode.SharpDevelop.Services
{
	/// <summary>
	/// A WPF process picker for "Attach to Process". Replaces the WinForms
	/// AttachToProcessForm, which is not built in this configuration.
	/// </summary>
	public partial class AttachToProcessWindow : Window
	{
		sealed class ProcessRow
		{
			public string Name { get; set; }
			public int Id { get; set; }
			public string Path { get; set; }
		}

		public AttachToProcessWindow()
		{
			InitializeComponent();
			processList.ItemsSource = EnumerateProcesses();
			if (processList.Items.Count > 0)
				processList.SelectedIndex = 0;
		}

		public int? SelectedProcessId { get; private set; }

		void AttachClick(object sender, RoutedEventArgs e)
		{
			if (processList.SelectedItem is ProcessRow row) {
				SelectedProcessId = row.Id;
				DialogResult = true;
			}
		}

		static IReadOnlyList<ProcessRow> EnumerateProcesses()
		{
			var currentId = Process.GetCurrentProcess().Id;
			var rows = new List<ProcessRow>();
			foreach (var process in Process.GetProcesses()) {
				try {
					if (process.HasExited || process.Id == currentId)
						continue;
					string path = null;
					try { path = process.MainModule?.FileName; } catch { /* access denied */ }
					rows.Add(new ProcessRow {
						Name = process.ProcessName,
						Id = process.Id,
						Path = path
					});
				} catch {
					// the process ended or is inaccessible
				} finally {
					// Dispose every handle: the rows keep only metadata, so nothing else owns it.
					process.Dispose();
				}
			}
			return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToList();
		}
	}
}
