# Agent notes

## Verifying changes on macOS
Run `tools/mac/test.sh` (see `tools/mac/README.md`): `dotnet` for a quick Core check, `editmode` or `playmode` for Unity.
Run it once per PR. If it fails, fix the product code or raise the question; do not keep rewriting test harness choreography.
Windows uses `tools/test.ps1`.
