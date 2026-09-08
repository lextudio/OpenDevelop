Imports ICSharpCode.Core
Imports ICSharpCode.SharpDevelop
Imports ICSharpCode.SharpDevelop.LanguageServices

Public NotInheritable Class RegisterVisualBasicLanguageServiceCommand
    Inherits AbstractCommand
    Implements IDisposable

    Private registration As IDisposable

    Public Overrides Sub Run()
        Dim registry = SD.GetRequiredService(Of LanguageServiceRegistry)()
        ' Resolve lazily so add-in registration order does not create a second workspace. The C#
        ' binding owns the one remote C#/VB service; no IDE-side Roslyn fallback exists.
        registration = registry.RegisterExtension(".vb", Function(fileName) registry.GetService(".cs"))
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        registration?.Dispose()
    End Sub
End Class
