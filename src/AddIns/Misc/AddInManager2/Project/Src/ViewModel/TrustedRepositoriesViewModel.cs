using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ICSharpCode.AddInManager2.Model;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.AddInManager2.ViewModel
{
	/// <summary>
	/// The repositories whose add-ins the AddIn Manager lists, installs and updates. Changes are collected
	/// while the options dialog is open and applied by <see cref="Save"/>.
	/// </summary>
	public class TrustedRepositoriesViewModel : Model<TrustedRepositoriesViewModel>
	{
		private string _newRepository = String.Empty;
		private string _selectedRepository;
		private string _message = String.Empty;

		public TrustedRepositoriesViewModel()
			: base()
		{
			Initialize();
		}

		public TrustedRepositoriesViewModel(IAddInManagerServices services)
			: base(services)
		{
			Initialize();
		}

		private void Initialize()
		{
			Repositories = new ObservableCollection<string>();
			AddCommand = new DelegateCommand(param => Add(), param => !String.IsNullOrWhiteSpace(NewRepository));
			RemoveCommand = new DelegateCommand(param => Remove(), param => SelectedRepository != null);
		}

		public ObservableCollection<string> Repositories
		{
			get;
			private set;
		}

		public ICommand AddCommand
		{
			get;
			private set;
		}

		public ICommand RemoveCommand
		{
			get;
			private set;
		}

		public string NewRepository
		{
			get
			{
				return _newRepository;
			}
			set
			{
				_newRepository = value ?? String.Empty;
				OnPropertyChanged(m => m.NewRepository);
			}
		}

		public string SelectedRepository
		{
			get
			{
				return _selectedRepository;
			}
			set
			{
				_selectedRepository = value;
				OnPropertyChanged(m => m.SelectedRepository);
			}
		}

		/// <summary>Why the last Add did not add anything, or empty.</summary>
		public string Message
		{
			get
			{
				return _message;
			}
			private set
			{
				_message = value ?? String.Empty;
				OnPropertyChanged(m => m.Message);
				OnPropertyChanged(m => m.HasMessage);
			}
		}

		public bool HasMessage
		{
			get
			{
				return !String.IsNullOrEmpty(_message);
			}
		}

		public void Load()
		{
			Repositories.Clear();
			foreach (RepositoryReference repository in AddInManager.Releases.TrustedRepositories)
			{
				Repositories.Add(repository.FullName);
			}
			Message = null;
		}

		private void Add()
		{
			Message = null;
			if (!RepositoryReference.TryParse(NewRepository, out RepositoryReference repository, out string error))
			{
				Message = error;
				return;
			}
			if (Repositories.Any(r => String.Equals(r, repository.FullName, StringComparison.OrdinalIgnoreCase)))
			{
				Message = String.Format(SD.ResourceService.GetString("AddInManager2.TrustedRepositories.AlreadyListed"), repository.FullName);
				return;
			}
			// An add-in runs inside the IDE with all of the user's rights: say so before trusting a repository.
			string question = String.Format(SD.ResourceService.GetString("AddInManager2.TrustedRepositories.TrustWarning"), repository.FullName);
			if (!MessageService.AskQuestion(question, SD.ResourceService.GetString("AddInManager.Title")))
			{
				return;
			}
			Repositories.Add(repository.FullName);
			NewRepository = String.Empty;
		}

		private void Remove()
		{
			if (SelectedRepository != null)
			{
				Repositories.Remove(SelectedRepository);
				SelectedRepository = null;
			}
		}

		/// <summary>Makes the release service trust exactly the repositories in the list.</summary>
		public void Save()
		{
			var wanted = Repositories
				.Select(r => RepositoryReference.TryParse(r, out RepositoryReference reference, out string e) ? reference : null)
				.Where(r => r != null)
				.ToList();
			foreach (RepositoryReference existing in AddInManager.Releases.TrustedRepositories.Where(c => !wanted.Contains(c)).ToList())
			{
				AddInManager.Releases.RemoveTrustedRepository(existing);
			}
			foreach (RepositoryReference added in wanted.Where(w => !AddInManager.Releases.TrustedRepositories.Contains(w)).ToList())
			{
				AddInManager.Releases.AddTrustedRepository(added.FullName, out string error);
			}
		}
	}
}
