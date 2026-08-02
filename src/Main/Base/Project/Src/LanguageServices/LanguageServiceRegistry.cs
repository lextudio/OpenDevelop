using System;
using System.Collections.Generic;
using System.IO;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
    public sealed class LanguageServiceRegistry
    {
        readonly Dictionary<string, RegistrationEntry> _servicesByExtension;
        readonly ILanguageService _fallbackService;

        public LanguageServiceRegistry()
            : this(NoOpLanguageService.Instance)
        {
        }

        public LanguageServiceRegistry(ILanguageService fallbackService)
        {
            _fallbackService = fallbackService ?? throw new ArgumentNullException(nameof(fallbackService));
            _servicesByExtension = new Dictionary<string, RegistrationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        public ILanguageService FallbackService => _fallbackService;

        public IDisposable RegisterExtension(string extension, ILanguageService languageService)
        {
            if (languageService is null)
                throw new ArgumentNullException(nameof(languageService));

            return RegisterExtension(extension, _ => languageService);
        }

        public IDisposable RegisterExtension(string extension, Func<string, ILanguageService> languageServiceResolver)
        {
            if (languageServiceResolver is null)
                throw new ArgumentNullException(nameof(languageServiceResolver));

            var normalizedExtension = NormalizeExtension(extension);
            var entry = new RegistrationEntry(languageServiceResolver);
            _servicesByExtension[normalizedExtension] = entry;
            return new Registration(this, normalizedExtension, entry);
        }

        public bool TryGetService(string fileNameOrExtension, out ILanguageService languageService)
        {
            var extension = NormalizeExtension(ExtractExtension(fileNameOrExtension));
            if (_servicesByExtension.TryGetValue(extension, out var entry))
            {
                languageService = entry.Resolve(fileNameOrExtension);
                return languageService != null;
            }
            languageService = null!;
            return false;
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

        sealed class Registration : IDisposable
        {
            LanguageServiceRegistry registry;
            readonly string extension;
            readonly RegistrationEntry entry;

            public Registration(LanguageServiceRegistry registry, string extension, RegistrationEntry entry)
            {
                this.registry = registry;
                this.extension = extension;
                this.entry = entry;
            }

            public void Dispose()
            {
                var owner = registry;
                if (owner == null)
                    return;
                registry = null;
                if (owner._servicesByExtension.TryGetValue(extension, out var current) && ReferenceEquals(current, entry))
                    owner._servicesByExtension.Remove(extension);
            }
        }

        sealed class RegistrationEntry
        {
            readonly Func<string, ILanguageService> resolver;

            public RegistrationEntry(Func<string, ILanguageService> resolver) => this.resolver = resolver;

            public ILanguageService Resolve(string fileName) => resolver(fileName);
        }
    }
}
