Imports ICSharpCode.Core
Imports ICSharpCode.SharpDevelop
Imports ICSharpCode.SharpDevelop.LanguageServices.OpenLens

Public NotInheritable Class RegisterVisualBasicOpenLensProvidersCommand
    Inherits AbstractCommand
    Implements IDisposable

    Private anchorRegistration As IDisposable
    Private providerRegistration As IDisposable

    Public Overrides Sub Run()
        Dim registry = SD.GetRequiredService(Of OpenLensProviderRegistry)()
        anchorRegistration = registry.RegisterAnchorProvider(New LanguageOpenLensAnchorProvider("VisualBasic", ".vb"))
        providerRegistration = registry.RegisterProvider(New LanguageOpenLensProvider("VisualBasic", ".vb"))
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        anchorRegistration?.Dispose()
        providerRegistration?.Dispose()
    End Sub
End Class
