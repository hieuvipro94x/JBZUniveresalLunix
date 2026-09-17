# Project Overview

JBZUniveresalLunix is a Windows production harness tester for JBZ wiring products. The current .NET application is WPF on .NET 8 for Windows x86, with self-tests in `Tests/`.

The production board backend is FTDI D2XX: scan frames, `.tht` model files, PC-side `TestEngine`, D2XX card capacity and relay behavior. The Leak machine and label printer use their own independent Windows COM connections and must not be mixed with the D2XX board lifecycle.

# Architecture Boundaries

Source of truth:

- `.tht`: D2XX model source, parsed by `ThtModelParser`.
- Product Bundle (`*.jbzproduct.json`): links one part number to its D2XX `.tht` file.
- Leak configuration: per-model runtime settings stored by `ProductionConfigService`; it does not replace or modify `.tht` topology.

Current source code is the final source of truth. If docs and source conflict, investigate with the smallest relevant source reads instead of guessing.

# Important Modules

- `JBZUniveresalLunix.csproj`: main WPF app project.
- `Tests/JBZUniveresalLunix.SelfTests.csproj`, `Tests/Program.cs`: self-test harness.
- `Models/ProductModel.cs`, `Models/TestModels.cs`: neutral model/result DTOs.
- `Models/ProductBundle.cs`: D2XX product bundle mapping.
- `Models/BoardMode.cs`, `Models/ProductionSettings.cs`: production configuration surface.
- `Services/ThtModelParser.cs`: D2XX `.tht` parser.
- `Services/D2xxBoardTransport.cs`: FTDI D2XX lifecycle, scan, relay, resistance routing.
- `Services/UnifiedBoardTransport.cs`: D2XX lifecycle wrapper.
- `Services/TestEngine.cs`: PC-side continuity/fault engine for D2XX flow.
- `Services/BoardAddressMapper.cs`, `Services/BoardIoDecoder.cs`, `Services/ProbeContactClassifier.cs`: D2XX pin/frame/probe mapping.
- `Services/ProductionConfigService.cs`: load/save production settings.
- `ViewModels/MainViewModel.cs`, `ViewModels/TestViewModel.cs`, `ViewModels/ProductionSettingsViewModel.cs`: production UI orchestration and independent Leak/label COM ownership.
- `Views/MainWindow.xaml(.cs)`, `Views/TestWindow.xaml(.cs)`, `Views/ProductionSettingsPage.xaml`: WPF wiring and operator UI.

# Golden Rules / Invariants

- Do not infer or convert another topology format into `.tht`.
- D2XX probe uses only the D2XX mapping/classifier.
- Probe/TESTPIN must not create FAIL, increment production, or fire relay.
- ProductRemoved/removal confirmation is required before re-arming the next cycle.
- FAIL must not trigger marking relay. D2XX FAIL allows only JIG/Relay 1 after confirmation.
- JIG/relay/output must return to initial state after each cycle.
- Hardware lifecycle must serialize owner/reader/dispose/reconnect and reject stale callbacks.
- Never guess firmware commands, ADC formulas, channel mapping, COM roles, FTDI serials, timing, retry policy, or data formats.

# Historical Regressions

Avoid repeating these known failures:

- `StackOverflowException` from recursive property/setter/event loops.
- WPF read-only bindings accidentally configured as `TwoWay`.
- Backup/copy files compiled into the app.
- Stale callbacks after model/board/mode changes.
- Multiple readers or owners on the same FTDI/COM device.
- Disposing a hardware handle while a worker is still reading.
- Probe events entering the fault engine.
- Treating one IO returning as full ProductRemoved.
- Duplicate physical edges inflating master counts.
- FAIL path accidentally firing Relay 2.
- Treating `:RESISTOR,3961` as ohms instead of raw ADC.

# Coding Safety Rules

- Every source-code change must include a release version increment in `Version.props`; keep `VersionPrefix`, `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`, `VersionFileTag`, and `AssemblyTitle` synchronized.
- Make minimal, task-scoped changes.
- Find root cause before editing.
- Do not refactor outside the requested task.
- Do not change protocol/API/data formats unless explicitly required.
- Do not upgrade dependencies unless requested.
- Do not hide errors with empty `catch`; log or preserve meaningful failures.
- Do not edit generated/build output.
- Do not create `*.bak`, `*_old.cs`, `*_copy.cs`, `*_fixed.cs`, or similar backup source files.
- Do not delete source unless the task explicitly requires it and the diff is understood.
- Do not add broad backend switches inside the production flow.

# Hardware Safety Rules

- One physical FTDI or COM device has one active owner/reader.
- Avoid multiple `SerialPort.DataReceived`/reader loops on one COM.
- On reconnect, fully dispose or cancel the previous lifecycle before opening again.
- Guard generation/stale callbacks so old transport events cannot mutate current state.
- If a Leak/printer COM is occupied, report it as occupied.
- Do not assume firmware behavior not proven by trace or current source.

# Bug Fix Workflow

1. Read `AGENTS.md`.
2. Understand the task.
3. Identify relevant files.
4. Read only required code.
5. Find root cause.
6. Apply minimal fix.
7. Build/test.
8. Check targeted regression risk.
9. Review diff.
10. Report clear result.

# Feature Workflow

- Identify the integration point first.
- Preserve the boundary between D2XX production scanning and independent Leak/printer COM services.
- Build/test the affected app and tests.
- Update documentation if an invariant changes.

# Build / Verification

Known verification commands:

- `dotnet clean`
- `dotnet restore`
- `dotnet build -c Release`
- `VERIFY_BUILD_V15_0_0.cmd`
- `VERIFY_BUILD_V15_2_0.cmd` when working on V15.2 changes.

# Definition of Done

A task is not done just because compile passes. Verification must match the risk: unit/self-tests for logic, build for project structure, and explicit hardware status for D2XX/COM behavior. If real hardware was not tested, say so clearly.

# History / SQLite Data Preservation Rules

These rules are CRITICAL for every change that touches History, SQLite schema, migration, indexing, filtering, pagination, export, production persistence, or test-result timestamps.

- Preserve all existing production history. Never solve a migration/history bug by deleting, resetting, recreating, or replacing the real `JBZUniveresalLunix.db`.
- Do not run destructive operations such as `DROP TABLE Tests`, `DELETE FROM Tests`, bulk replacement of production history, or any migration that can reduce history row counts unless the task explicitly requires it and a verified backup/restore plan exists.
- Preserve `Tests`, `TestFaults`, `ResistanceMeasurements`, `WaterProofMeasurements`, Master history, `CycleId`, `PartId`, `ModelId`, and their relationships.
- Legacy rows must remain queryable, visible, filterable, sortable, and exportable after upgrades.
- Migrations must be backward-compatible and non-destructive. Prefer additive `ALTER TABLE ... ADD COLUMN`, safe index creation, `INSERT OR IGNORE`, and transactional updates.
- If a schema migration is required on the production DB: validate `PRAGMA integrity_check`, create a timestamped backup before mutation, execute migration transactionally when SQLite supports it, validate `PRAGMA integrity_check` again, and verify critical row counts did not decrease.
- Never delete or rewrite the production database merely to make self-tests pass. Temp/self-test databases may be deleted after their tests.

Critical row-count checks when relevant:

```sql
SELECT COUNT(*) FROM Tests;
SELECT COUNT(*) FROM TestFaults;
SELECT COUNT(*) FROM ResistanceMeasurements;
SELECT COUNT(*) FROM WaterProofMeasurements;
```

After migration, none of these counts may decrease unexpectedly and no new orphan child rows may exist.

# History Timestamp / Ordering Invariants

`TestHistoryRecord.Started` is a valid C# property and legacy `TestHistory` may use a `Started` column. Do not globally replace `Started` with `StartedAt`.

For the current SQLite `Tests` table, the authoritative columns are:

- `StartedAt`
- `InstallStartedAt`
- `TestStartedAt`
- `ResultAt`
- `RemovalStartedAt`
- `RemovedAt`
- `FinishedAt`

The `Tests` table does NOT use a `Started` column.

For History UI date/time and oldest-to-newest ordering, use the test-start timestamp:

- C#: `EffectiveTestStartedAt = TestStartedAt ?? Started`
- SQLite `Tests`: `COALESCE(t.TestStartedAt, t.StartedAt)`

Correct ordering:

```sql
ORDER BY COALESCE(t.TestStartedAt,t.StartedAt), t.Id
```

Legacy rows with `TestStartedAt IS NULL` must fall back to `StartedAt`; they must not disappear from History.

Date filtering, History row ordering, keyset pagination cursor, and displayed History date/time must use the same timestamp semantics. Do not display `TestStartedAt` while filtering or paginating by `ResultAt`.

If an index is required, only create it after the migration path guarantees every referenced column exists. Example:

```sql
CREATE INDEX IF NOT EXISTS IX_Tests_HistoryAt_Id
ON Tests(COALESCE(TestStartedAt, StartedAt), Id);
```

Do not use the invalid expression:

```sql
COALESCE(TestStartedAt, Started)
```

for the `Tests` table.

# History Query / Pagination / Export Invariants

- Apply History filters in SQLite before pagination.
- Summary counts must represent the full filtered dataset, not only the currently loaded page.
- `PageSize = 200` is acceptable; a hard total cap such as `MaxResults = 200` is not.
- History must be able to reach every matching row through incremental/keyset loading.
- Keyset pagination must use the same History timestamp and stable `Id` tie-breaker as the `ORDER BY`.
- A stale async History request must never append rows into a newer filter session.
- CSV/XLSX export must export the complete current filtered dataset, not just the current DataGrid/ObservableCollection page.
- Prefer streaming large exports rather than materializing the full result set in memory.
- Keep Production / Master / Leak Retest history semantics isolated according to current source rules.

# History Regression Tests

Any change affecting History/SQLite must preserve or add regression coverage for:

- oldest History test-start timestamp appears first;
- a test that finishes later but started earlier does not jump above a newer-started test;
- legacy rows with `TestStartedAt = NULL` fall back to `StartedAt`;
- page 1 -> page 2 has no duplicate or missing rows;
- full filtered summary remains correct while pages are loaded incrementally;
- legacy DB initialization/migration keeps old rows;
- exactly-once `CycleId` persistence remains intact;
- child rows remain attached to their original Test;
- CSV/XLSX exports include the full filtered dataset;
- `PRAGMA integrity_check` remains `ok`.

# Compact Codex Execution / Reporting

For normal bug-fix requests, minimize conversational output and spend effort on repository work.

- Read `AGENTS.md`, inspect only the smallest relevant code surface, find root cause, edit directly, build/test, and continue fixing until the requested scope is green when feasible.
- Do not narrate the investigation step-by-step.
- Do not repeat the user's requirements back at length.
- Do not stop at recommendations when the code can be fixed directly.
- Ask a question only when a missing fact blocks a safe implementation.
- Keep scope minimal and preserve existing behavior outside the requested task.
- Never weaken or remove a valid test merely to obtain a green test run.
- When a current test reflects an obsolete requirement, update it only after proving the current source requirement has changed.

For logic/history changes, run at minimum:

```powershell
dotnet build -c Release
dotnet run --project Tests -c Release
```

All applicable self-tests must pass. If a new regression test increases the total test count, the new total replaces any older hard-coded target such as `51/51`.

Final response should be concise:

```text
RESULT
Root cause:
Files changed:
Data/history preservation:
Build:
Self-tests:
SQLite integrity:
Remaining risk:
```

Do not include hidden reasoning, long explanations, command-by-command narration, or repeated requirements in the final report.

# Detailed Technical Reference

Primary deep reference:

`docs/BAO_CAO_TONG_HOP_CODEX_REVIEW_V15_0_0.md`

Normal tasks should read only `AGENTS.md` plus relevant source files. Open the technical report only for deeper investigation. Source code remains the final source of truth; documentation/source conflicts require investigation, not guesses.
