#nullable enable
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
    /// <summary>
    /// Optional capability of an <see cref="ILanguageService"/> that keeps per-document state: it is told
    /// when the IDE has closed the file, so that state (text, diagnostics, a server-side open document)
    /// does not outlive the editor. <c>LanguageServiceRegistry</c> raises it from the workbench's
    /// document-closed message; a service that keeps nothing per document need not implement it.
    /// </summary>
    public interface ILanguageServiceDocumentLifecycle
    {
        /// <summary>Forgets every document of <paramref name="fileName"/> (whatever its target framework).
        /// Closing a file the service never saw is not an error.</summary>
        Task CloseDocumentAsync(string fileName);
    }
}
