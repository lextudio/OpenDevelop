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
| Translator view (Design view of a `.po`/culture `.resx`): source, translation, comment and state columns, untranslated/fuzzy filters | - | not implemented |
| Source-view language service for `.po` (placeholder mismatch, fuzzy, missing plural forms) | - | not implemented |
| `.resx` adapter: `X.resx` is the source, `X.<culture>.resx` the translations, keyed by resource name rather than by msgid | - | not implemented |
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

## Sources

- GtkBuilder (`ICSharpCode.GtkDesigner`): every element with `translatable="yes"` (`<property>`, menu
  `<attribute>`, string-list `<item>`), its `context` as `msgctxt`, its `comments` as a `#.` comment.
  `GtkTranslatableSource` is registered by `RegisterGtkTranslationSourceCommand`.

## Tests

`src/AddIns/Misc/Translations/Test` (pure: catalogue round trip, merge, project update, registry) and
`GtkDesigner.Tests.GtkTranslationsTests` (the `.ui` extraction). The integration test
`GtkDesigner_UpdateTranslations_WritesThePotFromUiFilesAndMergesPoFiles` runs `od.translations.update` against the
real host, so the addin discovery, the source registration and the merge are exercised together.
