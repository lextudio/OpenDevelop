using System.Security.Cryptography;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;

namespace ICSharpCode.WpfDesign.MicrosoftHost;

static class MicrosoftWpfPreviewProgram {
	[STAThread] static int Main(string[] args) => DesignerChildHost.Run(args, "MicrosoftWpfPreview.Host", token => new Service(token));
}

sealed class Service(string expectedToken) : IDesignerChildService {
	string? sessionId;
	readonly ManualResetEventSlim shutdown = new(false);
	readonly DesignerDocumentRegistry<object> documents = new();
	[JsonRpcMethod("initialize")]
	public HostHandshake Initialize(string token, int protocolVersion, string session) {
		DesignerHostHandshakeValidator.Validate(expectedToken, token, protocolVersion);
		documents.Initialize(session);
		sessionId = session;
		return new HostHandshake { ProtocolVersion = DesignerProtocol.Version, Runtime = "Microsoft WPF", ProcessId = Environment.ProcessId, SessionId = session };
	}
	[JsonRpcMethod("session/open")] public DesignerSessionState Open(DesignerDocumentSnapshot s) => Load(s, create: true);
	[JsonRpcMethod("session/update")] public DesignerSessionState Update(DesignerDocumentSnapshot s) => Load(s, create: false);
	DesignerSessionState Load(DesignerDocumentSnapshot s, bool create) {
		try {
			if (create) documents.GetOrAdd(s.SessionId, s.DocumentId, static () => new object());
			else documents.Get(s.SessionId, s.DocumentId);
			var xaml = s.Files.FirstOrDefault(f => f.FileName == s.PrimaryFileName)?.Text ?? s.Files.FirstOrDefault()?.Text ?? "";
			var root = XamlReader.Parse(xaml) as FrameworkElement ?? throw new XamlParseException("Root is not a FrameworkElement.");
			root.Measure(new Size(1280, 720)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
			return new DesignerSessionState { Accepted = true, SessionId = sessionId!, DocumentId = s.DocumentId, Version = s.Version, RootType = root.GetType().FullName ?? "", Tree = Tree(root, "") };
		} catch (Exception e) { return new DesignerSessionState { Accepted = false, SessionId = sessionId!, DocumentId = s.DocumentId, Version = s.Version, Error = e.Message }; }
	}
	static DesignerElementNode Tree(FrameworkElement e, string id) {
		// IsVisible folds Visibility up the chain rather than using UIElement.IsVisible, which also
		// demands a live presentation source this offscreen host never has and so reports false for
		// the entire tree. Clients need this before drawing any overlay keyed off X/Y - see
		// DesignerElementNode.IsVisible.
		var n = new DesignerElementNode { Id = id, Name = e.Name ?? "", Type = e.GetType().FullName ?? "", Width = e.ActualWidth, Height = e.ActualHeight, IsVisible = Visible(e) };
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(e); i++) if (VisualTreeHelper.GetChild(e, i) is FrameworkElement child) n.Children.Add(Tree(child, id.Length == 0 ? i.ToString() : id + "," + i));
		return n;
	}
	/// <summary>Effective visibility by folding Visibility up the visual tree - the whole subtree
	/// under a Collapsed element is off screen even though each child's own Visibility is Visible.
	/// This host's tree walk is itself a visual-tree walk, so an unrealised (unselected) tab's
	/// content is simply absent rather than needing to be filtered.</summary>
	static bool Visible(DependencyObject e) {
		for (var current = e; current != null; current = VisualTreeHelper.GetParent(current))
			if (current is UIElement visual && visual.Visibility != Visibility.Visible) return false;
		return true;
	}
	[JsonRpcMethod("session/close")] public void Close(string session, string documentId) => documents.Remove(session, documentId, _ => { });
	[JsonRpcMethod("ping")] public void Ping() { }
	[JsonRpcMethod("shutdown")] public void Shutdown() { documents.CloseAll(_ => { }); shutdown.Set(); }
	public void WaitForShutdown() => shutdown.Wait();
	public void OnParentDisconnected() => shutdown.Set();
}
