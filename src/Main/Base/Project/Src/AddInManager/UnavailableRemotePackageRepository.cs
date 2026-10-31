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
using System.Linq;
using NuGet;

namespace ICSharpCode.AddInManager2.Model
{
	/// <summary>
	/// Stands in for a remote (HTTP/HTTPS) add-in source where the legacy NuGet.Core engine cannot
	/// be used at all.
	///
	/// Reaching that engine's HTTP client initialises <c>NuGet.ProxyCache</c>, whose static
	/// constructor builds a <c>NuGet.Settings</c>, which synchronises access on
	/// <c>new Mutex("Global\\&lt;random token&gt;")</c>. On Unix, <c>System.Threading.Mutex</c>
	/// rejects a name containing a path separator, and that randomly generated token contains '/'
	/// in roughly half of all processes, so the static constructor throws:
	///
	/// <c>System.IO.IOException: The filename, directory name, or volume label syntax is
	/// incorrect. : 'Global\7DTH6CZ/XNHQTXHVPP8R/A7LDPQ0IOYHRQT9MOVIPA0='</c>
	///
	/// That happens inside the AddIn Manager's automatic update lookup, which runs on a
	/// background task during every startup, and it took the packaged macOS application down
	/// during the release bundle verification. It is the same named-mutex family of failures
	/// as the crashes recorded in doc/technotes/nuget.md.
	///
	/// So a remote source is represented by this empty repository on non-Windows platforms:
	/// its <see cref="Source"/> still gives the views their identity and equality, and it reports
	/// no packages, so nothing ever reaches the legacy HTTP stack. Local (file) sources keep
	/// using the real engine, which does not create the HTTP client.
	/// </summary>
	public sealed class UnavailableRemotePackageRepository : IPackageRepository
	{
		public UnavailableRemotePackageRepository(string source)
		{
			Source = source;
		}

		public string Source { get; }

		public PackageSaveModes PackageSaveMode { get; set; } = PackageSaveModes.Nupkg;

		public bool SupportsPrereleasePackages => true;

		public IQueryable<IPackage> GetPackages() => Enumerable.Empty<IPackage>().AsQueryable();

		public void AddPackage(IPackage package)
		{
			throw new NotSupportedException($"'{Source}' is an HTTP(S) add-in source, which the legacy NuGet.Core engine cannot query on this platform.");
		}

		public void RemovePackage(IPackage package)
		{
			throw new NotSupportedException($"'{Source}' is an HTTP(S) add-in source, which the legacy NuGet.Core engine cannot query on this platform.");
		}
	}
}
