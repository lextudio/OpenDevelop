using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ICSharpCode.UnitTesting
{
	internal sealed class UnitTestsPadViewModel : INotifyPropertyChanged
	{
		bool isLoading;
		int total;
		int passed;
		int failed;
		int skipped;

		public bool IsLoading {
			get => isLoading;
			set => SetField(ref isLoading, value);
		}

		public string TotalText => "Total: " + total;
		public string PassedText => passed.ToString();
		public string FailedText => failed.ToString();
		public string SkippedText => skipped.ToString();
		public string NotRunText => Math.Max(0, total - passed - failed - skipped).ToString();

		public void StartRun(int testCount)
		{
			total = testCount;
			passed = failed = skipped = 0;
			NotifyStatusChanged();
		}

		public void RecordResult(TestResult result)
		{
			if (result == null)
				return;

			switch (result.ResultType) {
				case TestResultType.Success:
					passed++;
					break;
				case TestResultType.Failure:
					failed++;
					break;
				case TestResultType.Ignored:
					skipped++;
					break;
			}
			NotifyStatusChanged();
		}

		void NotifyStatusChanged()
		{
			OnPropertyChanged(nameof(TotalText));
			OnPropertyChanged(nameof(PassedText));
			OnPropertyChanged(nameof(FailedText));
			OnPropertyChanged(nameof(SkippedText));
			OnPropertyChanged(nameof(NotRunText));
		}

		bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
		{
			if (Equals(field, value))
				return false;
			field = value;
			OnPropertyChanged(propertyName);
			return true;
		}

		void OnPropertyChanged(string propertyName) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

		public event PropertyChangedEventHandler PropertyChanged;
	}
}
