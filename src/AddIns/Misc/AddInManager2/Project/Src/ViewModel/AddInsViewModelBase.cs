using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ICSharpCode.AddInManager2.Model;

namespace ICSharpCode.AddInManager2.ViewModel
{
	/// <summary>
	/// Base class of the AddIn Manager's three lists (installed, available, updates): a title with an
	/// update count, a search box, optional filters, and the list of add-in entries.
	/// </summary>
	public abstract class AddInsViewModelBase : Model<AddInsViewModelBase>, IDisposable
	{
		private int _highlightCount;
		private string _title;
		private bool _isReadingPackages;
		private bool _isExpandedinView;
		private bool _hasError;
		private string _errorMessage;
		private string _lastSelectedId;

		protected AddInsViewModelBase()
			: base()
		{
			Initialize();
		}

		protected AddInsViewModelBase(IAddInManagerServices services)
			: base(services)
		{
			Initialize();
		}

		private void Initialize()
		{
			_isReadingPackages = false;
			_isExpandedinView = false;
			_highlightCount = 0;
			_errorMessage = String.Empty;
			AddInPackages = new ObservableCollection<AddInPackageViewModelBase>();

			SearchCommand = new DelegateCommand(param => Search());
			UpdatePreinstalledFilterCommand = new DelegateCommand(param => UpdatePreinstalledFilter());
			UpdatePrereleaseFilterCommand = new DelegateCommand(param => UpdatePrereleaseFilter());
			InstallFromArchiveCommand = new DelegateCommand(param => InstallFromArchive());
			RefreshCommand = new DelegateCommand(param => Refresh());
		}

		public ICommand SearchCommand
		{
			get;
			private set;
		}

		public ICommand UpdatePreinstalledFilterCommand
		{
			get;
			private set;
		}

		public ICommand UpdatePrereleaseFilterCommand
		{
			get;
			private set;
		}

		public ICommand InstallFromArchiveCommand
		{
			get;
			private set;
		}

		public ICommand RefreshCommand
		{
			get;
			private set;
		}

		public void Dispose()
		{
			OnDispose();
			IsDisposed = true;
		}

		protected virtual void OnDispose()
		{
		}

		public bool IsDisposed
		{
			get;
			protected set;
		}

		public bool HasError
		{
			get
			{
				return _hasError;
			}
		}

		public string ErrorMessage
		{
			get
			{
				return _errorMessage;
			}
		}

		/// <summary>Shows <paramref name="message"/> under the list, or hides the message area when it is null or empty.</summary>
		protected void SetMessage(string message)
		{
			_errorMessage = message ?? String.Empty;
			_hasError = !String.IsNullOrEmpty(_errorMessage);
			OnPropertyChanged(vm => vm.HasError);
			OnPropertyChanged(vm => vm.ErrorMessage);
		}

		public int HighlightCount
		{
			get
			{
				return _highlightCount;
			}
			protected set
			{
				_highlightCount = value;
				OnPropertyChanged(vm => vm.HighlightCount);
				OnPropertyChanged(vm => vm.HasHighlightCount);
				OnPropertyChanged(vm => vm.TitleWithHighlight);
			}
		}

		public bool HasHighlightCount
		{
			get
			{
				return (_highlightCount > 0);
			}
		}

		public string Title
		{
			get
			{
				return _title;
			}
			set
			{
				_title = value;
				OnPropertyChanged(vm => vm.Title);
				OnPropertyChanged(vm => vm.TitleWithHighlight);
			}
		}

		public string TitleWithHighlight
		{
			get
			{
				if (_highlightCount > 0)
				{
					return String.Format("{0} ({1})", Title, _highlightCount);
				}
				else
				{
					return Title;
				}
			}
		}

		public ObservableCollection<AddInPackageViewModelBase> AddInPackages
		{
			get;
			private set;
		}

		public bool IsReadingPackages
		{
			get
			{
				return _isReadingPackages;
			}
			protected set
			{
				_isReadingPackages = value;
				OnPropertyChanged(m => m.IsReadingPackages);
			}
		}

		public bool IsExpandedInView
		{
			get
			{
				return _isExpandedinView;
			}
			set
			{
				_isExpandedinView = value;
				OnPropertyChanged(m => m.IsExpandedInView);
			}
		}

		/// <summary>Whether the list is split into pages; none of the lists is any more (the XAML still binds it).</summary>
		public bool IsPaged
		{
			get
			{
				return false;
			}
		}

		/// <summary>Rebuilds the list from what is known now (installed add-ins, or the last release lists); it never waits for the network.</summary>
		public abstract void ReadPackages();

		public bool IsSearchable
		{
			get;
			set;
		}

		public bool HasFilterForPreinstalled
		{
			get;
			set;
		}

		public bool HasFilterForPrereleases
		{
			get;
			set;
		}

		public bool AllowInstallFromArchive
		{
			get;
			set;
		}

		/// <summary>Whether the list offers a Refresh button (the lists that come from GitHub).</summary>
		public bool AllowRefresh
		{
			get;
			set;
		}

		public string SearchTerms
		{
			get;
			set;
		}

		public void Search()
		{
			ReadPackages();
			OnPropertyChanged(null);
		}

		protected virtual void UpdatePreinstalledFilter()
		{
		}

		protected virtual void UpdatePrereleaseFilter()
		{
		}

		protected virtual void InstallFromArchive()
		{
		}

		protected virtual void Refresh()
		{
		}

		public bool ShowPreinstalledAddIns
		{
			get;
			set;
		}

		public bool ShowPrereleases
		{
			get;
			set;
		}

		/// <summary>Whether <paramref name="text"/> matches the search box (always true when it is empty).</summary>
		protected bool MatchesSearch(params string[] texts)
		{
			if (String.IsNullOrWhiteSpace(SearchTerms))
			{
				return true;
			}
			string terms = SearchTerms.Trim();
			return texts.Any(t => (t != null) && (t.IndexOf(terms, StringComparison.CurrentCultureIgnoreCase) >= 0));
		}

		public void UpdateInstallationState()
		{
			foreach (var packageViewModel in AddInPackages)
			{
				packageViewModel.UpdateInstallationState();
			}
		}

		protected void UpdatePackageViewModels(IEnumerable<AddInPackageViewModelBase> newPackageViewModels)
		{
			StoreSelection();
			AddInPackages.Clear();
			foreach (var viewModel in newPackageViewModels)
			{
				AddInPackages.Add(viewModel);
			}
			UpdateInstallationState();
			RestoreSelection();
		}

		private void StoreSelection()
		{
			AddInPackageViewModelBase selectedModel = AddInPackages.FirstOrDefault(m => m.IsSelected);
			if (selectedModel != null) {
				_lastSelectedId = selectedModel.Id;
			}
		}

		private void RestoreSelection()
		{
			AddInPackageViewModelBase modelToSelect = AddInPackages.FirstOrDefault(m => m.Id == _lastSelectedId);
			if (modelToSelect != null) {
				modelToSelect.IsSelected = true;
			}
			_lastSelectedId = null;
		}
	}
}
