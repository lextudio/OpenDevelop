using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Data;

namespace XamlStudio.Toolkit.Models;

// Namespace-only WinUI port of the XAML Studio model. Behaviour intentionally matches upstream.
public sealed class XamlBindingInfo : INotifyPropertyChanged
{
    public enum XamlBindingState { NotBound, Successful, ConversionError }
    public delegate void BindingUpdatedHandler(XamlBindingInfo sender, ConversionRecord record, object newValue);
    public event BindingUpdatedHandler BindingUpdated;
    public string OriginalBindingString { get; set; }
    public IValueConverter Converter { get; set; }
    public string ConverterKey { get; set; }
    public object ConverterParameter { get; set; }
    public uint Line { get; }
    public uint Column { get; }
    public int Length => OriginalBindingString.Length;
    public XAttribute PropertyAttribute { get; set; }
    public string PropertyName { get; set; }
    public string ElementTypeName { get; set; }
    public string ElementName { get; set; }
    public ObservableCollection<ConversionRecord> BindingHistory { get; } = new();
    public int Id { get; } = Services.IdGenerator.Next();
    public Services.XamlRenderService Service { get; internal set; }
    public bool HasBinded => BindingHistory.Count != 0;
    public bool HasConverter => Converter != null;
    public object LastConvertedValue => BindingHistory.LastOrDefault()?.Value;
    public object LastConvertedResult => BindingHistory.LastOrDefault()?.Result;
    public object LastConvertedResultOrValue => !HasBinded ? null : BindingHistory[^1].HasResult ? BindingHistory[^1].Result : BindingHistory[^1].Value;
    public string LastConvertedResultOrValueString => LastConvertedResultOrValue?.ToString() ?? string.Empty;
    public string LastExceptionMessage => BindingHistory.LastOrDefault()?.ExceptionObject?.Message;
    public DateTime FirstSetTime => BindingHistory.FirstOrDefault()?.TimeStamp ?? DateTime.MinValue;
    public DateTime LastConvertedTime => BindingHistory.LastOrDefault()?.TimeStamp ?? DateTime.MinValue;
    public XamlBindingState LastKnownBindingState => !HasBinded ? XamlBindingState.NotBound : BindingHistory[^1].IsSuccessful ? XamlBindingState.Successful : XamlBindingState.ConversionError;
    public long BindingCount => BindingHistory.Count;

    public XamlBindingInfo(uint line, uint column, string binding) { Line = line; Column = column; OriginalBindingString = binding; }
    public object NewValue(object value) => Add(new ConversionRecord(this, value), value);
    public object NewConversion(object value, object result) => Add(new ConversionRecord(this, value, result), result);
    public object NewException(object value, Exception error) { Add(new ConversionRecord(this, value, error), error); return null; }
    object Add(ConversionRecord record, object value) { BindingHistory.Add(record); BindingUpdated?.Invoke(this, record, value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); return value; }
    public override string ToString() => (string.IsNullOrWhiteSpace(ElementName) ? ElementTypeName : ElementName + "[" + ElementTypeName + "]") + "." + PropertyName;
    public event PropertyChangedEventHandler PropertyChanged;
}
