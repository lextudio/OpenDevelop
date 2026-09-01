// Host-side seam for the common designer protocol (doc/technotes/designer-common.md).
// The shared designer canvas and the per-backend adapters depend on this contract; they
// never reference a runtime type.
//
// Scope rule (deliberate): this seam covers the PROTOCOL - session/document lifecycle and
// the element mutations every backend must speak. It does NOT cover presentation: how a
// backend's canvas control draws selection, handles mouse gestures, or lays out its own
// chrome is each adapter's own business (WinForms uses WPF Thumb + bubbling events; WinUI/Uno
// uses Preview events because its ScrollViewer swallows bubbling ones under LibreWPF). Those
// differences are legitimate adapter implementation detail, not protocol gaps.
//
// Operations only some runtimes can serve (theme switching, PNG export, app-resource
// injection, property reset, layout/z-order commands, default-event activation) live on the
// small optional capability interfaces below, mirroring how DDP treats unsupported commands:
// the host feature-detects and disables the corresponding UI rather than forcing every
// backend to stub them.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>Host-side view of one out-of-process designer host.</summary>
	public interface IDesignHostClient : IDisposable
	{
		#region Lifecycle

		/// <summary>Child process id (visible so project-code debugging can attach).</summary>
		int ProcessId { get; }

		bool IsAlive { get; }

		/// <summary>Tail of the child's stdout/stderr, for diagnosing startup or render failures.</summary>
		string ChildLog { get; }

		/// <summary>Identifies the child process; stable for its life (DDP envelope).</summary>
		string SessionId { get; }

		/// <summary>Identifies the document within the session (DDP envelope).</summary>
		string DocumentId { get; }

		event EventHandler HostExited;

		/// <summary>Liveness probe; a hung child is terminated by the shared timeout.</summary>
		Task PingAsync(CancellationToken cancellationToken = default);

		/// <summary>Requests bounded graceful shutdown (the child then exits).</summary>
		Task ShutdownAsync(CancellationToken cancellationToken = default);

		/// <summary>Kills the child process tree immediately.</summary>
		void TerminateHost();

		#endregion

		#region Document (session/*)

		/// <summary>Opens a document from a host-owned snapshot (<c>session/open</c>).</summary>
		Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default);

		/// <summary>Delivers newer source for an open document (<c>session/update</c>).</summary>
		Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default);

		/// <summary>Commits the child's current state back as an edit set (<c>session/flush</c>).
		/// A stale <paramref name="baseVersion"/> fails without a partial write.</summary>
		Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken = default);

		#endregion

		#region Mutations (design/*)

		/// <summary>Sets a property on an element (<c>design/set-property</c>).</summary>
		Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken = default);

		/// <summary>
		/// Inserts a new element under a parent (<c>design/add-element</c>). The backend picks
		/// what it needs out of <paramref name="item"/>: a CLR-type backend (WinForms) uses
		/// <see cref="DesignerToolboxItemInfo.TypeName"/> plus <paramref name="proposedName"/>;
		/// a markup backend (WinUI/Uno) materializes
		/// <see cref="DesignerToolboxItemInfo.Template"/>.
		/// </summary>
		Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, CancellationToken cancellationToken = default);

		/// <summary>Removes elements (<c>design/delete-elements</c>).</summary>
		Task<DesignerSessionState> DeleteElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default);

		/// <summary>Renames an element and its source references (<c>design/rename</c>).</summary>
		Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken = default);

		#endregion
	}

	/// <summary>Optional: resetting a property to its default (<c>design/reset-property</c>).
	/// Requires a defaults model the runtime can query, which markup backends lack today.</summary>
	public interface IDesignHostPropertyReset
	{
		Task<DesignerSessionState> ResetPropertyAsync(long baseVersion, string elementId, string propertyName, CancellationToken cancellationToken = default);
	}

	/// <summary>Optional: binding or clearing an event handler (<c>design/set-event</c>).
	/// An empty <paramref name="handlerName"/> clears the binding. Backends without a code-behind
	/// event model do not implement this interface, so the shell can disable event editing instead
	/// of issuing a request that fails at runtime.</summary>
	public interface IDesignHostEventBinding
	{
		Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken = default);
	}

	/// <summary>Optional: moving or resizing an element (<c>design/set-bounds</c>). Coordinates
	/// are design units; backends with integer layout round on their own side.</summary>
	public interface IDesignHostBounds
	{
		Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken = default);
	}

	/// <summary>Optional: mapping surface coordinates to an element (<c>design/hit-test</c>).
	/// Source-model-only backends do not implement this until they own authoritative geometry.</summary>
	public interface IDesignHostHitTesting
	{
		Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken = default);
	}

	/// <summary>Optional: creating/navigating to the element's default event handler.</summary>
	public interface IDesignHostDefaultEvent
	{
		Task<DesignerSessionState> ActivateDefaultEventAsync(long baseVersion, string elementId, CancellationToken cancellationToken = default);
	}

	/// <summary>Optional: alignment/spacing/z-order commands (<c>design/apply-layout</c>).
	/// Meaningful for absolute-positioned runtimes; markup layout panels position by rules.</summary>
	public interface IDesignHostLayout
	{
		Task<DesignerSessionState> ApplyLayoutAsync(long baseVersion, string operation, string[] elementIds, double deltaX, double deltaY, CancellationToken cancellationToken = default);
		Task<DesignerSessionState> SetZOrderAsync(long baseVersion, string elementId, bool bringToFront, CancellationToken cancellationToken = default);
	}

	/// <summary>Optional: Light/Dark design theme switching (<c>design/theme</c>).</summary>
	public interface IDesignHostTheme
	{
		Task<DesignerSessionState> SetThemeAsync(string theme, CancellationToken cancellationToken = default);
	}

	/// <summary>Optional: rendering the design to a PNG file (diagnostics/tests).</summary>
	public interface IDesignHostExport
	{
		Task<string> ExportPngAsync(string path, CancellationToken cancellationToken = default);
	}

	/// <summary>Optional: supplying App.xaml / merged resource content (<c>app/resources</c>).</summary>
	public interface IDesignHostAppResources
	{
		Task<DesignerAppResourcesResult> SetAppResourcesAsync(string xaml, CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// Implemented by a Properties-pad selection that can create and bind an event handler for a
	/// named event on the design surface (VS-style double-click on an Events row). UI-neutral;
	/// the UIShell feature-detects it on the selected object.
	/// </summary>
	public interface IEventBindingHost
	{
		/// <summary>Creates the conventional <c>&lt;component&gt;_&lt;event&gt;</c> handler, binds it,
		/// and persists the binding through the design session.</summary>
		void BindEvent(string eventName);
	}
}
