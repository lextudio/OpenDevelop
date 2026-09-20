# When to use `MessageBus<T>`

`ICSharpCode.ILSpy.Util.MessageBus` / `MessageBus<T>` (`src/AddIns/DisplayBindings/ILSpyAddIn/ILSpy/Util/MessageBus.cs`)
is a minimal, per-type static publish/subscribe bus:

```csharp
public static class MessageBus<T> where T : EventArgs
{
    public static event EventHandler<T> Subscribers;
    public static void Send(object sender, T e) => Subscribers?.Invoke(sender, e);
}
```

One static event per closed generic type `T`. A sender calls `MessageBus.Send(this, new
FooEventArgs(...))`; anyone who cares does `MessageBus<FooEventArgs>.Subscribers += Handler;`.
Sender and subscriber never reference each other - they only both reference the message type.

It is source-linked into `ICSharpCode.Core` (see that project's `<Compile Include ... Link>` and
`ICSharpCode.Core.csproj`'s comment) so the primitive is reachable from the shell
(`SharpDevelop.csproj`) as well as every AddIn, without either side referencing the other. The
ILSpy-*specific* message payloads (`SettingsChangedEventArgs`, `ActiveTabPageChangedEventArgs`,
...) stay in `MessageBusMessages.cs`, in ILSpyAddIn's own tree - they reference ILSpy-only types
(`TabPageModel`, `SessionSettings`, `ViewState`) and have no reason to live in the shell. Follow
that split for any new message type: put the type as close as possible to whatever domain it
describes, not next to the bus itself.

This note exists because it is easy to reach for `MessageBus<T>` out of habit once it is visible in
a file, in places where a plain C# event, an interface, or a direct method call would be simpler,
more discoverable ("Find All References" works on those; it does not usefully work on a
`MessageBus<T>` type parameter), and just as correct. The sections below are drawn from every
`MessageBus.Send`/`MessageBus<T>.Subscribers` use in the codebase as of 2026-09, grouped by the
actual problem each one solves - use them to decide whether a *new* piece of code has one of these
problems, or whether it should just use a normal reference.

## Use it when

### 1. A dependency-direction boundary that a normal reference cannot cross

The motivating case for sinking the bus into `ICSharpCode.Core`: `AvalonDockLayout`
(`SharpDevelop.csproj`) needs to announce "the dock's raw active content changed" - which content
became active - so that `PropertyPadViewModel` (also `SharpDevelop.csproj`, so a plain event would
have worked *there*) and, in principle, any AddIn-hosted pane can react. `ILSpyAddIn.csproj`
*references* `SharpDevelop.csproj`; the reverse reference is impossible (circular project
reference, a hard MSBuild error) - so if the shell ever needs to notify code that lives in an AddIn
without a matching interface/service already registered for that purpose, a plain event or
callback cannot express it. `MessageBus<T>` can, because both sides only need to reference the
(much lower) assembly the message type and the bus live in - see
`ActiveDockContentChangedEventArgs` (`IWorkbenchLayout.cs`) for the concrete example.

Check first whether an existing service/interface (`SD.Services`, MEF export) already lets the
lower layer call up - if it does, use that; only reach for the bus when no such seam exists and
adding one would itself be circular or disproportionate.

### 2. Broadcasting to an open, unknown set of subscribers

`SettingsChangedEventArgs`, sent once from `SettingsService`, is picked up independently by
`ILSpyTreeNode`, `DecompilerTextView`, `ThemeManager`, `WindowStyleManagerBehavior`, `SearchPane`,
`SearchPaneModel`, `DebugSteps` - none of which know about each other, and none of which
`SettingsService` should have to enumerate or hold references to. Same shape for
`CurrentAssemblyListChangedEventArgs` (assembly list changed - `AnalyzerRootNode`, `SearchPane`,
`DockWorkspace` all react) and `ActiveTabPageChangedEventArgs` (`DockWorkspace.ActiveTabPage`
setter sends it; `AssemblyTreeModel` subscribes).

The tell: the sender's job is "this fact changed", not "and now go tell X, Y, Z" - if you find
yourself writing (or would have to write) an explicit list of who to notify inside the sender, that
list is the sign this pattern fits; if there is exactly one consumer, it usually does not.

### 3. Converging many unrelated senders into one handler

`NavigateToReferenceEventArgs` is sent from ~50+ otherwise-unrelated call sites - metadata table
tree nodes (`MethodTableTreeNode`, `TypeDefTableTreeNode`, ...), analyzer nodes, the decompiler
text view's hyperlinks, `GoToTokenCommand` - and received by exactly one place,
`AssemblyTreeModel.wpf.cs`'s `JumpToReference`. Threading a reference to `AssemblyTreeModel`
through every one of those leaf classes' constructors would be real coupling for no benefit; they
do not need to know who acts on a navigation request, only that one exists.

The tell is the inverse of #2: many senders, one (or a small, fixed) receiver, and the senders are
otherwise leaf/data-ish classes that should not need to know about the orchestration class that
reacts.

### 4. Observing an internal pipeline's checkpoint without adopting the whole pipeline

`IlSpyWorkspaceHost.cs` (OpenDevelop's own glue, not linked from upstream ILSpy) hosts
`AssemblyTreeModel`'s tree but deliberately does **not** use ILSpy's own tab/document system
(`Docking.DockWorkspace`, `TabPageModel`) - decompiled output opens as a plain OpenDevelop document
instead. But `AssemblyTreeModel.DecompileSelectedNodes()`, upstream's normal reaction to a
selection change, calls straight into that tab system. Calling it directly would mean either
adopting ILSpy's docking too, or fighting it. Instead, `IlSpyWorkspaceHost` subscribes to the same
`MessageBus<AssemblyTreeSelectionChangedEventArgs>` that `AssemblyTreeModel` already sends on every
selection change, and does its own decompile-into-an-OpenDevelop-tab in the handler - reusing the
*signal* upstream already produces without reusing (or reimplementing) the *reaction* it triggers
upstream.

This only works because the signal already exists as a message. It is not a reason to introduce a
*new* message type purely so a second module can "tap in" later - only reach for this when, like
here, upstream already publishes the exact checkpoint you need.

### 5. Fire-and-forget commands to whichever part of the app knows how to do it

`ShowAboutPageEventArgs`, `ShowSearchPageEventArgs`, `ResetLayoutEventArgs`,
`CheckIfUpdateAvailableEventArgs` read as commands ("show the About page", "reset the layout"), not
facts. `AssemblyTreeModel` sends them because *it* is not the code that owns pane-showing or
layout-resetting logic, and does not want a compile-time dependency on whatever does. This is the
same shape as #1/#3 in effect (avoiding an unwanted reference), but the message reads as an
imperative rather than a notification - naming it that way (`ShowXEventArgs`, not
`XRequestedEventArgs` inconsistently) keeps the two flavors (fact vs. command) visually distinct at
the call site.

## Do not use it when

### There is exactly one sender and one owned receiver

If a class creates, owns, or otherwise already has a direct reference to the thing that needs to
react, use a plain method call or a normal instance event on that reference. Nothing above applies
when the relationship is 1:1 and already wired - a `MessageBus<T>` there only adds an indirection
that "Find All References" cannot follow and a reader has to know to go search for by type name.

### A normal reference would not be circular

Before reaching for the bus, check whether the sender could just reference the receiver's assembly
(or an existing service/interface on it) without creating a cycle. `MessageBus<T>` earns its keep
specifically where that check fails (see #1) - it is not a general substitute for "I don't feel
like adding a `ProjectReference`".

### You would be faking an event to reuse someone else's constructor-time setup

`IlSpyWorkspaceHost.cs` calls `assemblyTreeModel.Initialize()` directly rather than sending a fake
`MainWindowLoadedEventArgs` (the message real ILSpy's own startup path uses to trigger it) - see
the comment at that call site. If the only reason to send a message is that some other code
happens to already be listening for it, and there is no independent reason a real instance of that
message would ever occur in this host, call the method the handler would have called instead. A
message should represent something that genuinely happened, not a mechanism to jump to a
handler.

### The payload needs a type that is not accessible for both ends

A message type must be visible to every project that sends or subscribes to it. If the natural
representation of "what happened" needs a type that only one side can reference (as `ActiveTabPage`
needs `ViewState`/`TabPageModel`, which only ILSpyAddIn has), keep that message type in the layer
that owns those types - do not drag the dependency down into `ICSharpCode.Core` just to make a
richer payload possible. Either shape the message around data every subscriber can already see
(plain data, not domain objects - see `ActiveDockContentChangedEventArgs.Content`, typed `object`,
deliberately not `ToolPaneModel`, since `ICSharpCode.Core` cannot reference that type), or accept
that the message belongs at the higher layer and lower-layer code cannot subscribe to it.

## Implementation notes for a new message type

- One class per message, deriving from `EventArgs` (or `WrappedEventArgs<T>` when it's just
  rebroadcasting someone else's `EventArgs`, e.g. `SettingsChangedEventArgs` wrapping
  `PropertyChangedEventArgs` - `WrappedEventArgs<T>` gives an implicit conversion back to the inner
  type so a handler can treat it like the original event without a wrapper-unwrapping call at every
  site).
- Put the type in the same project as the thing it describes (see the dependency-boundary point
  above), not necessarily next to `MessageBus.cs` itself.
- Subscriptions on the ILSpy-linked bus are weak (`TomsToolbox.Essentials.WeakEventSource<T>`)
  because many real subscribers are short-lived, per-instance objects (a tree node exists once per
  visible assembly/type/member and subscribes from its own constructor - `ILSpyTreeNode`,
  `MethodDebugInformationTableTreeNode`, and dozens more). A plain strong event there would pin
  every node ever created for the life of the process. Keep using weak subscription for any new
  message type that a short-lived object might subscribe to; a strong event is only safe when every
  subscriber is a long-lived singleton (e.g. a MEF `[Shared]` `ToolPaneModel`).
- Prefer an unqualified `MessageBus.Send<T>` type name at the call site (ILSpyAddIn's
  `GlobalUsings.cs` already brings `ICSharpCode.ILSpy.Util` into scope project-wide) over fully
  qualifying it everywhere - the existing ~80 call sites all rely on that global using, and a new
  one added elsewhere (e.g. `SharpDevelop.csproj`) should add its own `using ICSharpCode.ILSpy.Util;`
  rather than reinventing another bus.
