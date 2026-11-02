using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.FormsDesigner.Host.Tests;

/// <summary>The edit plumbing GTK and MewUI hosts share: session and version checks, refusals and version bookkeeping.</summary>
public sealed class SourceModelHostTests
{
	sealed class Session : ISourceModelSession
	{
		public long Version { get; set; } = 1;
		public int Edits;
		public string Reason = "";
	}

	static (DesignerDocumentRegistry<Session> Documents, Session Session) Open()
	{
		var documents = new DesignerDocumentRegistry<Session>();
		documents.Initialize("s1");
		return (documents, documents.GetOrAdd("s1", "d1", () => new Session()));
	}

	static DesignerSessionState State(Session session) => new() { Version = session.Version };

	[Fact]
	public void Edit_AppliesTheOperation_BumpsTheVersionAndReturnsTheState()
	{
		var (documents, session) = Open();
		var state = documents.Edit("s1", "d1", 1, s => { s.Edits++; return true; }, "rejected", State);
		Assert.Equal(1, session.Edits);
		Assert.Equal(2, session.Version);
		Assert.Equal(2, state.Version);
	}

	[Fact]
	public void Edit_RefusalThrowsTheMessageWithTheEditorsReason_AndLeavesTheVersionAlone()
	{
		var (documents, session) = Open();
		session.Reason = "no such element";
		var ex = Assert.Throws<InvalidOperationException>(() => documents.Edit("s1", "d1", 1, _ => false, "The change was rejected.", State, s => s.Reason));
		Assert.Equal("The change was rejected. no such element", ex.Message);
		Assert.Equal(1, session.Version);
		session.Reason = "";
		Assert.Equal("rejected", Assert.Throws<InvalidOperationException>(() => documents.Edit("s1", "d1", 1, _ => false, "rejected", State, s => s.Reason)).Message);
	}

	[Fact]
	public void Edit_UsesTheEditedHookInsteadOfTheDefaultBump()
	{
		var (documents, session) = Open();
		documents.Edit("s1", "d1", 1, _ => true, "rejected", State, edited: s => { s.Version += 10; s.Edits = 99; });
		Assert.Equal(11, session.Version);
		Assert.Equal(99, session.Edits);
	}

	[Fact]
	public void Open_RefusesAStaleVersionWithTheSharedMessage_AnAlienSessionAndAnUnknownDocument()
	{
		var (documents, session) = Open();
		session.Version = 5;
		Assert.Equal("Stale version 4; current is 5.", Assert.Throws<InvalidOperationException>(() => documents.Open("s1", "d1", 4)).Message);
		Assert.Same(session, documents.Open("s1", "d1", 5));
		Assert.Throws<UnauthorizedAccessException>(() => documents.Open("other-session", "d1", 5));   // the registry's own session check
		Assert.Throws<InvalidOperationException>(() => documents.Open("s1", "missing", 5));
		Assert.Equal(0, session.Edits);
	}

	[Fact]
	public void AStaleEdit_NeverRunsTheOperation()
	{
		var (documents, session) = Open();
		Assert.Throws<InvalidOperationException>(() => documents.Edit("s1", "d1", 0, s => { s.Edits++; return true; }, "rejected", State));
		Assert.Equal(0, session.Edits);
		Assert.Equal(1, session.Version);
	}
}
