// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Threading;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Thread ownership of a solution's project model (the solution, its projects and their collections).
	///
	/// The model is built by one thread and then published to the main thread:
	/// <list type="bullet">
	/// <item>While <b>unpublished</b> the model is owned by the thread that created it. Nothing outside the
	/// builder can see it yet (it is not <see cref="IProjectService.CurrentSolution"/> and has no subscribers),
	/// so the solution loader can construct it on a background thread.</item>
	/// <item><see cref="Publish"/> hands it to the main thread for good: from then on every access check is
	/// <c>SD.MainThread.VerifyAccess()</c>, exactly as before this type existed, and <see cref="Published"/>
	/// lets the model start what only a live model needs (file watchers).</item>
	/// </list>
	/// A model built on the main thread (synchronous load, new-project wizard) behaves exactly as it always has.
	/// </summary>
	public sealed class ModelThreadAffinity
	{
		/// <summary>An affinity that is already published: for model objects created outside a solution build.</summary>
		public static ModelThreadAffinity MainThread { get; } = new ModelThreadAffinity(Environment.CurrentManagedThreadId, published: true);

		volatile int ownerThreadId;
		volatile bool isPublished;

		ModelThreadAffinity(int ownerThreadId, bool published)
		{
			this.ownerThreadId = ownerThreadId;
			this.isPublished = published;
		}

		/// <summary>A new, unpublished affinity owned by the calling thread.</summary>
		public static ModelThreadAffinity ForCurrentThread()
		{
			return new ModelThreadAffinity(Environment.CurrentManagedThreadId, published: false);
		}

		/// <summary>Whether the model has been handed to the main thread.</summary>
		public bool IsPublished {
			get { return isPublished; }
		}

		/// <summary>Raised once, on the main thread, by <see cref="Publish"/>.</summary>
		public event EventHandler Published;

		/// <summary>Whether the calling thread may use the model now.</summary>
		public bool CheckAccess()
		{
			return isPublished ? SD.MainThread.CheckAccess() : Environment.CurrentManagedThreadId == ownerThreadId;
		}

		/// <summary>Throws if the calling thread may not use the model now.</summary>
		public void VerifyAccess()
		{
			if (isPublished) {
				SD.MainThread.VerifyAccess();
			} else if (Environment.CurrentManagedThreadId != ownerThreadId) {
				throw new InvalidOperationException("This project model is still being built by another thread and may only be used by that thread until it is published.");
			}
		}

		/// <summary>
		/// Hands an unpublished model to the calling thread: for the consumer of a build that has
		/// <i>finished</i> (e.g. after awaiting the background task that built the solution), which then
		/// publishes or disposes it. Calling it while the builder still uses the model is a bug.
		/// </summary>
		public void TransferOwnershipToCurrentThread()
		{
			if (isPublished)
				throw new InvalidOperationException("A published model belongs to the main thread.");
			ownerThreadId = Environment.CurrentManagedThreadId;
		}
		
		/// <summary>
		/// Hands the model to the main thread. Must be called on the main thread; idempotent.
		/// </summary>
		public void Publish()
		{
			SD.MainThread.VerifyAccess();
			if (isPublished)
				return;
			isPublished = true;
			Published?.Invoke(this, EventArgs.Empty);
			Published = null;
		}
	}

	/// <summary>Implemented by model owners (the solution) that projects inherit their thread affinity from.</summary>
	internal interface IHasModelThreadAffinity
	{
		ModelThreadAffinity ThreadAffinity { get; }
	}
}
