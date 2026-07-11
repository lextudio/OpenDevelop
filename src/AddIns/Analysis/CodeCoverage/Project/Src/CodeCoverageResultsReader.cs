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
using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.CodeCoverage
{
	public class CodeCoverageResultsReader
	{
		List<string> fileNames = new List<string>();
		IFileSystem fileSystem = SD.FileSystem;
		List<string> missingFileNames = new List<string>();
		
		public CodeCoverageResultsReader()
		{
		}
		
		public void AddResultsFile(string fileName)
		{
			fileNames.Add(fileName);
		}
		
		public IEnumerable<CodeCoverageResults> GetResults()
		{
			foreach (string fileName in fileNames) {
				if (fileSystem.FileExists(FileName.Create(fileName))) {
					yield return ReadCodeCoverageResults(fileName);
				} else {
					missingFileNames.Add(fileName);
				}
			}
		}
		
		CodeCoverageResults ReadCodeCoverageResults(string fileName)
		{
			// The instrumented test process's own AltCover recorder can still be flushing/closing
			// its handle on this same file for a brief moment after the IDE's own collect step
			// reports done - reading two coverage runs back-to-back then hit a transient
			// IOException ("being used by another process") or the file briefly not existing.
			// Retry rather than fail the whole run over a handle-release race.
			const int maxAttempts = 10;
			for (int attempt = 1; attempt <= maxAttempts; attempt++) {
				try {
					using (TextReader reader = fileSystem.OpenText(FileName.Create(fileName))) {
						return new CodeCoverageResults(reader);
					}
				} catch (IOException) when (attempt < maxAttempts) {
					System.Threading.Thread.Sleep(200);
				}
			}
			// Unreachable: the last attempt above either returns or lets the exception propagate.
			throw new InvalidOperationException();
		}
		
		public IEnumerable<string> GetMissingResultsFiles()
		{
			return missingFileNames;
		}
	}
}
