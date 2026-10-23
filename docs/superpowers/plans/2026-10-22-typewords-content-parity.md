# TypeWords Content and Article Reading Parity Plan

**Spec:** `docs/superpowers/specs/2026-10-22-typewords-content-parity-design.md`  
**Execution:** Inline / Native in the current workspace, per user direction. Preserve pre-existing dirty changes and do not create a commit that mixes them with this work.

## Constraints

- Keep WinUI 3, .NET 10, SQLite, current FSRS/session behavior, and offline speech.
- Append migration 9 only; preserve migrations 1–8.
- Do not embed TypeWords data or media in the Lexio package.
- Keep existing CSV/TXT import and user-created article editing working.
- Only replace rich word details when the import supplies non-empty detail fields.
- Allow the user to select the downloaded TypeWords ZIP directly; never embed or extract its contents into the application package.
- Use the existing test projects and SDK; add no packages.

## Task 1 — Parse and persist rich TypeWords word data

**Files:** `src/Lexico.Core/Domain/Models.cs`, `src/Lexico.Core/Data/Migrations.cs`, `src/Lexico.Core/Data/AppDatabase.cs`, `src/Lexico.Core/Data/AppDatabase.Dictionary.cs`, `src/Lexico.Core/Data/AppDatabase.Vocabulary.cs`, `src/Lexico.Core/Data/AppDatabase.Study.cs`, `src/Lexico.Core/Study/StudyService.cs`, `src/Lexico.Core/Dictionary/DictionaryService.cs`, `tests/Lexico.Core.Tests/DictionaryTests.cs`, `tests/Lexico.Core.Tests/MigrationTests.cs`.

1. [x] Add a failing test for a representative TypeWords JSON word, including senses, examples, phrases, synonyms, etymologies, and related words.
2. [x] Run the focused Core test and confirm it fails because TypeWords import/details persistence are missing.
3. [x] Add the typed detail records and append migration 9 with `words.details_json`.
4. [x] Parse UTF-8 TypeWords dictionary JSON and map its `word`, phonetics, `trans`, `sentences`, `phrases`, `synos`, `etymology`, and `relWords` fields.
5. [x] Persist/load rich details through upsert, imports, search, Library results, and StudyItem; merge partial details without losing previously imported content.
6. [x] Re-run the focused test and the full Core suite.

**Expected:** imported structured fields round-trip through SQLite; bad JSON writes nothing; older word constructors remain source-compatible.

## Task 2 — Expose TypeWords dictionary import and word details

**Files:** `src/Lexico.App/ViewModels/LibraryViewModel.cs`, `src/Lexico.App/Pages/LibraryPage.xaml`, `src/Lexico.App/Pages/LibraryPage.xaml.cs`, `src/Lexico.App/ViewModels/LearnViewModel.cs`, `src/Lexico.App/Pages/LearnPage.xaml`, `docs/01-product-spec.md`, `docs/ui-contract.md`.

1. [x] Add failing Core/App acceptance tests for TypeWords detail mapping and display.
2. [x] Run them and confirm the importer, persistence, and Library detail presentation gaps.
3. [x] Allow the Library picker to select `.json` and route it through the TypeWords importer while keeping CSV/TXT unchanged.
4. [x] Show rich details in a collapsed Library result expander and after the Learn answer is revealed; keep the existing practice prompt and FSRS rating behavior.
5. [x] Build the app and run the Core and App test suites.

**Expected:** user can import a TypeWords JSON dictionary from Library and see imported details in Library/Learn.

## Task 3 — Import bilingual TypeWords articles

**Files:** `src/Lexico.Core/Domain/Models.cs`, `src/Lexico.Core/Data/Migrations.cs`, `src/Lexico.Core/Data/AppDatabase.Articles.cs`, `src/Lexico.Core/Dictionary/TypeWordsJsonImporter.cs` (only if a shared importer file is needed), `src/Lexico.App/ViewModels/ArticlesViewModel.cs`, `src/Lexico.App/Pages/ArticlesPage.xaml`, `src/Lexico.App/Pages/ArticlesPage.xaml.cs`, `tests/Lexico.Core.Tests/*`.

1. [x] Add a failing test for TypeWords article JSON with `titleTranslate`, paragraph-aligned text and translations, duplicate input, and mismatched paragraph counts.
2. [x] Run the focused test and confirm parsing/persistence is missing.
3. [x] Append `articles.title_translation` to migration 9 and map TypeWords paragraph pairs to existing sentence rows.
4. [x] Add `.json` import to Articles; preserve `.txt`, skip exact duplicates, and reject malformed/unpaired content before any writes.
5. [x] Run focused article tests, migration tests, and the full Core suite.

**Expected:** title/paragraph translations persist, are recoverable after migration/backup, and invalid article JSON is atomic.

## Task 4 — Show and navigate the full bilingual article

**Files:** `src/Lexico.App/ViewModels/ArticleLearnViewModel.cs`, `src/Lexico.App/Pages/ArticleLearnPage.xaml`, `src/Lexico.App/Pages/ArticleLearnPage.xaml.cs`, `src/Lexico.App/ViewModels/ArticlesViewModel.cs`, `docs/ui-contract.md`, `README.md`.

1. [x] Add a view-model acceptance test for selected paragraph navigation and persisted progress using the App test harness.
2. [x] Confirm the test fails before implementation.
3. [x] Expose title translation and paragraph rows to a scrollable native reader; highlight and auto-scroll the current row; save selection before changing practice position.
4. [x] Keep existing follow-write/dictation controls and translation reveal behavior, including selection after completion.
5. [x] Run the Core/App suites and build the Release app; inspect changes and update this checklist.

**Expected:** the whole bilingual article stays available while the current paragraph is practiced; clicking a paragraph navigates and saves its position.

## Task 5 — Import recommended TypeWords content directly from ZIP

**Files:** `src/Lexico.Core/Study/TypeWordsArchiveReader.cs`, `src/Lexico.Core/Dictionary/DictionaryService.cs`, `src/Lexico.Core/Study/TypeWordsArticleImporter.cs`, `src/Lexico.App/ViewModels/LibraryViewModel.cs`, `src/Lexico.App/ViewModels/ArticlesViewModel.cs`, the Library/Articles file pickers, and Core/App tests.

1. [x] Add failing Core tests for recommended word/article manifests, friendly book names, bilingual persistence, and missing assets.
2. [x] Read only the selected category from the user ZIP with `ZipArchive`; do not extract files or add dependencies.
3. [x] Reject ambiguous manifests, missing entries, non-UTF-8 content, and archives/assets above their size limits before writing; show manifest/file count mismatches after a successful import.
4. [x] Extend the existing pickers to accept `.zip` while retaining `.json`, CSV, and text imports.
5. [x] Re-run full Core/App Release suites and rebuild the current Release app after this extension.

**Expected:** a downloaded TypeWords ZIP imports its recommended vocabulary from Library or its recommended bilingual articles from Articles; dataset names come from the ZIP manifest.

## Final review

- Check migration 9 is append-only and all required-schema lists include its columns.
- Check empty TypeWords details cannot erase existing content and importer validation occurs before writes.
- Check the article list, title translation, full reader, active paragraph, and current modes remain consistent on resume.
- [x] Bind `last_insert_rowid()` to the active article transaction and cover new-article persistence.
- [x] Preserve article sentence rows and saved progress on title-only edits; reset progress only when sentence content changes.
- [x] Reject TypeWords article JSON that omits translations for its English paragraphs before any writes.
- [x] Display American and British IPA in Library and Learn, including entries with only British IPA.
- [x] Save practice-mode changes made after article completion and use that mode when restarting.
- [x] Re-run the full Core/App suites and build the Release app after ZIP import changes.
- [x] Prior review fixes passed Core/App suites; isolated Release App build succeeded before ZIP import changes.
- [ ] Full `Lexio.slnx` Release build is not verified: the running Lexio process holds the default App output; globally redirecting `BaseOutputPath` also makes the SDK include copied linked sources from the App.Tests `bin` tree. Individual Release test-project builds and the isolated App Release build are the reliable checks.
- [x] Run the actual downloaded ZIP through an isolated temporary database: CET-4 imported 2,607/2,607 listed entries; NCE_1 imported 5 articles/63 bilingual sentences while its manifest claims 72, so the UI reports the mismatch.
- Preserve all unrelated pre-existing working tree changes; leave commits unchecked.
