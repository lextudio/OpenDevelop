using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.AddIn.Options;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.Search;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;
using SemanticLanguageService = ICSharpCode.SharpDevelop.LanguageServices.ILanguageService;
using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// OpenLens-style "N references | M implementations" (and, once other AddIns register their own
	/// <see cref="IOpenLensProvider"/>, whatever else they contribute) annotation reserved above each
	/// declaration line.
	///
	/// This is the OpenLens host (doc/technotes/codelens.md §8/§12/§14): it owns anchor discovery
	/// orchestration, viewport-scoped lazy resolution, caching and rendering, but knows nothing about
	/// *how* a lens's value is computed - that is entirely delegated to whatever
	/// <see cref="IOpenLensAnchorProvider"/>/<see cref="IOpenLensProvider"/> instances are registered
	/// in the shared <see cref="OpenLensProviderRegistry"/> (CSharpBinding/VBBinding register the
	/// built-in references/implementations pair via <c>LanguageOpenLensAnchorProvider</c>/
	/// <c>LanguageOpenLensProvider</c>; other AddIns can add test-status/Git/coverage lenses the same
	/// way without this class changing).
	///
	/// Rendering is the production mechanism doc §14.2 calls for: this class is an
	/// <see cref="IVisualLineBlockAdornmentGenerator"/> registered on
	/// <see cref="TextView.BlockAdornmentGenerators"/>, which reserves real layout space above a
	/// declaration's visual line (folded into <see cref="VisualLine.Height"/>, and from there into
	/// AvalonEdit's height tree - the same mechanism that already makes word-wrapped lines scroll and
	/// hit-test correctly) rather than the earlier <see cref="VisualLineElementGenerator"/> +
	/// zero-width <see cref="InlineObjectElement"/> prototype (doc §14.1/§14.3), which could only fake
	/// reserved space by exploiting inline-object baseline math and never correctly participated in
	/// scrolling, hit testing, or word wrap.
	///
	/// Two stages (doc §12.1):
	/// <list type="bullet">
	/// <item><b>Anchor discovery</b> (cheap, whole document): every registered anchor provider that
	/// <see cref="IOpenLensAnchorProvider.CanHandle"/>s this file is asked for anchors, debounced
	/// after each document change. Every registered <see cref="IOpenLensProvider"/> is then asked to
	/// <see cref="IOpenLensProvider.ProvideAsync"/> cheap/placeholder items for those anchors.</item>
	/// <item><b>Resolution</b> (expensive, per-item): <see cref="IOpenLensProvider.ResolveAsync"/>
	/// runs only for items whose anchor sits within the visible viewport plus a small prefetch margin
	/// (doc §12.2) - not eagerly for the whole document - and results are cached by
	/// (AnchorId, LensId) so an anchor untouched by a subsequent edit keeps its resolved value rather
	/// than re-querying.</item>
	/// </list>
	/// </summary>
	sealed class OpenLensRenderer : IVisualLineBlockAdornmentGenerator, IDisposable
	{
		// doc §21 acceptance criteria: a file with an unbounded number of declarations must not fire
		// an unbounded number of resolutions, even lazily. Anchors beyond this count simply don't get
		// discovered - logged once per recompute rather than silently dropped.
		const int MaxAnchors = 300;

		// doc §12.2: resolve visible lenses plus a small prefetch margin, not the whole document.
		// Expressed in characters rather than lines for simplicity - generous enough to cover a
		// couple of screens' worth of prefetch either side without resolving a huge file wholesale.
		const int PrefetchMargin = 4000;

		// doc §12.3: a bounded concurrent-resolution queue ("maximum 2 expensive language
		// resolutions"), not an unbounded Task.WhenAll over every item.
		readonly SemaphoreSlim resolutionThrottle = new(2, 2);

		readonly TextView textView;
		readonly TextDocument document;
		readonly string fileName;
		readonly DocumentId documentId;
		readonly OpenLensProviderRegistry registry;
		readonly List<(int Offset, int Length)> pendingEdits = new();

		// Cache of resolved items, keyed by (AnchorId, LensId) - survives across discovery passes so
		// an anchor untouched by a subsequent edit keeps its resolved value instead of re-querying.
		readonly Dictionary<(string AnchorId, string LensId), OpenLensItem> resolvedItems = new();
		// Set of items currently mid-resolution, keyed by (AnchorId, LensId) - mutated from the
		// render/measure pass (ResolveVisibleAnchors) and from ResolveAsync's finally on the
		// continuation thread, so it must tolerate concurrent access (the plain HashSet raced:
		// AddIfNotPresent threw IndexOutOfRangeException on resize while the async continuation
		// was removing).
		readonly ConcurrentDictionary<(string AnchorId, string LensId), byte> resolving = new();

		CancellationTokenSource refreshCancellation = new();
		long documentVersion;
		IReadOnlyList<OpenLensAnchor> anchors = Array.Empty<OpenLensAnchor>();
		Dictionary<string, int> offsetByAnchorId = new();
		// Keyed by 1-based document line number - GetBlockAdornments is asked per DocumentLine, not
		// per character offset, so this is the lookup the rendering side actually needs.
		Dictionary<int, OpenLensAnchor> anchorByLineNumber = new();
		ILookup<string, OpenLensItem> itemsByAnchorId = Array.Empty<OpenLensItem>().ToLookup(i => i.AnchorId);
		Dictionary<string, IOpenLensProvider> providersById = new();

		// doc §21 "Suggested telemetry or debug counters" - local debug instrumentation only (this
		// project has no user-consented telemetry policy), logged via LoggingService.Debug so the
		// acceptance criteria ("must not issue 50 reference searches immediately", "only visible
		// lenses should begin expensive resolution", etc.) can actually be checked against real
		// numbers instead of assumed from reading the code.
		int anchorsDiscoveredCount;
		int resolutionsStartedCount;
		int resolutionsCompletedCount;
		int resolutionsCancelledCount;
		int cacheHitCount;

		OpenLensRenderer(TextDocument document, TextView textView, string fileName, OpenLensProviderRegistry registry)
		{
			this.document = document;
			this.textView = textView;
			this.fileName = fileName;
			this.documentId = new DocumentId(fileName);
			this.registry = registry;
			document.Changed += DocumentChanged;
			textView.BlockAdornmentGenerators.Add(this);
			textView.VisualLinesChanged += VisualLinesChanged;
			registry.RefreshRequested += OnRefreshRequested;
			ScheduleAnchorRefresh();
		}

		/// <summary>
		/// A provider-initiated refresh (doc §13) - e.g. a coverage run finishing, a Git HEAD
		/// change, a test run completing - none of which are document edits, so nothing else would
		/// otherwise invalidate this document's cached resolved items for that provider.
		/// <see cref="OpenLensRefreshEventArgs.DocumentId"/> null means "every open document";
		/// non-null is checked against this renderer's own <see cref="documentId"/>.
		/// <see cref="OpenLensRefreshEventArgs.AnchorIds"/> null means "every anchor for this
		/// provider" rather than "no anchors" (see its doc comment).
		///
		/// Drops the matching cache entries and re-runs full discovery rather than just re-resolving
		/// them in place: a provider that computes its value directly in
		/// <see cref="IOpenLensProvider.ProvideAsync"/> (e.g. the coverage lens, which is cheap
		/// enough not to need <see cref="IOpenLensProvider.ResolveAsync"/> at all) would never
		/// actually recompute if this only invalidated the cache and waited for the visible-viewport
		/// resolution pass, since that pass calls <c>ResolveAsync</c>, not <c>ProvideAsync</c>.
		/// </summary>
		void OnRefreshRequested(object sender, OpenLensRefreshEventArgs e)
		{
			if (e.DocumentId != null && !e.DocumentId.Equals(documentId))
				return;

			var keysToInvalidate = resolvedItems
				.Where(pair => pair.Value.ProviderId == e.ProviderId && (e.AnchorIds == null || e.AnchorIds.Contains(pair.Key.AnchorId)))
				.Select(pair => pair.Key)
				.ToArray();
			foreach (var key in keysToInvalidate)
				resolvedItems.Remove(key);

			ScheduleAnchorRefresh();
		}

		public static OpenLensRenderer Create(TextDocument document, TextView textView, string fileName)
		{
			if (!CodeEditorOptions.Instance.EnableOpenLens)
				return null;
			// No real language-service document can ever back a file outside any open project (e.g.
			// ILSpy's decompiled "ilspy://..." documents, ILSpyParser.cs/ILSpyDisplayBinding.cs's own
			// synthetic scheme - or any other standalone file opened outside a project) - anchor
			// discovery would just query a language service for a document it never registered.
			// Today that happens to no-op harmlessly (CSharpVBLanguageService.GetOrLoadDocumentAsync
			// returns null for an unregistered DocumentId, so GetDocumentOutlineAsync returns an empty
			// list and no anchors/lenses ever render) rather than throw, but that's incidental, not a
			// guarantee every current or future IOpenLensAnchorProvider honors - skip constructing the
			// renderer at all rather than relying on every provider's own null-handling.
			if (SD.ProjectService.FindProjectContainingFile(FileName.Create(fileName)) == null)
				return null;
			var registry = SD.GetService<OpenLensProviderRegistry>();
			if (registry == null)
				return null;
			return new OpenLensRenderer(document, textView, fileName, registry);
		}

		OpenLensDocumentContext CreateContext()
		{
			// Captures `document` by reference, not by value: the offset conversion always runs
			// against the current buffer at call time, never a stale snapshot from when the context
			// was built (discovery and resolution can straddle further edits).
			return new OpenLensDocumentContext(documentId, fileName, documentVersion,
				pos => document.GetOffset(pos.Line, pos.Column));
		}

		void DocumentChanged(object sender, DocumentChangeEventArgs e)
		{
			pendingEdits.Add((e.Offset, Math.Max(e.InsertionLength, e.RemovalLength)));
			documentVersion++;
			ScheduleAnchorRefresh();
		}

		void VisualLinesChanged(object sender, EventArgs e) => ResolveVisibleAnchors();

		void ScheduleAnchorRefresh()
		{
			refreshCancellation.Cancel();
			refreshCancellation.Dispose();
			refreshCancellation = new CancellationTokenSource();
			var cancellationToken = refreshCancellation.Token;
			_ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => {
				try {
					await Task.Delay(500, cancellationToken);
					// Snapshot now rather than clearing pendingEdits up front - if this attempt gets
					// cancelled by a newer edit before completing, none of these edits should be lost,
					// so only the edits actually accounted for by a *successful* discovery are removed
					// (below), not the whole list.
					var editsSinceLastDiscovery = pendingEdits.ToArray();
					await DiscoverAsync(editsSinceLastDiscovery, cancellationToken);
					if (cancellationToken.IsCancellationRequested)
						return;
					pendingEdits.RemoveRange(0, Math.Min(editsSinceLastDiscovery.Length, pendingEdits.Count));
					// A plain repaint isn't enough - reserving/un-reserving block-adornment space
					// requires visual lines to be rebuilt, so this needs a full line regeneration.
					textView.Redraw();
					ResolveVisibleAnchors();
				}
				catch (OperationCanceledException) { }
				catch (Exception ex) { LoggingService.Warn("OpenLens discovery failed for '" + fileName + "'. " + ex.Message); }
			}));
		}

		/// <summary>
		/// Discovery (doc §12.1 Stage 1/2): every registered anchor provider contributes anchors,
		/// every registered provider then contributes (mostly unresolved) items for those anchors.
		/// A resolved item is kept from <see cref="resolvedItems"/> instead of the provider's fresh
		/// placeholder, unless a pending edit touches the anchor's span (approximated from the
		/// anchor's own <see cref="OpenLensAnchor.Range"/> - the shared anchor model carries only the
		/// declaration's nav span, not its full body extent, so this is a coarser touch check than
		/// the single-feature prototype this replaced; a body edit elsewhere in a large declaration
		/// may not invalidate its cached counts until the next full edit-driven refresh naturally
		/// would anyway).
		/// </summary>
		async Task DiscoverAsync(IReadOnlyList<(int Offset, int Length)> editsSinceLastDiscovery, CancellationToken cancellationToken)
		{
			var context = CreateContext();
			var registeredLanguage = SD.GetService<LanguageServiceRegistry>();
			if (registeredLanguage != null && registeredLanguage.TryGetService(fileName, out var languageService))
				await languageService.UpsertDocumentAsync(documentId, document.Text, cancellationToken);

			var anchorProviders = registry.GetAnchorProviders(context);
			var discovered = new List<OpenLensAnchor>();
			foreach (var anchorProvider in anchorProviders) {
				cancellationToken.ThrowIfCancellationRequested();
				try {
					var found = await anchorProvider.GetAnchorsAsync(context, requestedRange: null, cancellationToken).ConfigureAwait(true);
					discovered.AddRange(found);
				} catch (OperationCanceledException) {
					throw;
				} catch (Exception ex) {
					// doc §22 "one failing provider does not hide other providers" - an anchor
					// provider that throws loses only its own anchors for this pass, not everyone
					// else's.
					LoggingService.Warn("OpenLens: anchor provider '" + anchorProvider.Id + "' failed for '" + fileName + "'. " + ex.Message);
				}
			}

			if (discovered.Count > MaxAnchors) {
				LoggingService.Warn(
					"OpenLens: '" + fileName + "' has " + discovered.Count + " anchors, only keeping the first " + MaxAnchors + ".");
				discovered = discovered.Take(MaxAnchors).ToList();
			}

			var newOffsetByAnchorId = new Dictionary<string, int>();
			var newAnchorByLineNumber = new Dictionary<int, OpenLensAnchor>();
			foreach (var anchor in discovered) {
				try {
					newOffsetByAnchorId[anchor.AnchorId] = document.GetOffset(anchor.Range.Span.Start.Line, anchor.Range.Span.Start.Column);
					// One anchor per line is the only case the renderer supports - a second anchor
					// whose nav span starts on the same line (e.g. an expression-bodied member sharing
					// its declaration's line) simply doesn't get its own row; last one wins.
					newAnchorByLineNumber[anchor.Range.Span.Start.Line] = anchor;
				} catch (ArgumentOutOfRangeException) {
					// Stale outline data referring to a position past the current document end.
				}
			}
			discovered = discovered.Where(a => newOffsetByAnchorId.ContainsKey(a.AnchorId)).OrderBy(a => newOffsetByAnchorId[a.AnchorId]).ToList();

			foreach (var anchor in discovered) {
				int start = newOffsetByAnchorId[anchor.AnchorId];
				bool touchedByRecentEdit = editsSinceLastDiscovery.Any(edit => edit.Offset <= start + 1 && edit.Offset + edit.Length >= start);
				if (touchedByRecentEdit) {
					foreach (var key in resolvedItems.Keys.Where(k => k.AnchorId == anchor.AnchorId).ToArray())
						resolvedItems.Remove(key);
				}
			}

			// doc §16 "Provider-specific options are contributed by their AddIns" - a disabled
			// provider's items are dropped before composition, same as a disabled language Binding
			// already removes its anchors via LanguageServiceRegistry.
			var providers = registry.GetProviders(context).Where(p => CodeEditorOptions.Instance.IsOpenLensProviderEnabled(p.Id)).ToArray();
			providersById = providers.ToDictionary(p => p.Id);

			var items = new List<OpenLensItem>();
			foreach (var provider in providers) {
				cancellationToken.ThrowIfCancellationRequested();
				IReadOnlyList<OpenLensItem> provided;
				try {
					provided = await provider.ProvideAsync(context, discovered, cancellationToken).ConfigureAwait(true);
				} catch (OperationCanceledException) {
					throw;
				} catch (Exception ex) {
					// doc §22 "one failing provider does not hide other providers".
					LoggingService.Warn("OpenLens: provider '" + provider.Id + "' failed for '" + fileName + "'. " + ex.Message);
					continue;
				}
				foreach (var item in provided) {
					if (resolvedItems.TryGetValue((item.AnchorId, item.LensId), out var cached)) {
						items.Add(cached);
						cacheHitCount++;
					} else {
						items.Add(item);
					}
				}
			}

			anchors = discovered;
			offsetByAnchorId = newOffsetByAnchorId;
			anchorByLineNumber = newAnchorByLineNumber;
			itemsByAnchorId = items.ToLookup(i => i.AnchorId);
			anchorsDiscoveredCount = discovered.Count;
			LoggingService.Debug(
				"OpenLens: '" + fileName + "' discovered " + discovered.Count + " anchors ("
				+ "resolved=" + resolvedItems.Count + ", cacheHits=" + cacheHitCount
				+ ", resolutionsStarted=" + resolutionsStartedCount + ", completed=" + resolutionsCompletedCount
				+ ", cancelled=" + resolutionsCancelledCount + ").");
		}

		/// <summary>
		/// Resolution (doc §12.1 Stage 3 / §12.2): only items whose anchor sits within the visible
		/// viewport plus <see cref="PrefetchMargin"/> get resolved, not the whole document up front -
		/// scrolling past an unresolved anchor triggers resolution for it via
		/// <see cref="VisualLinesChanged"/>. Bounded by <see cref="resolutionThrottle"/> (doc §12.3)
		/// rather than an unbounded Task.WhenAll, and guarded by <see cref="resolving"/> so a rapid
		/// scroll doesn't queue the same item twice.
		/// </summary>
		void ResolveVisibleAnchors()
		{
			if (!textView.VisualLinesValid || textView.VisualLines.Count == 0)
				return;

			int viewStart = textView.VisualLines[0].FirstDocumentLine.Offset - PrefetchMargin;
			int viewEnd = textView.VisualLines[textView.VisualLines.Count - 1].LastDocumentLine.EndOffset + PrefetchMargin;

			var visibleAnchorIds = new HashSet<string>(
				anchors.Where(a => offsetByAnchorId.TryGetValue(a.AnchorId, out var o) && o >= viewStart && o <= viewEnd)
					.Select(a => a.AnchorId));

			var toResolve = itemsByAnchorId
				.Where(g => visibleAnchorIds.Contains(g.Key))
				.SelectMany(g => g)
				.Where(i => !i.IsResolved && providersById.ContainsKey(i.ProviderId) && resolving.TryAdd((i.AnchorId, i.LensId), 0))
				.ToArray();

			if (toResolve.Length > 0) {
				resolutionsStartedCount += toResolve.Length;
				LoggingService.Debug("OpenLens: '" + fileName + "' resolving " + toResolve.Length + " newly-visible item(s).");
			}
			foreach (var item in toResolve)
				_ = ResolveAsync(item);
		}

		async Task ResolveAsync(OpenLensItem item)
		{
			var cancellationToken = refreshCancellation.Token;
			await resolutionThrottle.WaitAsync(cancellationToken).ConfigureAwait(true);
			try {
				if (cancellationToken.IsCancellationRequested) {
					resolutionsCancelledCount++;
					return;
				}

				if (!providersById.TryGetValue(item.ProviderId, out var provider))
					return;

				var context = CreateContext();
				var resolved = await provider.ResolveAsync(context, item, cancellationToken).ConfigureAwait(true);

				if (cancellationToken.IsCancellationRequested) {
					resolutionsCancelledCount++;
					return;
				}

				resolvedItems[(resolved.AnchorId, resolved.LensId)] = resolved;
				itemsByAnchorId = itemsByAnchorId.SelectMany(g => g)
					.Select(i => i.AnchorId == resolved.AnchorId && i.LensId == resolved.LensId ? resolved : i)
					.ToLookup(i => i.AnchorId);
				resolutionsCompletedCount++;
				// The block adornment's visual was already created and arranged for the previous
				// (placeholder) item set - a plain repaint wouldn't touch it, since it's a real WPF
				// visual, not something drawn via DrawingContext each frame. Only a full line
				// regeneration re-invokes GetBlockAdornments to pick up the new item. Reserved height
				// is fixed regardless of resolution state (see DesiredHeight below), so this is a
				// relayout of content, not a resize.
				textView.Redraw();
			}
			catch (OperationCanceledException) {
				resolutionsCancelledCount++;
			}
			catch (Exception ex) { LoggingService.Warn("OpenLens resolution failed for '" + fileName + "'. " + ex.Message); }
			finally {
				resolving.TryRemove((item.AnchorId, item.LensId), out _);
				resolutionThrottle.Release();
			}
		}

		/// <summary>
		/// <see cref="IVisualLineBlockAdornmentGenerator"/> entry point - called once per visual-line
		/// construction (<c>TextView.BuildVisualLine</c>) for whichever document line starts that
		/// visual line. Returns at most one adornment: composing every lens item for this anchor into
		/// a single row is this class's own responsibility (doc §14.4), not something the host
		/// (TextView) needs to know about.
		/// </summary>
		public IReadOnlyList<IVisualLineBlockAdornment> GetBlockAdornments(TextView view, DocumentLine documentLine)
		{
			if (!anchorByLineNumber.TryGetValue(documentLine.LineNumber, out var anchor))
				return null;
			var items = itemsByAnchorId[anchor.AnchorId].OrderBy(i => i.Order).ToArray();
			if (items.Length == 0)
				return null;
			// Align the row with the code line's own indentation (the first non-whitespace column)
			// like Visual Studio's CodeLens rows - the anchor's range spans the declaration's
			// *name* token, which sits further right than the line's indent.
			int alignToColumn = -1;
			for (int i = documentLine.Offset; i < documentLine.EndOffset; i++) {
				char ch = document.GetCharAt(i);
				if (ch != ' ' && ch != '\t') {
					alignToColumn = i - documentLine.Offset;
					break;
				}
			}
			return new[] { (IVisualLineBlockAdornment)new OpenLensBlockAdornment(this, anchor.AnchorId, items, alignToColumn) };
		}

		/// <summary>
		/// One anchor's composed row ("N references | M implementations"). A fresh instance is
		/// produced by every <see cref="GetBlockAdornments"/> call, but <see cref="Key"/> (the stable
		/// AnchorId) lets a future host implementation recognize "the same" adornment across
		/// recomputation passes if it ever needs to (e.g. to preserve hover/focus state) - this
		/// renderer doesn't need that today.
		/// </summary>
		sealed class OpenLensBlockAdornment : IVisualLineBlockAdornment
		{
			readonly OpenLensRenderer owner;
			readonly IReadOnlyList<OpenLensItem> items;

			public OpenLensBlockAdornment(OpenLensRenderer owner, string anchorId, IReadOnlyList<OpenLensItem> items, int alignToColumn)
			{
				this.owner = owner;
				Key = anchorId;
				this.items = items;
				AlignToColumn = alignToColumn;
			}

			public object Key { get; }

			public int AlignToColumn { get; }

			// A lens row reads comfortably a bit shorter than a full text line - unlike the retired
			// inline-element prototype, this number no longer needs to relate to
			// TextView.DefaultBaseline at all: the block-adornment layer reserves exactly this much
			// space above the line regardless of font metrics (doc §14.2 point 2).
			public double DesiredHeight => owner.textView.DefaultLineHeight * 0.9;

			public UIElement CreateVisual() => owner.CreateElement(items);
		}

		UIElement CreateElement(IReadOnlyList<OpenLensItem> items)
		{
			var panel = new StackPanel {
				Orientation = Orientation.Horizontal,
				VerticalAlignment = VerticalAlignment.Center,
			};

			for (int i = 0; i < items.Count; i++) {
				if (i > 0) {
					panel.Children.Add(new TextBlock {
						Text = " | ",
						FontSize = ((double)textView.GetValue(TextBlock.FontSizeProperty)) * 0.85,
						Foreground = Brushes.Gray,
					});
				}

				var item = items[i];
				// A provider that knows an icon for its item (e.g. the test lens's pass/fail status)
				// renders icon-only, with the title as tooltip; others keep the plain title text.
				if (item.Presentation.IconKey != null) {
					var icon = LoadIcon(item.Presentation.IconKey);
					if (icon != null) {
						icon.ToolTip = item.Presentation.Title;
						if (item.Command != null) {
							icon.Cursor = Cursors.Hand;
							var command = item.Command;
							icon.MouseLeftButtonDown += (sender, e) => {
								e.Handled = true;
								ExecuteCommand(command, panel);
							};
						}
						panel.Children.Add(icon);
						continue;
					}
				}

				// One TextBlock per item (not one label + Runs) so each item's title is readable as
				// TextBlock.Text - the visual-tree walker (and assistive tech) sees "0 references"
				// instead of an empty text with a drawing surface of inlines.
				var block = new TextBlock {
					Text = item.Presentation.Title,
					FontSize = ((double)textView.GetValue(TextBlock.FontSizeProperty)) * 0.85,
					Foreground = Brushes.Gray,
					VerticalAlignment = VerticalAlignment.Center,
				};
				if (item.Command != null) {
					block.Cursor = Cursors.Hand;
					var command = item.Command;
					// Run (an inline text element, not a UIElement) can't be a Popup.PlacementTarget
					// - anchor the results popup to the whole row's panel instead.
					block.MouseLeftButtonDown += (sender, e) => {
						e.Handled = true;
						ExecuteCommand(command, panel);
					};
				}
				panel.Children.Add(block);
			}

			return panel;
		}

		static Image LoadIcon(string iconKey)
		{
			try {
				var icon = new Image {
					Source = PresentationResourceService.GetImageSource(iconKey),
					Width = 12,
					Height = 12,
					VerticalAlignment = VerticalAlignment.Center,
				};
				if (icon.Source == null)
					return null;
				return icon;
			} catch (Exception ex) {
				LoggingService.Warn("OpenLens: couldn't load icon '" + iconKey + "'. " + ex.Message);
				return null;
			}
		}

		/// <summary>
		/// Executes a resolved item's <see cref="OpenLensCommand"/>. Only the built-in command ids
		/// from <c>LanguageOpenLensProvider</c> plus the two generic escape hatches are wired up here
		/// - a third-party provider's own command id is simply logged and ignored, matching how an
		/// unrecognized menu command id elsewhere in this codebase is a no-op rather than a crash.
		/// Dispatching arbitrary provider command ids through a general command-execution service is
		/// Phase 3+ scope (doc §20), not required for the lenses this host renders today.
		/// </summary>
		void ExecuteCommand(OpenLensCommand command, UIElement placementTarget)
		{
			// "OpenLens.RunAction" is a generic escape hatch for providers outside Base/AvalonEdit.AddIn
			// (e.g. CodeCoverageOpenLensProvider) that need to invoke their own AddIn's behavior -
			// AvalonEdit.AddIn can't reference those AddIns directly (they already depend on it), so
			// the provider supplies the click behavior itself as a plain delegate instead of a new
			// command id this class would need to know about.
			if (command.Argument is Action action) {
				action();
				return;
			}

			// "OpenLens.ShowMenu" is the same escape hatch for a provider that wants its click to
			// offer several choices (e.g. the test lens's Run/Debug menu): the provider supplies the
			// item actions, this host supplies the anchored popup (it has the placement target).
			if (command.Argument is OpenLensMenu menu) {
				new OpenLensMenuPopup(placementTarget, menu).IsOpen = true;
				return;
			}

			if (command.Argument is not OpenLensAnchor anchor) {
				LoggingService.Warn("OpenLens: command '" + command.CommandId + "' has no anchor argument.");
				return;
			}
			switch (command.CommandId) {
				case "OpenLens.ShowReferences":
					_ = ShowReferencesAsync(anchor, placementTarget);
					break;
				case "OpenLens.ShowImplementations":
					_ = ShowImplementationsAsync(anchor, placementTarget);
					break;
				default:
					LoggingService.Warn("OpenLens: unrecognized command id '" + command.CommandId + "'.");
					break;
			}
		}

		async Task ShowReferencesAsync(OpenLensAnchor anchor, UIElement placementTarget)
		{
			try {
				var languageService = GetLanguageService();
				if (languageService == null)
					return;
				await languageService.UpsertDocumentAsync(documentId, document.Text, CancellationToken.None);
				int offset = document.GetOffset(anchor.Range.Span.Start.Line, anchor.Range.Span.Start.Column);
				var result = await languageService.FindReferencesAsync(documentId, offset, CancellationToken.None);
				if (result == null)
					return;

				var matches = result.References.Where(t => t.Span != null).Select(ToSearchResultMatch).Where(m => m != null).ToArray();
				string title = StringParser.Parse("${res:SharpDevelop.Refactoring.FindReferences}") + " '" + result.Subject + "'";
				// doc §15.2: a lightweight popup anchored to the lens is the primary interaction;
				// "Show in Search Results" promotes the exact same match list into the existing pad
				// rather than this popup owning a second copy of the result data.
				var popup = new OpenLensResultsPopup(placementTarget, title, matches, () => {
					SearchResultsHost.Current.ShowSearchResults(title, matches);
					SearchResultsHost.Current.BringToFront();
				});
				popup.IsOpen = true;
			} catch (Exception ex) {
				LoggingService.Warn("OpenLens: find references failed. " + ex.Message);
			}
		}

		async Task ShowImplementationsAsync(OpenLensAnchor anchor, UIElement placementTarget)
		{
			try {
				var languageService = GetLanguageService();
				if (languageService == null)
					return;
				await languageService.UpsertDocumentAsync(documentId, document.Text, CancellationToken.None);
				int offset = document.GetOffset(anchor.Range.Span.Start.Line, anchor.Range.Span.Start.Column);
				var result = await languageService.GetDerivedSymbolsAsync(documentId, offset, CancellationToken.None);
				if (result == null)
					return;

				var matches = FlattenNodes(result.Nodes)
					.Where(n => n.Target.Span != null)
					.Select(n => ToSearchResultMatch(n.Target))
					.Where(m => m != null)
					.ToArray();
				string title = "Implementations of '" + result.Subject + "'";
				var popup = new OpenLensResultsPopup(placementTarget, title, matches, () => {
					SearchResultsHost.Current.ShowSearchResults(title, matches);
					SearchResultsHost.Current.BringToFront();
				});
				popup.IsOpen = true;
			} catch (Exception ex) {
				LoggingService.Warn("OpenLens: find implementations failed. " + ex.Message);
			}
		}

		SemanticLanguageService GetLanguageService()
		{
			var registry = SD.GetService<LanguageServiceRegistry>();
			return registry != null && registry.TryGetService(fileName, out var service) ? service : null;
		}

		static IEnumerable<SymbolNavigationNode> FlattenNodes(IReadOnlyList<SymbolNavigationNode> nodes)
		{
			foreach (var node in nodes) {
				yield return node;
				foreach (var child in FlattenNodes(node.Children))
					yield return child;
			}
		}

		SearchResultMatch ToSearchResultMatch(NavigationTarget target)
		{
			var span = target.Span.Value;
			string text;
			try {
				text = string.Equals(target.FileName, fileName, StringComparison.OrdinalIgnoreCase) ? document.Text : File.ReadAllText(target.FileName);
			} catch (IOException) {
				return null;
			} catch (UnauthorizedAccessException) {
				return null;
			}
			int startOffset = GetOffset(text, span.Start.Line, span.Start.Column);
			int endOffset = GetOffset(text, span.End.Line, span.End.Column);
			return new SearchResultMatch(
				FileName.Create(target.FileName),
				new TextLocation(span.Start.Line, span.Start.Column),
				new TextLocation(span.End.Line, span.End.Column),
				startOffset, Math.Max(0, endOffset - startOffset),
				displayText: null, defaultTextColor: null);
		}

		static int GetOffset(string text, int requestedLine, int requestedColumn)
		{
			int line = 1;
			int offset = 0;
			while (offset < text.Length && line < requestedLine) {
				if (text[offset++] == '\n')
					line++;
			}
			return Math.Min(text.Length, offset + Math.Max(0, requestedColumn - 1));
		}

		public void Dispose()
		{
			document.Changed -= DocumentChanged;
			textView.VisualLinesChanged -= VisualLinesChanged;
			textView.BlockAdornmentGenerators.Remove(this);
			registry.RefreshRequested -= OnRefreshRequested;
			refreshCancellation.Cancel();
			refreshCancellation.Dispose();
			resolutionThrottle.Dispose();
		}
	}
}
