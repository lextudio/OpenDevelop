// Copyright (c) 2024 Tom Englert for the SharpDevelop Team
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
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Navigation;

using ICSharpCode.ILSpy.TextView;
using ICSharpCode.ILSpy.ViewModels;

#nullable enable

namespace ICSharpCode.ILSpy.Util
{
	// The ILSpy-specific message payloads, split out from MessageBus.cs so that only the generic
	// primitive (that file) is source-linked into ICSharpCode.Core. Everything here references
	// ILSpy types (TabPageModel, SessionSettings, ViewState, RequestNavigateEventArgs) and must
	// therefore stay in this assembly. Sent/received through ICSharpCode.ILSpy.Util.MessageBus,
	// which is compiled into ICSharpCode.Core.

	public abstract class WrappedEventArgs<T> : EventArgs
	{
		private readonly T inner;

		protected WrappedEventArgs(T inner)
		{
			this.inner = inner;
		}

		public static implicit operator T(WrappedEventArgs<T> outer)
		{
			return outer.inner;
		}
	}

	public class CurrentAssemblyListChangedEventArgs(NotifyCollectionChangedEventArgs e) : WrappedEventArgs<NotifyCollectionChangedEventArgs>(e);
	public class TabPagesCollectionChangedEventArgs(NotifyCollectionChangedEventArgs e) : WrappedEventArgs<NotifyCollectionChangedEventArgs>(e);

	public class SettingsChangedEventArgs(PropertyChangedEventArgs e) : WrappedEventArgs<PropertyChangedEventArgs>(e);

	public class NavigateToReferenceEventArgs(object reference, object? source = null, bool inNewTabPage = false) : EventArgs
	{
		public object Reference { get; } = reference;
		public object? Source { get; } = source;
		public bool InNewTabPage { get; } = inNewTabPage;
	}

	public class NavigateToEventArgs(RequestNavigateEventArgs request, bool inNewTabPage = false) : EventArgs
	{
		public RequestNavigateEventArgs Request { get; } = request;

		public bool InNewTabPage { get; } = inNewTabPage;
	}

	public class AssemblyTreeSelectionChangedEventArgs() : EventArgs;

	public class ApplySessionSettingsEventArgs(SessionSettings sessionSettings) : EventArgs
	{
		public SessionSettings SessionSettings { get; } = sessionSettings;
	}

	public class MainWindowLoadedEventArgs() : EventArgs;

	public class ActiveTabPageChangedEventArgs(ViewState? viewState) : EventArgs
	{
		public ViewState? ViewState { get; } = viewState;
	}

	public class ResetLayoutEventArgs : EventArgs;

	public class ShowAboutPageEventArgs(TabPageModel tabPage) : EventArgs
	{
		public TabPageModel TabPage { get; } = tabPage;
	}

	public class ShowSearchPageEventArgs(string? searchTerm) : EventArgs
	{
		public string? SearchTerm { get; } = searchTerm;
	}

	public class CheckIfUpdateAvailableEventArgs(bool notify = false) : EventArgs
	{
		public bool Notify { get; } = notify;
	}
}
