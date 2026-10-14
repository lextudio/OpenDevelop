#nullable enable
using System;
using System.IO;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;

namespace ICSharpCode.MewUIDesigner;

public static partial class MewUICodeBehindHandlers
{
	/// <summary>Ensures the handler exists in <c>mxamlPath + ".cs"</c> and opens it at the method.
	/// An open editor's buffer is authoritative (the edit lands on its undo stack); otherwise the
	/// file is rewritten keeping its UTF-8 byte order mark. False without a code-behind file or a
	/// known signature.</summary>
	public static bool EnsureAndNavigate(string mxamlPath, string? mxamlText, string controlType, string eventName, string handlerName)
	{
		var codePath = mxamlPath + ".cs";
		if (!File.Exists(codePath) || ParameterList(controlType, eventName) is not { } parameters)
			return false;
		try {
			var fileName = FileName.Create(codePath);
			var openEditor = SD.FileService.IsOpen(fileName)
				? SD.FileService.GetOpenFile(fileName)?.GetService(typeof(ITextEditor)) as ITextEditor
				: null;
			var bytes = openEditor == null ? File.ReadAllBytes(codePath) : Array.Empty<byte>();
			var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
			var source = openEditor?.Document.Text ?? new System.Text.UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
			var updated = AddHandler(source, ClassNameOf(mxamlText), handlerName, parameters, out var line);
			if (line == 0)
				return false;
			if (updated != null) {
				if (openEditor != null)
					openEditor.Document.Text = updated;
				else
					File.WriteAllText(codePath, updated, new System.Text.UTF8Encoding(hasBom));
			}
			var view = SD.FileService.OpenFile(fileName);
			(view?.GetService(typeof(ITextEditor)) as ITextEditor)?.JumpTo(line, 1);
			return true;
		} catch (Exception exception) {
			LoggingService.Warn("MewUI designer: could not create event handler " + handlerName + " in " + codePath + ": " + exception.Message);
			return false;
		}
	}
}
