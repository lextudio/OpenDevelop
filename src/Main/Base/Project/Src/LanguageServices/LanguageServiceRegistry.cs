using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.ILSpy.Util;
using ICSharpCode.SharpDevelop.Workbench;

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
            // The registry is the one place that knows which service owns a file, so it alone relays
            // "the editor closed this file" instead of every service watching the workbench itself.
            MessageBus<DocumentClosedMessageEventArgs>.Subscribers += OnDocumentClosed;
        }

        void OnDocumentClosed(object sender, DocumentClosedMessageEventArgs e) => NotifyDocumentClosed(e.FileName);

        /// <summary>Tells the service registered for <paramref name="fileName"/> that the file was closed, when it
        /// implements <see cref="ILanguageServiceDocumentLifecycle"/>. Never throws: a service failing to
        /// forget a document must not break closing the editor.</summary>
        public void NotifyDocumentClosed(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrEmpty(Path.GetExtension(fileName)))
                return;
            try
            {
                if (TryGetService(fileName, out var service) && service is ILanguageServiceDocumentLifecycle lifecycle)
                    lifecycle.CloseDocumentAsync(fileName).ContinueWith(
                        t => LoggingService.Warn("Closing " + fileName + " in its language service failed: " + t.Exception?.GetBaseException().Message),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Closing " + fileName + " in its language service failed: " + ex.Message);
            }
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

        // One protocol per service instance. Remote services return their existing connection;
        // resolving the protocol must never create a second workspace beside the registered one.
        readonly ConditionalWeakTable<ILanguageService, Protocol.IRoslynLanguageProtocol> _protocolsByService = new();

        /// <summary>
        /// The wire-shaped protocol for a file's language service
        /// (doc/technotes/roslyn-host-process.md Phase 2).
        ///
        /// Callers should prefer this over <see cref="TryGetService"/>: it is the surface that
        /// survives the language service moving out of process, so anything written against it
        /// needs no change when that happens - only what this method hands back does.
        /// </summary>
        public bool TryGetProtocol(string fileNameOrExtension, out Protocol.IRoslynLanguageProtocol protocol)
        {
            if (!TryGetService(fileNameOrExtension, out var service))
            {
                protocol = null!;
                return false;
            }
            protocol = _protocolsByService.GetValue(service, CreateProtocol);
            return true;
        }

        /// <summary>
        /// Returns the protocol of the registered service. Process selection happens when the
        /// language binding creates that service, so old and protocol-based consumers agree on
        /// workspace ownership. Local Roslyn adapters also receive project/lifecycle callbacks.
        /// </summary>
        static Protocol.IRoslynLanguageProtocol CreateProtocol(ILanguageService service)
        {
            if (service is Protocol.RemoteLanguageService remote)
                return remote.Protocol;
            // The registry also contains external LSP implementations (TypeScript, XAML, HTML,
            // ...). A Roslyn host is only for the C#/VB remote service; it must not turn an
            // unrelated language server into a client of that workspace merely because both
            // implement ILanguageService.
            if (service is not Roslyn.CSharpVBLanguageService)
                return new Protocol.InProcessRoslynLanguageProtocol(service);

            var roslyn = (Roslyn.CSharpVBLanguageService)service;
            return new Protocol.InProcessRoslynLanguageProtocol(service,
                projectLoad: roslyn.LoadProjectAsync, solutionClosed: roslyn.CloseSolutionAsync,
                workspaceStatusProvider: roslyn.GetWorkspaceStatus);
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

		/// <summary>Refreshes the project owning a document through its registered language service.</summary>
		public Task RefreshProjectAsync(string fileName, System.Threading.CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(fileName) || !TryGetService(fileName, out var languageService))
				return Task.CompletedTask;
			return languageService.RefreshProjectAsync(new DocumentId(fileName), cancellationToken);
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
