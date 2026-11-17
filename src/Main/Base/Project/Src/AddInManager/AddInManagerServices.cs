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
using ICSharpCode.AddInManager2.Model;

namespace ICSharpCode.AddInManager2
{
    /// <summary>
    /// Container for public services of AddInManager AddIn.
    /// </summary>
    public class AddInManagerServices
    {
    	private class AddInManagerServiceContainer : IAddInManagerServices
    	{
			public IAddInManagerEvents Events
			{
				get;
				set;
			}
    		
			public IAddInSetup Setup
			{
				get;
				set;
			}
    		
			public IAddInReleaseService Releases
			{
				get;
				set;
			}

			public IAddInManagerSettings Settings
			{
				get;
				set;
			}
			
			public ISDAddInManagement SDAddInManagement
			{
				get;
				set;
			}
    	}
    	
    	private static AddInManagerServiceContainer _container;
    	
        static AddInManagerServices()
		{
        	_container = new AddInManagerServiceContainer();
        	_container.Settings = new AddInManagerSettings();
			_container.Events = new AddInManagerEvents();
			_container.SDAddInManagement = new SDAddInManagement();
			_container.Setup = new AddInSetup(_container.Events, _container.SDAddInManagement);
			_container.Releases = CreateReleaseService(_container);
		}

		private static IAddInReleaseService CreateReleaseService(AddInManagerServiceContainer container)
		{
			var options = new GitHubReleaseSourceOptions();
			// Only an integration test (OD_TEST_MODE) may point the source at a local fake of GitHub; a
			// production run always talks to github.com, so an environment variable cannot redirect downloads.
			if (Environment.GetEnvironmentVariable("OD_TEST_MODE") == "1")
			{
				Uri api, download;
				if (Uri.TryCreate(Environment.GetEnvironmentVariable("OD_ADDIN_GITHUB_API"), UriKind.Absolute, out api))
				{
					options.ApiBase = api;
				}
				if (Uri.TryCreate(Environment.GetEnvironmentVariable("OD_ADDIN_GITHUB_DOWNLOAD"), UriKind.Absolute, out download))
				{
					options.DownloadBase = download;
				}
			}
			// The timeout covers a list request; a download reads its body after the headers, outside it.
			var http = new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler
			{
				AutomaticDecompression = System.Net.DecompressionMethods.All
			})
			{
				Timeout = TimeSpan.FromSeconds(30)
			};
			string cacheDirectory = System.IO.Path.Combine(container.SDAddInManagement.ConfigDirectory, "AddInReleaseCache");
			var source = new GitHubReleaseSource(http, new ReleaseCache(cacheDirectory), options);
			return new AddInReleaseService(source, container.Settings, container.Events, container.Setup, container.SDAddInManagement);
		}

		public static IAddInReleaseService Releases
        {
            get
            {
                return _container.Releases;
            }
        }

        public static IAddInManagerEvents Events
        {
            get
            {
                return _container.Events;
            }
        }


        public static IAddInSetup Setup
        {
            get
            {
                return _container.Setup;
            }
        }

        
        public static IAddInManagerSettings Settings
        {
            get
            {
                return _container.Settings;
            }
        }
        
        public static IAddInManagerServices Services
        {
        	get
        	{
        		return _container;
        	}
        }
    }
}
