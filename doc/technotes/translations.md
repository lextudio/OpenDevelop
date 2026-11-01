# Translations addin

`ICSharpCode.Translations` (`src/AddIns/Misc/Translations`) is the gettext workflow the designers share. It is
its own addin: neither the GTK designer nor the WinForms designer owns it, and a language or designer addin only
says which of its files carry translatable text.

## Pieces

| Piece | Where | Status |
|---|---|---|
| `PoCatalog` / `PoEntry`: parse, write and `msgmerge`-style merge of `.pot`/`.po` (context, comments, references, flags, plurals, obsolete entries) | `Project/Src/PoCatalog.cs` | implemented |
| `ITranslatableStringSource` + `TranslatableStringSources`: what an addin contributes (`Handles(path)`, `Extract(text, referencePath)`), registered once by an autostart command | `Project/Src/TranslatableStringSource.cs` | implemented |
| `TranslationProject`: build `po/<project>.pot` from a project's files, merge every `po/*.po`, add a language | `Project/Src/TranslationProject.cs` | implemented |
| Tools > Update Translations, `od.translations.update` (DevFlow) | `Project/Src/TranslationsService.cs`, `TranslationsDevFlowActions.cs` | implemented |
| `TranslatorModel` / `TranslatorRow`: the rows, filters, search and progress of one `.po` text; edits mark it dirty, untouched text is written back verbatim | `Project/Src/TranslatorModel.cs` | implemented |
| Translator view: the Design view of a `.po` or a culture `.resx` (state, name/context, source, translation, fuzzy, comment, notes columns; All / Needs work / Untranslated / Fuzzy / Translated / Obsolete filters; search; progress line) | `Project/Src/TranslatorView.cs`, `TranslatorViewContent.cs` | implemented |
| `PoAnalyzer`: the problems, hover, completion, `#:` references and outline of a `.po` text, as pure functions | `Project/Src/PoAnalyzer.cs` | implemented |
| `PoLanguageService` (`ILanguageService` for ".po") with its registration, completion binding and syntax highlighting | `Project/Src/PoLanguageService.cs`, `PoLanguageServiceRegistration.cs`, `Resources/Po.xshd` | implemented |
| `ITranslationFormat`: how a file maps to entries and back (`PoFormat`, `ResxFormat`) | `Project/Src/TranslationFormats.cs` | implemented |
| `.resx` adapter: `X.resx` is the source, `X.<culture>.resx` the translations, keyed by resource name rather than by msgid | `Project/Src/TranslationFormats.cs` (`ResxFormat`) | implemented for editing a culture file; Update Translations does not generate culture files |
| Compiling `.po` to `.mo` (`msgfmt`) | - | not planned: it is a build step of the project that ships the catalogues, not editor behaviour |

## Rules

- A source returns entries; it never touches `po/`. `TranslationProject` is the only writer.
- A string is identified by (context, msgid). The same string in several files is one entry with several
  `#:` references and merged `#.` comments.
- A translation whose string left the template is kept as obsolete (`#~`) and revived if the string returns, so
  editing a UI never loses a translator's work. An untranslated entry that disappears is dropped.
- Nothing is rewritten when its content would not change (the `.pot` keeps its creation date), so running the
  update on every build leaves source control quiet.
- Only the project's own `po/` folder is written, and `AddLanguage` accepts plain locale codes only.

## Translator view

A `.po` file opens with its source and a **Translator** tab (a secondary display binding, like the designers' Design
tab). The grid lists one row per entry: State, Source (with the context, if any, in the row's data), an editable
Translation, a Fuzzy check box, the translator's own Comment (the `#` lines) and read-only Notes (the source's `#.`
comment and `#:` references). Rules:

- Typing a translation into a fuzzy entry confirms it (the flag goes), as other PO editors do; ticking Fuzzy marks it again.
- Plural and obsolete entries are listed but not edited here (their forms and their fate are the source view's); the
  Obsolete filter lists the kept translations of strings that left the template.
- The filter box and search narrow the list (search matches source, translation, comments, context and references);
  the progress line counts translated, fuzzy and untranslated entries.
- Saving writes the file back verbatim until a row is edited; after that the catalogue is rewritten in gettext's layout.
  Editing the source view and switching back reloads the rows from the file.

## Source-view language service

A `.po` file's text editor gets an in-process language service (the same registry the `.ui` and C# services use), with
Po syntax highlighting. `PoAnalyzer` answers; `PoLanguageService` converts to the editor's contracts.

| Id | Severity | Meaning |
|---|---|---|
| PO1001 | error | msgstr placeholders (`%s`, `%d`, `%1$s`...) do not match the source's. A translation may reorder placeholders only by numbering them; a plural form may leave one out but not add or change one. Checked when the source has placeholders, or when the entry is flagged `c-format`; skipped for `no-c-format`. |
| PO1002 | warning | the markup tags (`<b>`...) differ from the source's |
| PO1003 | warning | msgid and msgstr do not both begin, or both end, with a line break (what `msgfmt -c` rejects) |
| PO1004 | info | the entry is fuzzy, so it is not used until confirmed; its text is not checked until then |
| PO1005 | error | a plural entry has a different number of `msgstr[n]` forms than the header's `nplurals` |
| PO1006 | warning | a translated plural entry but no `Plural-Forms` in the header |
| PO1007 | error | duplicate message definition (same context and msgid) |
| PO1008 | warning | no header entry, no `Content-Type` charset, or a charset other than UTF-8 (the translator view saves UTF-8) |

Obsolete (`#~`) entries and untranslated ones are not reported: the Translator view lists what is still to do. Problems
are reported on the offending line (the msgstr, the flag line for fuzzy, the msgid for duplicates).

Hover on a message shows its source (and plural), context, translation with its state, the extracted note, the `#:` sources
and its placeholders. Completion offers the flags after `#,` (`fuzzy`, `c-format`, `no-c-format`, ...), the source text
inside an open `msgstr "`, and the keywords that may start the line (after `msgctxt` only `msgid`; after `msgid`,
`msgid_plural` or `msgstr`; after `msgid_plural`, `msgstr[0]`/`msgstr[1]`; at a new entry, `msgid` or `msgctxt`). Go to
definition on a `#:` reference opens `file:line`, looked up in the project folder (the parent of the `po` folder) and then
beside the `.po`. The outline lists the header and one node per live message, kind-tagged translated, fuzzy or
untranslated.

The completion binding is a copy of the GTK addin's for `.ui`; if more addins need one, that is the code to move to Base.

### Culture `.resx` files

`Form1.fr.resx` opens with the same Translator tab when `Form1.resx` is next to it (the file name's culture must be a
real culture such as `fr`, `zh-Hans` or `pt-BR`; `Form1.Designer.resx` is not one). The neutral file is the source and is
read again on every load, so a change there shows up. `ResxFormat` maps the two files to the translator's entries:

- Only string resources take part. An entry with a `type` or `mimetype` (a Point, an image, a boolean) and the
  designer's `>>name.Type` bookkeeping are not text a translator touches, and are left exactly as they are.
- An entry is keyed by its resource name (the **Name** column), shows the neutral value as its source and the neutral
  `<comment>` as its note, and is untranslated until the culture file has a value for the name.
- A resource file has no fuzzy state, so the Fuzzy column is hidden and ticking it does nothing.
- A name the culture file has but the neutral file lost is listed as obsolete and never written away.
- Saving writes the file back verbatim until a row is edited. After that a translated name gets or updates its
  `<data>` (with the translator's comment), an emptied one loses it, and the culture file's own headers, typed
  resources and layout stay. An empty or unreadable culture file is started from the standard resource headers.
- Creating a culture file is still done by adding `Form1.fr.resx` to the project; the view then lists every neutral
  string as untranslated.

Model and view are separate on purpose: the model is pure (unit-tested, no WPF), so the `.resx` adapter feeds the
same view with rows keyed by resource name.

## Sources

- GtkBuilder (`ICSharpCode.GtkDesigner`): every element with `translatable="yes"` (`<property>`, menu
  `<attribute>`, string-list `<item>`), its `context` as `msgctxt`, its `comments` as a `#.` comment.
  `GtkTranslatableSource` is registered by `RegisterGtkTranslationSourceCommand`.

## Tests

`src/AddIns/Misc/Translations/Test` (pure: catalogue round trip, merge, project update, registry, the translator
model) and
`GtkDesigner.Tests.GtkTranslationsTests` (the `.ui` extraction). The integration test
`GtkDesigner_UpdateTranslations_WritesThePotFromUiFilesAndMergesPoFiles` runs `od.translations.update` against the
real host, so the addin discovery, the source registration and the merge are exercised together;
`GtkDesigner_TranslatorView_EditsAPoFileAndSavesIt` opens a `.po`, reads the grid's rows and columns
(`od.translator.status`), filters, edits through the DevFlow actions and checks the saved file;
`GtkDesigner_PoFilesGetALanguageService` drives the generic language-service actions (`od.diagnostics`, `od.quick-info`,
`od.completions`, `od.document-outline`, `od.go-to-definition`) against a `.po`, the path the editor itself uses; and
`GtkDesigner_TranslatorView_TranslatesACultureResxAgainstItsNeutralFile` does the same for `Form1.fr.resx` against `Form1.resx`. (Both live in the GTK
designer's journey class because it owns the fixture project; they exercise this addin.)
