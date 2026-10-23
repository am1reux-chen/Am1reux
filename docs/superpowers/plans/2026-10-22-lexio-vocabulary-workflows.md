# Lexio Vocabulary Workflows Implementation Plan

> **Execution status (2026-10-22):** Implementation and Release builds are complete. The disposable migration/session acceptance checks passed. Desktop UI walkthroughs remain pending because this session cannot control a native WinUI window. Commit steps remain unchecked to preserve the pre-existing dirty worktree.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add five word-practice modes and persistent favorite, wrong-word, and mastered-card workflows to Lexio.

**Architecture:** Keep all modes on the existing StudyService queue and FSRS card schedule. Persist a selected mode on each study session and persist marks per deck card; wrong answers and Again reviews commit together.

**Tech Stack:** C# and .NET 10, WinUI 3, SQLite, existing Windows local speech synthesis.

**Spec:** docs/superpowers/specs/2026-10-22-typewords-workflow-parity-design.md

## Global Constraints

- Keep the existing .NET 10, WinUI 3, SQLite, and FSRS.Core 1.0.7 stack.
- Append schema migration version 7; never edit an applied migration.
- Keep review_logs immutable and keep existing review ratings: Again=1, Hard=2, Good=3, Easy=4.
- Store favorite, wrong, and mastered marks per card so each deck retains its own state.
- Exclude mastered cards from plan candidate and count queries without changing FSRS due dates or review history.
- Preserve existing Learn bindings and keep Recall as the default mode.
- Use local speech synthesis and do not add packages or network requirements.

## Review Focus

- A wrong typed answer must save one Again log and one wrong mark atomically; a repeated submit must not create a duplicate log.
- A mastered due card must be excluded from plans; clearing its mark must make it eligible with its original due date.
- A card without a usable sense must be unavailable in spelling/self-test mode and remain available in Recall.
- A session interrupted in an input mode must resume in that mode with the same queue position and rating preference.
- Switching modes while typing must not trigger hidden grading shortcuts or accept stale input from the previous card.

---

### Task 1: Persist vocabulary modes and card marks

**Files:**
- Modify: src/Lexico.Core/Data/Migrations.cs
- Modify: src/Lexico.Core/Data/AppDatabase.cs
- Modify: src/Lexico.Core/Domain/Models.cs
- Create: src/Lexico.Core/Data/AppDatabase.Marks.cs
- Modify: src/Lexico.Core/Data/AppDatabase.Study.cs

**Interfaces:**
- Produce StudyMode values Recall, FollowWrite, SpellFromMeaning, Dictation, and SelfTest, with Recall=0.
- Produce CardMark values Favorite, Wrong, and Mastered.
- Produce MarkedWordEntry with CardId, DeckId, DeckName, WordEntry, and the three mark values.
- Produce SetCardMarkAsync(long cardId, CardMark mark, bool enabled).
- Produce SearchMarkedWordsAsync(string query, CardMark mark, long? deckId = null, int limit = 50).

- [x] Append migration 7 with a card_marks row keyed by card_id, boolean favorite/wrong/mastered fields defaulting false, and sessions.study_mode defaulting to Recall; update canonical required-schema validation.
- [x] Add StudyMode, CardMark, and MarkedWordEntry domain types without changing existing positional record constructors.
- [x] Implement atomic mark writes and marked-word search; make invalid mark values and unknown card IDs fail without partial updates.
- [x] Exclude mastered cards consistently from planning candidates, new-card counts, and due snapshots.
- [x] Build Lexico.Core in Release; confirm migration 7 is latest and a pre-migration backup restores through the new schema.

### Task 2: Keep mode and wrong-answer state with the FSRS session

**Files:**
- Modify: src/Lexico.Core/Study/StudyService.cs
- Modify: src/Lexico.Core/Data/AppDatabase.Study.cs
- Modify: src/Lexico.Core/Domain/Models.cs
- Modify: src/Lexico.Core/Data/AppDatabase.cs

**Interfaces:**
- Add StudyService.StartAsync(Intensity intensity, StudyMode mode, long? deckId = null); retain StartAsync(Intensity intensity, long? deckId = null) as Recall-compatible behavior.
- Add a StudyMode property to interrupted-session data so RecoverInterruptedSessionAsync restores the selected mode.
- Extend StudyAnswerContext with IncorrectInput while retaining the existing HintUsed default.
- Pass IncorrectInput to answer persistence so the FSRS update, immutable review log, wrong mark, and queue update are one database transaction.

- [x] Persist the selected StudyMode when starting a session and restore it with the saved session queue.
- [x] On IncorrectInput=true, save Grade.Again and the wrong mark atomically; keep the existing AnswerAsync overload compatible.
- [x] Ensure a persistence error rolls back the rating, mark, and queue update together.
- [x] Build Lexico.Core in Release and manually inspect a resumed input-mode session using a disposable data directory.

### Task 3: Add mode selection and input interactions

**Files:**
- Modify: src/Lexico.App/ViewModels/TodayViewModel.cs
- Modify: src/Lexico.App/Pages/TodayPage.xaml
- Modify: src/Lexico.App/Pages/TodayPage.xaml.cs
- Modify: src/Lexico.App/ViewModels/LearnViewModel.cs
- Modify: src/Lexico.App/Pages/LearnPage.xaml
- Modify: src/Lexico.App/Pages/LearnPage.xaml.cs
- Modify: docs/01-product-spec.md
- Modify: docs/ui-contract.md

**Interfaces:**
- Add SelectedStudyMode to TodayViewModel; StartCommand passes it to StudyService.StartAsync.
- Add mode-specific prompt, input, correctness, and submit state to LearnViewModel while keeping existing Recall commands and properties.
- Add a LearnViewModel submit command that records IncorrectInput through StudyAnswerContext and advances only after the answer is saved.

- [x] Add a Today mode selector for Recall, FollowWrite, SpellFromMeaning, Dictation, and SelfTest; default to Recall.
- [x] Implement FollowWrite, SpellFromMeaning, Dictation, and SelfTest prompts; normalize input with Trim and OrdinalIgnoreCase.
- [x] Use local TTS for Dictation; on TTS failure show a retryable message and keep the current card.
- [x] For SelfTest, validate the selected plan before starting: every card must have a usable sense plus three distinct same-deck distractors, otherwise explain why the tier cannot start.
- [x] Record wrong input as Again; on correct typed answers use the current simple or four-grade controls.
- [x] Record a correct SelfTest choice as Good and an incorrect choice as Again.
- [x] Scope keyboard accelerators to the Learn root and disable grade accelerators while a text input owns focus.
- [x] Update the product spec and UI contract with mode and binding behavior.
- [x] Build Lexio.slnx in Release.
- [ ] Manually walk one card through each mode, pause/resume, wrong answer, correct answer, and a narrow window.

### Task 4: Add mark controls and Library filters

**Files:**
- Modify: src/Lexico.App/ViewModels/LibraryViewModel.cs
- Modify: src/Lexico.App/Pages/LibraryPage.xaml
- Modify: src/Lexico.App/Pages/LibraryPage.xaml.cs
- Modify: src/Lexico.App/ViewModels/LearnViewModel.cs
- Modify: src/Lexico.App/Pages/LearnPage.xaml
- Modify: docs/01-product-spec.md
- Modify: docs/ui-contract.md

**Interfaces:**
- Add LibraryViewModel.SelectedMarkFilter with All, Favorite, Wrong, and Mastered choices.
- Add a mark toggle command accepting card ID and CardMark; route persistence through SetCardMarkAsync.
- Add per-result CardId and DeckId so a mark is applied to the exact card in the selected deck.

- [x] Add Favorite, Wrong, and Mastered filters and actions in Library; selecting a mark refreshes the existing search.
- [x] Add Favorite and Mastered toggles to the current Learn card; Wrong is set by incorrect input and can be cleared from Library.
- [x] Show a clear empty state for each filtered list and preserve the current search/deck scope.
- [x] Build Lexio.slnx in Release.
- [ ] Manually verify mark persistence across navigation and application restart.

### Task 5: Update the workflow documentation

**Files:**
- Modify: README.md
- Modify: docs/01-product-spec.md
- Modify: docs/02-architecture.md
- Modify: docs/03-database.md

- [x] Document the new modes, marks, migration 7, and mastered-card scheduling rule.
- [x] Confirm all in-scope requirements in the design spec map to Tasks 1–4.
- [x] Review the implementation diff against the captured pre-task baseline.
- [ ] Commit the vocabulary workflow as one focused change.
