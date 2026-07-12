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

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Services;
using System.Threading.Tasks;

namespace ICSharpCode.GitAddIn
{
	public class RegisterEventsCommand : AbstractCommand
	{
		public override void Run()
		{
			SD.GetService<IProjectBrowserOverlayService>()?.RegisterProvider(OverlayIconManager.Provider);
			
			FileService.FileCreated += (sender, e) => {
				AddFileAsync(e.FileName).FireAndForget();
			};
			FileService.FileCopied += (sender, e) => {
				AddFileAsync(e.TargetFile).FireAndForget();
			};
			FileService.FileRemoved += (sender, e) => {
				RemoveFileAsync(e.FileName).FireAndForget();
			};
			FileService.FileRenamed += (sender, e) => {
				RenameFileAsync(e.SourceFile, e.TargetFile).FireAndForget();
			};
			FileUtility.FileSaved += (sender, e) => {
				ClearStatusCacheAndInvalidate(e.FileName);
			};
		}
		
		async Task AddFileAsync(string fileName)
		{
			if (!AddInOptions.AutomaticallyAddFiles)
				return;
			await Git.AddAsync(fileName);
			ClearStatusCacheAndInvalidate(fileName);
		}
		
		async Task RemoveFileAsync(string fileName)
		{
			if (!AddInOptions.AutomaticallyDeleteFiles)
				return;
			if (GitStatusCache.GetFileStatus(fileName) == GitStatus.Added) {
				await Git.RemoveAsync(fileName, true);
			}
			ClearStatusCacheAndInvalidate(fileName);
		}
		
		async Task RenameFileAsync(string sourceFileName, string targetFileName)
		{
			if (AddInOptions.AutomaticallyAddFiles)
				await Git.AddAsync(targetFileName);
			ClearStatusCacheAndInvalidate(targetFileName);
			await RemoveFileAsync(sourceFileName);
		}
		
		void ClearStatusCacheAndInvalidate(string fileName)
		{
			OverlayIconManager.Invalidate(fileName);
		}
	}
}
