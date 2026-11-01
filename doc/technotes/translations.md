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
| Source-view language service for `.po` (placeholder mismatch, fuzzy, missing plural forms) | - | not implemented |
| `ITranslationFormat`: how a file maps to entries and back (`PoFormat`, `ResxFormat`) | `Project/Src/TranslationFormats.cs` | implemented |
| `.resx` adapter: `X.resx` is the source, `X.<culture>.resx` the translations, keyed by resource name rather than by msgid | `Project/Src/TranslationFormats.cs` (`ResxFormat`) | implemented for editing a culture file; Update Translations does not generate culture files |
| Compiling `.po` to `.mo` (`msgfmt`) | - | not implemented |

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
(`od.translator.status`), filters, edits through the DevFlow actions and checks the saved file, and
`GtkDesigner_TranslatorView_TranslatesACultureResxAgainstItsNeutralFile` does the same for `Form1.fr.resx` against `Form1.resx`. (Both live in the GTK
designer's journey class because it owns the fixture project; they exercise this addin.)
