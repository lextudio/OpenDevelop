Imports ICSharpCode.Core
Imports ICSharpCode.SharpDevelop
Imports ICSharpCode.SharpDevelop.LanguageServices
Imports ICSharpCode.SharpDevelop.LanguageServices.Roslyn
Imports ICSharpCode.SharpDevelop.Project
Imports System.Collections.Generic
Imports System.Threading

Public NotInheritable Class RegisterVisualBasicLanguageServiceCommand
    Inherits AbstractCommand
    Implements IDisposable

    Private service As CSharpVBLanguageService
    Private registration As IDisposable

    Public Overrides Sub Run()
        If Environment.GetEnvironmentVariable("OD_ROSLYN_HOST") = "1" Then
            Dim registry = SD.GetRequiredService(Of LanguageServiceRegistry)()
            ' Resolve lazily so add-in registration order does not create a second workspace.
            registration = registry.RegisterExtension(".vb", Function(fileName) registry.GetService(".cs"))
            Return
        End If
        service = New CSharpVBLanguageService(AddressOf GetSnapshots)
        AddHandler SD.ProjectService.SolutionClosed, AddressOf OnSolutionClosed
        registration = SD.GetRequiredService(Of LanguageServiceRegistry)().RegisterExtension(".vb", service)
    End Sub

    Private Shared Function GetSnapshots(fileName As String) As IReadOnlyList(Of LanguageServiceProjectSnapshot)
        Dim project = SD.ProjectService.FindProjectContainingFile(ICSharpCode.Core.FileName.Create(fileName))
        If project Is Nothing Then Return Array.Empty(Of LanguageServiceProjectSnapshot)()
        Return LanguageServiceProjectSnapshotFactory.FromProjectAllTargetFrameworks(project)
    End Function

    Private Async Sub OnSolutionClosed(sender As Object, e As SolutionEventArgs)
        Try
            Await service.CloseSolutionAsync(e.Solution?.Directory.ToString(), CancellationToken.None)
        Catch ex As Exception
            LoggingService.Warn("Unable to close the Visual Basic workspace: " & ex.Message)
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        registration?.Dispose()
        If service IsNot Nothing Then RemoveHandler SD.ProjectService.SolutionClosed, AddressOf OnSolutionClosed
        service?.Dispose()
    End Sub
End Class
