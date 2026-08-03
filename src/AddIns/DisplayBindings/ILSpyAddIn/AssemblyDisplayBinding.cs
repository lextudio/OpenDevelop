// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// Makes the file types real ILSpy can open (.NET assemblies, NuGet packages, PDBs - mirroring
// ILSpy's own Commands/OpenCommand.cs filter) openable from OpenDevelop's own surfaces (File >
// Open dialog, project browser double-click): routes them to the hosted ILSpy
// (IlSpyWorkspaceHost.OpenAssemblyAsync, the same path as File > Open > Assembly) and lets the
// workbench switch to the "ILSpy" layout, instead of the "no display binding" error a binary
// assembly used to produce.

using System;
using System.Collections.Generic;
using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.ILSpyAddIn
{
	public sealed class AssemblyDisplayBinding : IDisplayBinding
	{
		static readonly HashSet<string> AssemblyExtensions = new(StringComparer.OrdinalIgnoreCase) {
			".dll",
			".exe",
			".winmd",
			".wasm",
			".nupkg",
			".pdb"
		};

		static bool IsAssembly(FileName fileName)
		{
			return AssemblyExtensions.Contains(Path.GetExtension(fileName.ToString()));
		}

		public bool IsPreferredBindingForFile(FileName fileName)
		{
			return IsAssembly(fileName);
		}

		public bool CanCreateContentForFile(FileName fileName)
		{
			return IsAssembly(fileName);
		}

		public double AutoDetectFileContent(FileName fileName, Stream fileContent, string detectedMimeType)
		{
			return IsAssembly(fileName) ? 1 : 0;
		}

		public IViewContent CreateContentForFile(OpenedFile file)
		{
			// The hosted-ILSpy flow creates and shows its own view content (the decompiled-code
			// document tab) asynchronously inside OpenAssemblyAsync - there is no synchronous
			// view content to return here (FileService tolerates null; see LoadFileWrapper).
			_ = IlSpyWorkspaceHost.OpenAssemblyAsync(file.FileName);
			return null;
		}
	}
}
