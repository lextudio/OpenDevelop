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
using System.Threading;

using TomsToolbox.Essentials;

#nullable enable

namespace ICSharpCode.ILSpy.Util
{
	// The generic publish/subscribe primitive on its own. This file is SOURCE-LINKED: it is compiled
	// into ICSharpCode.Core (see ICSharpCode.Core.csproj's <Compile Include ... Link>) and
	// Compile-Removed from ILSpyAddIn.csproj, so exactly one MessageBus/MessageBus<T> identity exists
	// in the process. Two copies would be two independent static buses: a Send from the shell (which
	// references ICSharpCode.Core) would never reach a Subscriber registered by this AddIn, and vice
	// versa - a silent, per-message-instance kind of wrong.
	//
	// It stays here, in ILSpy's own tree, because this is where the primitive came from; the
	// ILSpy-SPECIFIC message types deliberately do NOT live here any more - they are in
	// MessageBusMessages.cs, which is not linked into Core (they reference TabPageModel,
	// SessionSettings, ViewState, ... and would drag ILSpy into the shell).
	//
	// Subscriptions are weak (TomsToolbox.Essentials' WeakEventSource<T>) because ILSpy's tree nodes
	// and document views subscribe per-instance from their constructors - a plain strong event would
	// keep every closed node alive for the lifetime of the process.

	public static class MessageBus
	{
		public static void Send<T>(object? sender, T e)
			where T : EventArgs
		{
			MessageBus<T>.Send(sender, e);
		}
	}

	/// <summary>
	/// Simple, minimalistic message bus.
	/// </summary>
	/// <typeparam name="T">The type of the message event arguments</typeparam>
	public static class MessageBus<T>
		where T : EventArgs
	{
		private static readonly WeakEventSource<T> subscriptions = new();

		/// <summary>
		/// Subscribes a handler and returns a deterministic unsubscription token. Prefer this for
		/// long-lived Shell/AddIn objects that have a well-defined disposal boundary; the event
		/// syntax remains available for compatibility with the upstream ILSpy sources.
		/// </summary>
		public static IDisposable Subscribe(EventHandler<T> handler)
		{
			if (handler == null)
				throw new ArgumentNullException(nameof(handler));
			subscriptions.Subscribe(handler);
			return new Subscription(handler);
		}

		public static event EventHandler<T> Subscribers {
			add => subscriptions.Subscribe(value);
			remove => subscriptions.Unsubscribe(value);
		}

		public static void Send(object? sender, T e)
		{
			subscriptions.Raise(sender!, e);
		}

		sealed class Subscription : IDisposable
		{
			EventHandler<T>? handler;

			public Subscription(EventHandler<T> handler) => this.handler = handler;

			public void Dispose()
			{
				var value = Interlocked.Exchange(ref handler, null);
				if (value != null)
					subscriptions.Unsubscribe(value);
			}
		}
	}
}
