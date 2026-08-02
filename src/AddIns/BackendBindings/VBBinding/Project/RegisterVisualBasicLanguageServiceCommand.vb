Imports ICSharpCode.Core
Imports ICSharpCode.SharpDevelop
Imports ICSharpCode.SharpDevelop.LanguageServices
Imports ICSharpCode.SharpDevelop.LanguageServices.Roslyn

Public NotInheritable Class RegisterVisualBasicLanguageServiceCommand
    Inherits AbstractCommand
    Implements IDisposable

    Private service As CSharpVBLanguageService
    Private registration As IDisposable

    Public Overrides Sub Run()
        service = New CSharpVBLanguageService()
        registration = SD.GetRequiredService(Of LanguageServiceRegistry)().RegisterExtension(".vb", service)
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        registration?.Dispose()
        service?.Dispose()
    End Sub
End Class
