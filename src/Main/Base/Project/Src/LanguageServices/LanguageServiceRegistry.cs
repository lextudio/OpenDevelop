using System;
using System.Collections.Generic;
using System.IO;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
    public sealed class LanguageServiceRegistry
    {
        readonly Dictionary<string, ILanguageService> _servicesByExtension;
        readonly ILanguageService _fallbackService;

        public LanguageServiceRegistry()
            : this(NoOpLanguageService.Instance)
        {
        }

        public LanguageServiceRegistry(ILanguageService fallbackService)
        {
            _fallbackService = fallbackService ?? throw new ArgumentNullException(nameof(fallbackService));
            _servicesByExtension = new Dictionary<string, ILanguageService>(StringComparer.OrdinalIgnoreCase);
        }

        public ILanguageService FallbackService => _fallbackService;

        public void RegisterExtension(string extension, ILanguageService languageService)
        {
            if (languageService is null)
                throw new ArgumentNullException(nameof(languageService));

            _servicesByExtension[NormalizeExtension(extension)] = languageService;
        }

        public bool TryGetService(string fileNameOrExtension, out ILanguageService languageService)
        {
            var extension = NormalizeExtension(ExtractExtension(fileNameOrExtension));
            return _servicesByExtension.TryGetValue(extension, out languageService!);
        }

        public ILanguageService GetService(string fileNameOrExtension)
        {
            return TryGetService(fileNameOrExtension, out var languageService)
                ? languageService
                : _fallbackService;
        }

        static string ExtractExtension(string fileNameOrExtension)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrExtension))
                throw new ArgumentException("An extension or file name is required.", nameof(fileNameOrExtension));

            if (fileNameOrExtension[0] == '.')
                return fileNameOrExtension;

            return Path.GetExtension(fileNameOrExtension);
        }

        static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                throw new ArgumentException("An extension is required.", nameof(extension));

            return extension[0] == '.'
                ? extension
                : "." + extension;
        }
    }
}
