# Learn Session Continuity Implementation Plan

> **For agentic workers:** Use this plan directly in the current workspace; preserve all existing changes and do not commit.

**Goal:** Keep Learn progress, the current card, reveal state, and elapsed timing consistent when a page creates a new ViewModel, while distinguishing pause, navigation, session end, and process recovery.

**Architecture:** Treat `StudyService` as the process-wide owner of committed session progress and active-card state. Make `LearnViewModel` rehydrate its observable progress and card state from that shared service, and expose additive pause/resume commands without changing existing bindings. Add real ViewModel integration tests using an isolated SQLite database.

**Tech Stack:** C# / .NET 10, WinUI 3 ViewModel, SQLite, xUnit.

**Spec:** Current user request; existing binding inventory in `docs/ui-contract.md`.

## Global Constraints

- Keep every existing uncommitted change; do not reset, clean, or commit.
- Do not modify page XAML, visual resources, or animation files.
- Preserve current public ViewModel properties, command names, and parameter types; make any additions backward compatible.
- Do not change dependency versions or the application's target/build configuration.
- Use an isolated temporary database in tests; do not access the user's Lexio data.

## Review Focus

- A newly created Learn ViewModel after a completed answer must show the persisted session count and same next card.
- A revealed but ungraded card must remain revealed across page re-entry; a successfully graded card must clear its reveal state.
- Pause/resume must preserve queue, card, reveal state, and review logs; navigation alone must not pause or end a session.
- Repeated grading or stale card submission must not increase completed count or duplicate immutable review logs.
- Process recovery must restore committed reviews and the queue while hiding any transient reveal state.

---

### Task 1: Reproduce Learn ViewModel resets

**Files:**
- Create: `tests/Lexico.App.Tests/Lexico.App.Tests.csproj`
- Create: `tests/Lexico.App.Tests/LearnViewModelTests.cs`
- Modify: `Lexio.slnx` only if required to build the test project (otherwise run it directly).

**Interfaces:** Uses the existing `AppServices`, `StudyService`, and `LearnViewModel` APIs.

- [x] Add an isolated-database integration test that answers one card, creates a new `LearnViewModel`, calls `ResumeAsync()`, and expects the same `ProgressText` and next `Current.CardId`.
- [x] Run that test and confirm it fails because the count restarts at zero.
- [x] Add a separate reveal-state re-entry test and confirm it fails because the new VM starts unrevealed.

### Task 2: Keep session state in the shared StudyService

**Files:**
- Modify: `src/Lexico.Core/Study/StudyService.cs`
- Modify: `src/Lexico.App/ViewModels/LearnViewModel.cs`
- Test: `tests/Lexico.Core.Tests/StudyServiceTests.cs`
- Test: `tests/Lexico.App.Tests/LearnViewModelTests.cs`

**Interfaces:** Add read-only committed review count, active-card reveal state, pause/resume state, and additive Learn ViewModel observable counts/commands. Existing command signatures remain unchanged.

- [x] Add service tests for pause/resume, stable active card, repeated `PeekNext()`, reveal clearing after a committed answer, and completed-review count.
- [x] Run the targeted service test and confirm the missing continuity behavior fails.
- [x] Implement continuity state in `StudyService`; make repeated `PeekNext()` for the same active card retain its effective timing.
- [x] Rehydrate `LearnViewModel` counts and reveal state from the shared service; only advance progress after a newly committed answer.
- [x] Add `PauseCommand`, `ResumeCommand`, and pause state while keeping `EndCommand` able to finish a paused session.
- [x] Run app and Core tests; confirm page switching does not pause, pause/resume preserves the queue, and ending clears it.

### Task 3: Document the additive UI contract and verify

**Files:**
- Modify: `docs/ui-contract.md`

- [x] Document `CompletedCount`, `RemainingCount`, `IsPaused`, `ShowPaused`, `PauseCommand`, and `ResumeCommand`, including pause semantics and the fact that navigation does not pause.
- [x] Run `dotnet build Lexio.slnx -c Release` and the complete Core and app ViewModel test suites.
- [x] Report actual failures or platform limitations without changing unrelated UI files.
