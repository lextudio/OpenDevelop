using System;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.Translations;

/// <summary>Attaches the translator as a Design view beside the source of a <c>.po</c> file.</summary>
public sealed class TranslatorDisplayBinding : ISecondaryDisplayBinding
{
	public bool ReattachWhenParserServiceIsReady => false;
	public bool CanAttachTo(IViewContent content) => string.Equals(Path.GetExtension(content?.PrimaryFileName), ".po", StringComparison.OrdinalIgnoreCase);
	public IViewContent[] CreateSecondaryViewContent(IViewContent content) => new IViewContent[] { new TranslatorViewContent(content.PrimaryFile) };
}

/// <summary>The translator view of one <c>.po</c> file: loads it into a <see cref="TranslatorModel"/>, shows
/// <see cref="TranslatorView"/>, and writes the model back (verbatim while nothing was edited).</summary>
public sealed class TranslatorViewContent : AbstractViewContentHandlingLoadErrors
{
	readonly TranslatorModel model = new TranslatorModel();
	readonly TranslatorView view;

	public TranslatorViewContent(OpenedFile file)
	{
		TabPageText = "Translator";
		view = new TranslatorView(model);
		model.Changed += (_, _) => { view.RefreshSummary(); PrimaryFile?.MakeDirty(); };
		UserContent = view;
		Files.Add(file);
	}

	public TranslatorModel Model => model;
	public TranslatorView View => view;
	public override bool IsReadOnly => false;

	protected override void LoadInternal(OpenedFile file, Stream stream)
	{
		using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
		model.Load(reader.ReadToEnd());
	}

	protected override void SaveInternal(OpenedFile file, Stream stream)
	{
		using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
		writer.Write(model.ToText());
		writer.Flush();
		model.MarkSaved();
	}

	/// <summary>Brings the open translator forward (switching its window to the Translator tab) and returns it; null when none is open.</summary>
	public static TranslatorViewContent? Activate()
	{
		var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow;
		if (window != null)
			for (var i = 0; i < window.ViewContents.Count; i++)
				if (window.ViewContents[i] is TranslatorViewContent inActive) { window.SwitchView(i); return inActive; }
		var view = SD.Workbench.ViewContentCollection.OfType<TranslatorViewContent>().FirstOrDefault();
		var viewWindow = view?.WorkbenchWindow;
		if (view == null || viewWindow == null) return null;
		viewWindow.SelectWindow();
		for (var i = 0; i < viewWindow.ViewContents.Count; i++)
			if (ReferenceEquals(viewWindow.ViewContents[i], view)) { viewWindow.SwitchView(i); break; }
		return view;
	}
}
