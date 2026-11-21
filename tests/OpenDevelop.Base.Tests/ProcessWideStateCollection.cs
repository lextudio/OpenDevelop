using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Tests that touch state shared by the whole test process run one after another in this collection:
/// <list type="bullet">
/// <item>The SharpDevelop service container (<c>SD.InitializeForUnitTests</c> replaces it, the matching
/// tear-down removes it). Ten classes did this from their own collections, so they raced each other and one
/// could lose its AddInTree in the middle of a test.</item>
/// <item>WPF controls (<c>UIFact</c>). StaFact gives every collection a UI thread of its own, but the icons
/// <c>PresentationResourceService</c> caches stay unfrozen on purpose so they can follow a theme switch, which
/// is only valid on one UI thread (as in the IDE). A cached icon built by one class's thread and assigned to an
/// Image by another's failed with "a different thread owns it".</item>
/// </list>
/// Everything else keeps running in parallel.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ProcessWideStateCollection
{
	public const string Name = "Process-wide state";
}
