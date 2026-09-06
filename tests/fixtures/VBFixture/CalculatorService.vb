Public Class CalculatorService
    Private ReadOnly _calc As New Calculator()

    Public Function Compute(ByVal x As Integer, ByVal y As Integer) As Integer
        Return _calc.Multiply(x, y)
    End Function
End Class
