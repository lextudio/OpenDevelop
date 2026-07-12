using System;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
    public static class LanguageDiagnosticFormatter
    {
        public static string Format(string fileName, LanguageDiagnostic diagnostic)
        {
            if (fileName is null)
                throw new ArgumentNullException(nameof(fileName));
            if (diagnostic is null)
                throw new ArgumentNullException(nameof(diagnostic));

            return string.Format(
                "{0}({1},{2}): {3} {4}: {5}",
                fileName,
                diagnostic.Span.Start.Line,
                diagnostic.Span.Start.Column,
                GetSeverityText(diagnostic.Severity),
                diagnostic.Id,
                diagnostic.Message);
        }

        static string GetSeverityText(DiagnosticSeverity severity)
        {
            return severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                DiagnosticSeverity.Info => "info",
                DiagnosticSeverity.Hidden => "hidden",
                _ => "info"
            };
        }
    }
}
