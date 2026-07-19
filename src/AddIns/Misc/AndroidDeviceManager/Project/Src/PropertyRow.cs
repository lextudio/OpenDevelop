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

using System.Collections.Generic;
using System.ComponentModel;

namespace ICSharpCode.AndroidDeviceManager
{
	/// <summary>One editable row in the Advanced Settings property grid (AvdEditorWindow).</summary>
	public sealed class PropertyRow : INotifyPropertyChanged
	{
		readonly HardwareProperty definition;
		string textValue;

		public PropertyRow(HardwareProperty definition, string value)
		{
			this.definition = definition;
			textValue = string.IsNullOrEmpty(value) ? definition.DefaultValue : value;
		}

		public string Key => definition.Key;
		public string Title => definition.Title;
		public string Description => definition.Description;
		public string KindText => definition.Kind.ToString();
		public IReadOnlyList<string> EnumValues => definition.EnumValues;

		public string Value {
			get => textValue;
			set {
				if (textValue != value) {
					textValue = value;
					RaisePropertyChanged(nameof(Value));
					RaisePropertyChanged(nameof(BoolValue));
				}
			}
		}

		public bool BoolValue {
			get => string.Equals(textValue, "yes", System.StringComparison.OrdinalIgnoreCase);
			set => Value = value ? "yes" : "no";
		}

		public event PropertyChangedEventHandler PropertyChanged;

		void RaisePropertyChanged(string name)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		}
	}
}
