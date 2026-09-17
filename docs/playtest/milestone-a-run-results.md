# Playtest QA — Milestone A death / extract → RunResultsPanel

Death or extract must open `RunResultsPanel` with an outcome. A silent Title dump (`RunReturnInfo` only) fails this exit.

Automated coverage:

- EditMode: `SessionUiBridgeRunResultsTests` (`DeathOpensRunResultsPanelWithOutcome`, `ExtractOpensRunResultsPanelWithOutcome`, `SliceCompleteReasonOpensExtractionResults`, `ResultsReturnToTitleRaisesTheHostSeam`, `QuitToTitleDoesNotOpenResults`)
- PlayMode: `RunLifecyclePlayModeTests.DeathShowsResultsAndConfirmReturnsToTitleWithTheLastRun` and `ExtractShowsResultsAndConfirmReturnsToTitleWithTheLastRun`

```
pwsh tools/test.ps1 -Mode EditMode -Filter SessionUiBridgeRunResults
pwsh tools/test.ps1 -Mode PlayMode -Filter RunLifecyclePlayModeTests
```

## Acceptance

### 1. Die once → RunResultsPanel

1. Title → New Run (slice defaults). Confirm the hub is live.
2. Die once (stand until incapacitated, or set health to 0 in a debug boot).
3. Expect **RUN ENDED — DEATH** (`RunResultsPanel`) over the paused run: outcome death, time survived, seed · biome · difficulty context. Simulation must not keep ticking.
4. Do **not** land on Title without this panel.

### 2. Extract once → RunResultsPanel

1. Title → New Run (or continue from a live hub).
2. Extract once (`EndRun("extraction")` in automation; in play, finish the slice / extract path so `PlayableSliceCompleted` fires with extract/complete).
3. Expect **RUN COMPLETE — EXTRACTION** with outcome extraction (complete/completion normalize to extraction).
4. Do **not** land on Title without this panel.

### 3. From results, return to Title

1. On either results panel, focus starts on **Return to Title**.
2. Confirm. Title shows `Last run: death|extraction — seed … · biome · difficulty` and `Progress: objectives n/m`.
3. New Run from results is a fresh Milestone A hub boot, not a silent replay of the finished run.

Pause **Quit to Title** is not this flow: it may return to Title without a results panel. Death and extract must not use that path.
