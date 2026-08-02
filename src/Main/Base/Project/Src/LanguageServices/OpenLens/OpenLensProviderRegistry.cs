#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.SharpDevelop.LanguageServices.OpenLens
{
    /// <summary>
    /// Central registry for OpenLens providers and anchor providers (doc/technotes/codelens.md
    /// §8/§18). Registered as an addin-tree <c>&lt;Service&gt;</c>
    /// (<c>ICSharpCode.SharpDevelop.addin</c>), the same way as <see cref="LanguageServiceRegistry"/>,
    /// so any AddIn can reach it via <c>SD.GetRequiredService&lt;OpenLensProviderRegistry&gt;()</c>
    /// without a direct reference to whichever AddIn hosts the OpenLens renderer.
    ///
    /// This is Phase 0 infrastructure only (doc §20): the registry exists and providers can
    /// register/unregister against it, but the renderer in AvalonEdit.AddIn
    /// (<c>OpenLensRenderer.cs</c>) does not consume it yet - it still talks to
    /// <see cref="ILanguageService"/> directly for its two hardcoded indicators, per the Phase 1
    /// prototype status documented there. Wiring the renderer to compose lenses through this
    /// registry, and giving CSharpBinding/VBBinding real
    /// <see cref="IOpenLensProvider"/>/<see cref="IOpenLensAnchorProvider"/> implementations instead
    /// of the renderer's private discovery/resolution logic, is the next step, not part of this one.
    /// </summary>
    public sealed class OpenLensProviderRegistry
    {
        readonly List<IOpenLensProvider> _providers = new();
        readonly List<IOpenLensAnchorProvider> _anchorProviders = new();
        readonly object _gate = new();

        /// <summary>
        /// Raised when a provider's underlying data changed for reasons the OpenLens host can't
        /// infer from document edits alone - a test run finishing, a Git HEAD change, a coverage run
        /// completing (doc §13). A provider that isn't <see cref="OpenLensRefreshEventArgs.ProviderId"/>
        /// should ignore the notification rather than recompute defensively.
        /// </summary>
        public event EventHandler<OpenLensRefreshEventArgs>? RefreshRequested;

        public IReadOnlyList<IOpenLensProvider> GetProviders(OpenLensDocumentContext context)
        {
            if (context is null)
                throw new ArgumentNullException(nameof(context));
            lock (_gate)
                return _providers.Where(p => p.CanHandle(context)).OrderBy(p => p.Order).ToArray();
        }

        public IReadOnlyList<IOpenLensAnchorProvider> GetAnchorProviders(OpenLensDocumentContext context)
        {
            if (context is null)
                throw new ArgumentNullException(nameof(context));
            lock (_gate)
                return _anchorProviders.Where(p => p.CanHandle(context)).ToArray();
        }

        /// <summary>
        /// Registers <paramref name="provider"/> and returns a disposable that unregisters it. The
        /// owning AddIn's Autostart command is expected to hold this and dispose it on unload/disable
        /// (matching <c>RegisterCSharpLanguageServiceCommand</c>'s pattern for
        /// <see cref="LanguageServiceRegistry"/>), so the provider's lenses disappear without
        /// disturbing any other provider or the OpenLens host itself (doc §18).
        /// </summary>
        public IDisposable RegisterProvider(IOpenLensProvider provider)
        {
            if (provider is null)
                throw new ArgumentNullException(nameof(provider));
            lock (_gate)
                _providers.Add(provider);
            return new Registration(() => {
                lock (_gate)
                    _providers.Remove(provider);
            });
        }

        public IDisposable RegisterAnchorProvider(IOpenLensAnchorProvider provider)
        {
            if (provider is null)
                throw new ArgumentNullException(nameof(provider));
            lock (_gate)
                _anchorProviders.Add(provider);
            return new Registration(() => {
                lock (_gate)
                    _anchorProviders.Remove(provider);
            });
        }

        public void RequestRefresh(OpenLensRefreshEventArgs e)
        {
            if (e is null)
                throw new ArgumentNullException(nameof(e));
            RefreshRequested?.Invoke(this, e);
        }

        sealed class Registration : IDisposable
        {
            Action? _unregister;

            public Registration(Action unregister) => _unregister = unregister;

            public void Dispose()
            {
                var action = _unregister;
                _unregister = null;
                action?.Invoke();
            }
        }
    }

    public sealed class OpenLensRefreshEventArgs : EventArgs
    {
        public OpenLensRefreshEventArgs(string providerId, DocumentId? documentId = null, IReadOnlyCollection<string>? anchorIds = null)
        {
            ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
            DocumentId = documentId;
            AnchorIds = anchorIds;
        }

        public string ProviderId { get; }
        public DocumentId? DocumentId { get; }

        /// <summary>Null means "every anchor for this provider/document", not "no anchors".</summary>
        public IReadOnlyCollection<string>? AnchorIds { get; }
    }
}
