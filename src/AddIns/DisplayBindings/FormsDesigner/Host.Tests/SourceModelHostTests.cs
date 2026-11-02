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
	public void Edit_RefusalIsANormalResult_WithTheMessageAndTheEditorsReason_AndTheVersionUnchanged()
	{
		var (documents, session) = Open();
		session.Reason = "no such element";
		var state = documents.Edit("s1", "d1", 1, _ => false, "The change was rejected.", State, s => s.Reason);
		Assert.False(state.Accepted);
		Assert.Equal("The change was rejected. no such element", state.Error);
		Assert.Contains(state.Diagnostics, d => d.Message == state.Error);
		Assert.Equal(1, state.Version);
		Assert.Equal(1, session.Version);
		session.Reason = "";
		Assert.Equal("rejected", documents.Edit("s1", "d1", 1, _ => false, "rejected", State, s => s.Reason).Error);
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
	public void AStaleEdit_IsRefusedAsANormalResult_AndNeverRunsTheOperation()
	{
		var (documents, session) = Open();
		session.Version = 5;
		var state = documents.Edit("s1", "d1", 4, s => { s.Edits++; return true; }, "rejected", State);
		Assert.False(state.Accepted);
		Assert.Equal("Stale version 4; current is 5.", state.Error);
		Assert.Equal(5, state.Version);
		Assert.Equal(0, session.Edits);
		Assert.Equal(5, session.Version);
	}

	[Fact]
	public void AnUnknownSessionOrDocument_IsAProtocolErrorAndStillThrows()
	{
		var (documents, _) = Open();
		Assert.Throws<UnauthorizedAccessException>(() => documents.Edit("other-session", "d1", 1, _ => true, "rejected", State));
		Assert.Throws<InvalidOperationException>(() => documents.Edit("s1", "missing", 1, _ => true, "rejected", State));
	}

	[Fact]
	public void AnOperationThatThrowsInvalidOperation_IsRefusedWithItsOwnReason_AndNoCreatedIdsLeakIntoTheRefusal()
	{
		var (documents, session) = Open();
		var state = documents.Edit("s1", "d1", 1, _ => throw new InvalidOperationException("Button:width: abc is not a number."), "rejected",
			s => { var st = State(s); st.CreatedElementId = "stale"; st.CreatedElementIds.Add("stale"); return st; });
		Assert.False(state.Accepted);
		Assert.Equal("Button:width: abc is not a number.", state.Error);
		Assert.Null(state.CreatedElementId);
		Assert.Empty(state.CreatedElementIds);
		Assert.Equal(1, session.Version);
		Assert.Throws<ArgumentException>(() => documents.Edit("s1", "d1", 1, _ => throw new ArgumentException("not a refusal"), "rejected", State));
	}
}
