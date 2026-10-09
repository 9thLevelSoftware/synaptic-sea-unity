# Agent notes

## Verifying changes on macOS
Run `tools/mac/test.sh` (see `tools/mac/README.md`): `dotnet` for a quick Core check, `editmode` or `playmode` for Unity.
Run it once per PR. If it fails, fix the product code or raise the question; do not keep rewriting test harness choreography.
Windows uses `tools/test.ps1`.

## Agent skills

### Issue tracker

Issues are tracked in GitHub Issues for 9thLevelSoftware/synaptic-sea-unity (via the `gh` CLI). See `docs/agents/issue-tracker.md`.

### Triage labels

The five default labels: needs-triage, needs-info, ready-for-agent, ready-for-human, wontfix. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
