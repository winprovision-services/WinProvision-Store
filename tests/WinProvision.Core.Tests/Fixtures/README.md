# Operation compatibility fixtures

These fixtures represent the persisted JSON contracts at pre-refactor commit `7b1f62c` (the stage 5 starting point before the stage 6 overlay work). The operation history, operation snapshot, operation preferences, and application preferences serializers are unchanged from that commit.

- `operation-history-pre-refactor.jsonl` exercises an operation history record containing a nested snapshot.
- `operation-preferences-pre-refactor.json` exercises the existing operation preferences property names and values.
- `../../WinProvision.Store.Smoke/Fixtures/app-preferences-pre-refactor.json` exercises saved operations panel height and expanded state.

The fixtures are intentionally kept as files in the test projects and embedded for compatibility tests; they are not application defaults.
