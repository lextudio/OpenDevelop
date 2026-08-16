Imports System.Windows.Forms

Partial Class Form1
    Inherits Form

    Private Sub InitializeComponent()
        Me.button1 = New System.Windows.Forms.Button()
        Me.button1.Text = "button1"
        Me.button1.Location = New System.Drawing.Point(12, 20)
        Me.button1.Size = New System.Drawing.Size(90, 30)
        Me.Controls.Add(Me.button1)
    End Sub

    Friend WithEvents button1 As System.Windows.Forms.Button
End Class
