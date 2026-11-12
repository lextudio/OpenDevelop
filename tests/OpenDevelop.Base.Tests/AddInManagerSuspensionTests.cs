using System.ComponentModel;
using ICSharpCode.AddInManager2.Model;
using ICSharpCode.AddInManager2.ViewModel;
using ICSharpCode.Core;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The AddIn manager shows whether an AddIn is suspended and lets the user suspend or resume it with a button:
/// view model -> IAddInSetup.SwitchAddInSuspension -> ISDAddInManagement.Suspend/Resume -> AddInManager.Suspend/Resume.
/// </summary>
public class AddInManagerSuspensionTests
{
	/// <summary>The thin SharpDevelop AddIn management of the manager, with only what these tests use; Suspend/Resume are recorded.</summary>
	sealed class FakeSDAddInManagement : ISDAddInManagement
	{
		public readonly List<AddIn> Loaded = new List<AddIn>();
		public readonly List<string> Calls = new List<string>();
		public IReadOnlyList<AddIn> AddIns => Loaded;
		public string TempInstallDirectory => "";
		public string UserInstallDirectory => "";
		public string ConfigDirectory => "";
		public void AddToTree(AddIn addIn) => throw new NotSupportedException();
		public void AbortRemoveUserAddInOnNextStart(string identity) => throw new NotSupportedException();
		public void Enable(IList<AddIn> addIns) => throw new NotSupportedException();
		public void Disable(IList<AddIn> addIns) => throw new NotSupportedException();
		public void Suspend(IList<AddIn> addIns) { foreach (var a in addIns) { Calls.Add("suspend " + a.Manifest.PrimaryIdentity); a.Suspended = true; } }
		public void Resume(IList<AddIn> addIns) { foreach (var a in addIns) { Calls.Add("resume " + a.Manifest.PrimaryIdentity); a.Suspended = false; } }
		public void RemoveExternalAddIns(IList<AddIn> addIns) => throw new NotSupportedException();
		public void RemoveUserAddInOnNextStart(string identity) => throw new NotSupportedException();
		public AddIn Load(TextReader textReader) => throw new NotSupportedException();
		public AddIn Load(string fileName) => throw new NotSupportedException();
		public void AddExternalAddIns(IList<AddIn> addIns) => throw new NotSupportedException();
		public bool IsAddInManifestInExternalPath(AddIn addIn) => false;
	}

	sealed class FakeServices(IAddInManagerEvents events, IAddInSetup setup) : IAddInManagerServices
	{
		public IAddInManagerEvents Events => events;
		public IPackageRepositories Repositories => throw new NotSupportedException();
		public IAddInSetup Setup => setup;
		public INuGetPackageManager NuGet => throw new NotSupportedException();
		public IAddInManagerSettings Settings => throw new NotSupportedException();
		public ISDAddInManagement SDAddInManagement => throw new NotSupportedException();
	}

	sealed class Harness
	{
		public readonly FakeSDAddInManagement Management = new FakeSDAddInManagement();
		public readonly AddInManagerEvents Events = new AddInManagerEvents();
		public readonly AddInSetup Setup;
		public Harness() { Setup = new AddInSetup(Events, null!, Management); }

		public AddIn Load(string identity, bool enabled = true)
		{
			var tree = new AddInTreeImpl(null);
			var manifest = identity == null ? "" : $"<Manifest><Identity name='{identity}'/></Manifest>";
			var addIn = AddIn.Load(tree, new StringReader($"<AddIn name='{identity ?? "Anonymous"}'>{manifest}</AddIn>"));
			addIn.Enabled = enabled;
			Management.Loaded.Add(addIn);
			return addIn;
		}

		public OfflineAddInsViewModel ViewModel(AddIn addIn) =>
			new OfflineAddInsViewModel(new FakeServices(Events, Setup), new ManagedAddIn(addIn) { IsTemporary = false, IsUpdate = false });
	}

	[Fact]
	public void SwitchAddInSuspension_SuspendsAndThenResumes_AndIgnoresNothing()
	{
		var h = new Harness();
		var addIn = h.Load("Test.One");

		h.Setup.SwitchAddInSuspension(addIn);
		Assert.True(addIn.Suspended);
		h.Setup.SwitchAddInSuspension(addIn);
		Assert.False(addIn.Suspended);
		h.Setup.SwitchAddInSuspension(null!);

		Assert.Equal(new[] { "suspend Test.One", "resume Test.One" }, h.Management.Calls);
	}

	[Fact]
	public void TheViewModelShowsWhetherAnAddInIsSuspended_AndTheButtonTogglesIt()
	{
		var h = new Harness();
		var addIn = h.Load("Test.One");
		var vm = h.ViewModel(addIn);
		var changed = new List<string>();
		((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

		Assert.False(vm.IsSuspended);
		Assert.True(vm.IsSuspendingPossible);
		Assert.True(vm.SuspendPackageCommand.CanExecute(null));

		vm.SuspendPackageCommand.Execute(null);
		Assert.True(vm.IsSuspended);
		Assert.True(addIn.Suspended);
		Assert.Contains(nameof(vm.IsSuspended), changed);
		// The same button now resumes: it stays available
		Assert.True(vm.SuspendPackageCommand.CanExecute(null));

		vm.SuspendPackageCommand.Execute(null);
		Assert.False(vm.IsSuspended);
		Assert.False(addIn.Suspended);
		Assert.Equal(new[] { "suspend Test.One", "resume Test.One" }, h.Management.Calls);
	}

	[Fact]
	public void ASuspendedAddInStaysResumable_AndTheViewModelStartsOutSuspendedForARestartedOne()
	{
		var h = new Harness();
		var addIn = h.Load("Test.One");
		addIn.Suspended = true; // what CoreStartup applied from AddIns.xml before the manager was opened

		var vm = h.ViewModel(addIn);

		Assert.True(vm.IsSuspended);
		Assert.True(vm.SuspendPackageCommand.CanExecute(null));
		vm.SuspendPackageCommand.Execute(null);
		Assert.False(addIn.Suspended);
	}

	[Fact]
	public void SomeAddInsCannotBeSuspended()
	{
		var h = new Harness();

		// The manager itself is where a suspended AddIn is resumed; an AddIn without identity cannot be remembered; an AddIn that is not
		// loaded now (disabled, or only installed) contributes nothing to suspend
		var manager = h.ViewModel(h.Load("ICSharpCode.AddInManager2"));
		var anonymous = h.ViewModel(h.Load(null));
		var notLoaded = h.ViewModel(h.Load("Test.NotLoaded", enabled: false));

		foreach (var vm in new[] { manager, anonymous, notLoaded })
		{
			Assert.False(vm.IsSuspendingPossible);
			Assert.False(vm.SuspendPackageCommand.CanExecute(null));
		}
	}
}
