using System;
using System.Diagnostics;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>Reports that a command was started with its keyboard shortcut, then runs it.</summary>
	sealed class ShortcutCommandWrapper : System.Windows.Input.ICommand
	{
		readonly System.Windows.Input.ICommand baseCommand;
		readonly string featureName;

		public ShortcutCommandWrapper(System.Windows.Input.ICommand baseCommand, string featureName)
		{
			Debug.Assert(baseCommand != null);
			Debug.Assert(featureName != null);
			this.baseCommand = baseCommand;
			this.featureName = featureName;
		}

		public event EventHandler CanExecuteChanged {
			add { baseCommand.CanExecuteChanged += value; }
			remove { baseCommand.CanExecuteChanged -= value; }
		}

		public void Execute(object parameter)
		{
			ServiceSingleton.GetRequiredService<IAnalyticsMonitor>().TrackFeature(featureName, "Shortcut");
			baseCommand.Execute(parameter);
		}

		public bool CanExecute(object parameter)
		{
			return baseCommand.CanExecute(parameter);
		}
	}

	/// <summary>
	/// Two commands on one gesture: the first that can execute runs. This is what happens when two items on the same owner have the same
	/// shortcut (the same command in two menus, or two different commands that collide).
	/// </summary>
	sealed class AmbiguousCommandWrapper : System.Windows.Input.ICommand
	{
		readonly System.Windows.Input.ICommand first;
		readonly System.Windows.Input.ICommand second;

		public AmbiguousCommandWrapper(System.Windows.Input.ICommand first, System.Windows.Input.ICommand second)
		{
			this.first = first;
			this.second = second;
		}

		public event EventHandler CanExecuteChanged {
			add {
				first.CanExecuteChanged += value;
				second.CanExecuteChanged += value;
			}
			remove {
				first.CanExecuteChanged -= value;
				second.CanExecuteChanged -= value;
			}
		}

		public void Execute(object parameter)
		{
			if (first.CanExecute(parameter))
				first.Execute(parameter);
			else
				second.Execute(parameter);
		}

		public bool CanExecute(object parameter)
		{
			return first.CanExecute(parameter) || second.CanExecute(parameter);
		}
	}
}
