// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

// Rewritten as a WPF dialog against Microsoft.CodeAnalysis symbols - the original WinForms
// ExtractInterfaceOptions-based version was commented out by upstream SharpDevelop itself back
// in 2011 ("Starting to port SD to new NRefactory") and was never revived across two separate
// rewrites since (see doc/technotes/csharp-roslyn.md). Not a WinForms port: this project only
// supports WPF.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;

using Microsoft.CodeAnalysis;

namespace ICSharpCode.SharpDevelop.Editor.Dialogs
{
	public partial class ExtractInterfaceDialog : Window, INotifyPropertyChanged
	{
		public event PropertyChangedEventHandler PropertyChanged;

		public sealed class MemberOption : INotifyPropertyChanged
		{
			public event PropertyChangedEventHandler PropertyChanged;

			public ISymbol Symbol { get; }
			public string DisplayText { get; }

			bool isChecked = true;
			public bool IsChecked {
				get => isChecked;
				set { isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
			}

			public MemberOption(ISymbol symbol)
			{
				Symbol = symbol;
				DisplayText = symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
			}
		}

		public ObservableCollection<MemberOption> Members { get; } = new ObservableCollection<MemberOption>();

		string interfaceName = "";
		public string InterfaceName {
			get => interfaceName;
			set { interfaceName = value; OnPropertyChanged(nameof(InterfaceName)); OnPropertyChanged(nameof(IsValid)); }
		}

		string newFileName = "";
		public string NewFileName {
			get => newFileName;
			set { newFileName = value; OnPropertyChanged(nameof(NewFileName)); OnPropertyChanged(nameof(IsValid)); }
		}

		public bool AddInterfaceToClass { get; set; } = true;
		public bool IncludeComments { get; set; }

		public bool IsValid =>
			!string.IsNullOrWhiteSpace(InterfaceName)
			&& Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(InterfaceName)
			&& !string.IsNullOrWhiteSpace(NewFileName);

		public ExtractInterfaceDialog()
		{
			InitializeComponent();
			DataContext = this;
			membersListBox.ItemsSource = Members;
		}

		public IReadOnlyList<ISymbol> ChosenMembers => Members.Where(m => m.IsChecked).Select(m => m.Symbol).ToArray();

		void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

		void OkButtonClick(object sender, RoutedEventArgs e)
		{
			if (!IsValid || !Members.Any(m => m.IsChecked)) {
				MessageBox.Show(this, "Please choose a valid interface name, file name, and at least one member.");
				return;
			}
			DialogResult = true;
		}
	}
}
