# Lexio Article Learning Implementation Plan

> **Execution status (2026-10-22):** Implementation and Release builds are complete. The disposable article/migration acceptance harness passed. Desktop UI walkthroughs remain pending because this session cannot control a native WinUI window. Commit steps remain unchecked to preserve the pre-existing dirty worktree.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a local article library with sentence-by-sentence follow-write and dictation practice that resumes from saved progress.

**Architecture:** Store articles, sentence rows, and per-article progress in SQLite. Keep the ordered sentence session separate from FSRS cards; reuse local speech synthesis and the complete database backup path.

**Tech Stack:** C# and .NET 10, WinUI 3, SQLite, existing Windows local speech synthesis.

**Spec:** docs/superpowers/specs/2026-10-22-typewords-workflow-parity-design.md

**Execution order:** Implement the vocabulary plan first so migration 7 is established before this plan appends migration 8.

## Global Constraints

- Keep the existing .NET 10, WinUI 3, SQLite, and FSRS.Core 1.0.7 stack.
- Append schema migration version 8; never edit an applied migration.
- Keep article progress separate from cards, review_logs, and daily plans.
- Import only strict UTF-8 text with one sentence per non-empty line.
- Store translations as optional per-sentence text entered by the user.
- Include the new tables in canonical schema validation and retain SQLite snapshot backup behavior.
- Do not add packages, accounts, network requirements, or online translation.

## Review Focus

- Empty, whitespace-only, and invalid UTF-8 files must be rejected before article data is written.
- Article deletion must remove its sentences and progress without affecting vocabulary or review history.
- A failed progress save must keep the user on the current sentence and allow retry.
- Reopening an incomplete article must restore the same sentence and practice mode.
- A sentence with punctuation, apostrophes, or Unicode must compare case-insensitively without losing those characters.

---

### Task 1: Add article schema and domain records

**Files:**
- Modify: src/Lexico.Core/Data/Migrations.cs
- Modify: src/Lexico.Core/Data/AppDatabase.cs
- Modify: src/Lexico.Core/Domain/Models.cs
- Create: src/Lexico.Core/Data/AppDatabase.Articles.cs

**Interfaces:**
- Produce ArticlePracticeMode values FollowWrite and Dictation.
- Produce ArticleSummary(long Id, string Title, int SentenceCount, int CurrentSentence, ArticlePracticeMode Mode, bool IsComplete, DateTime UpdatedAtUtc).
- Produce ArticleSentence(long Id, long ArticleId, int Position, string Text, string? Translation).
- Produce ArticleDraft(string Title, IReadOnlyList<ArticleSentenceDraft> Sentences) and ArticleSentenceDraft(string Text, string? Translation).
- Produce ListArticlesAsync(), GetArticleAsync(long id), SaveArticleAsync(ArticleDraft article), DeleteArticleAsync(long id), and SaveArticleProgressAsync(long articleId, int currentSentence, ArticlePracticeMode mode, bool isComplete).

- [x] Append migration 8 with articles, article_sentences, and article_progress; use foreign keys with cascade deletion and a unique article sentence position.
- [x] Add article domain records without altering existing study or word records.
- [x] Implement article list/load/save/delete/progress operations in AppDatabase.Articles.cs using the serialized database worker.
- [x] Add all three tables and their required columns to canonical schema validation.
- [x] Build Lexico.Core in Release and manually inspect a migrated database and a pre-migration backup restore.

### Task 2: Add article practice state

**Files:**
- Create: src/Lexico.Core/Study/ArticlePracticeService.cs
- Modify: src/Lexico.Core/Data/AppDatabase.Articles.cs
- Modify: src/Lexico.App/Services/AppServices.cs

**Interfaces:**
- Produce ArticlePracticeState(ArticleSummary Article, IReadOnlyList<ArticleSentence> Sentences, int CurrentSentence, ArticlePracticeMode Mode).
- Produce ArticlePracticeService.StartAsync(long articleId, ArticlePracticeMode mode), SavePositionAsync(long articleId, int currentSentence, ArticlePracticeMode mode), CompleteAsync(long articleId), and RestartAsync(long articleId, ArticlePracticeMode mode).
- StartAsync resumes the saved position and mode for an unfinished article; RestartAsync resets progress to sentence zero using the supplied mode.

- [x] Implement resume, position save, next/previous, completion, and restart semantics without calling StudyService or changing FSRS state.
- [x] Register the service in AppServices using the existing AppDatabase instance.
- [x] Keep the current sentence loaded after a database failure and surface the error to the caller.
- [x] Build Lexico.Core and Lexico.App in Release.

### Task 3: Add article management and import

**Files:**
- Create: src/Lexico.App/ViewModels/ArticlesViewModel.cs
- Create: src/Lexico.App/Pages/ArticlesPage.xaml
- Create: src/Lexico.App/Pages/ArticlesPage.xaml.cs
- Modify: src/Lexico.App/MainWindow.xaml
- Modify: src/Lexico.App/MainWindow.xaml.cs

**Interfaces:**
- Add ArticlesViewModel.Articles, StatusText, SaveArticleCommand, DeleteArticleCommand, ImportCommand, and PracticeCommand.
- ImportCommand accepts one file path; SaveArticleCommand accepts an ArticleDraft.
- Add the articles navigation key to MainWindow and map it to ArticlesPage.

- [x] Implement article list, create/edit form, delete confirmation, and UTF-8 text import.
- [x] Parse each non-empty line as one sentence; reject invalid UTF-8 and files with no sentence before calling SaveArticleAsync.
- [x] Let the editor add an optional translation to each sentence and validate non-empty title and sentence text.
- [x] Add Articles to the navigation pane while preserving existing page routes.
- [x] Build Lexio.slnx in Release.
- [ ] Manually verify create, edit, import, delete, and navigation.

### Task 4: Add sentence-by-sentence practice

**Files:**
- Create: src/Lexico.App/ViewModels/ArticleLearnViewModel.cs
- Create: src/Lexico.App/Pages/ArticleLearnPage.xaml
- Create: src/Lexico.App/Pages/ArticleLearnPage.xaml.cs
- Modify: src/Lexico.App/MainWindow.xaml.cs
- Modify: src/Lexico.App/Services/AppServices.cs

**Interfaces:**
- Add ArticleLearnViewModel.CurrentSentence, InputText, TranslationVisible, Mode, ProgressText, ErrorText, SubmitCommand, PreviousCommand, NextCommand, ReplayCommand, and EndCommand.
- SubmitCommand advances only when case-insensitive trimmed input matches the current sentence; it saves progress before changing the current index.
- ReplayCommand speaks only the current English sentence through the existing Speech service.

- [x] Implement FollowWrite and Dictation presentations with optional translation reveal, replay, skip, previous, and next controls.
- [x] Restore article ID, current sentence, and mode when navigating back to unfinished practice.
- [x] Save sentence position after successful input and skip; save completion at the final sentence and provide a restart action.
- [x] On speech failure keep the current sentence available for retry or skip; on progress failure do not advance.
- [x] Build Lexio.slnx in Release.
- [ ] Manually walk create/import, both modes, navigation away/back, app restart, completion, and restart.

### Task 5: Update user documentation

**Files:**
- Modify: README.md
- Modify: docs/01-product-spec.md
- Modify: docs/02-architecture.md
- Modify: docs/03-database.md
- Modify: docs/ui-contract.md

- [x] Document the Articles navigation, text import format, per-sentence translations, practice modes, progress recovery, and migration 8.
- [x] Confirm every article requirement in the design spec maps to Tasks 1–4.
- [x] Review the implementation diff against the available baseline and task plan.
- [ ] Commit the article learning feature as one focused change.
