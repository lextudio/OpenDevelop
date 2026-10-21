using System.ComponentModel;
using System.Windows;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>One visible line of the call tree grid.</summary>
internal sealed class CallTreeRow : INotifyPropertyChanged
{
	const double IndentPerLevel = 16;
	bool isExpanded;

	public CallTreeRow(CallTreeNode node, int depth, long rootTotal, double sampleIntervalMs)
	{
		Node = node;
		Depth = depth;
		TotalMs = node.Total * sampleIntervalMs;
		SelfMs = node.Self * sampleIntervalMs;
		TotalFraction = ReportFormat.Fraction(node.Total, rootTotal);
		SelfFraction = ReportFormat.Fraction(node.Self, rootTotal);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public CallTreeNode Node { get; }
	public int Depth { get; }
	public string Name => Node.Name;
	public string Module => Node.Module;
	public double TotalMs { get; }
	public double SelfMs { get; }
	public double TotalFraction { get; }
	public double SelfFraction { get; }
	public bool HasChildren => Node.Children.Count > 0;
	public Thickness Indent => new(Depth * IndentPerLevel, 0, 0, 0);

	public bool IsExpanded
	{
		get => isExpanded;
		set
		{
			if (isExpanded == value)
				return;
			isExpanded = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
		}
	}

	/// <summary>Disclosure triangle; blank for a leaf so names stay aligned.</summary>
	public string Glyph => !HasChildren ? "" : isExpanded ? "▾" : "▸";
}
