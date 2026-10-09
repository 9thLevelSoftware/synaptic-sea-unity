# Playtest 1: player brief

A 60-minute first look at The Synaptic Sea on the Mac. You start from a fresh New Run in a home ship generated from a random seed, and play as far as you can. We want to know what it feels like, not whether it is finished.

## What to do

1. Start a New Run (leave the defaults; keep the Engineer class if offered). The home ship, its supplies and your lifeboat are different every run.
2. Look around your home. Find the supplies and the repair kit, finish the onboarding objectives, and repair the lifeboat.
3. Take the lifeboat to a wreck (press Tab near the lifeboat bridge to scan and travel). Board it, loot it, survive.
4. Come back, keep going, and try to stay alive for the hour. Dying is fine and useful: note what killed you and start another run.
5. At least once, save (F5), quit to the Title and Continue, and check that you are where you were.
6. Fill in the notes template as you go (`docs/playtest/playtest-1-notes-template.md`). Say what you felt, not what you think should be fixed.

## Controls (keyboard only)

W A S D move, E interact, F attack (toward the direction you last moved), R reload, I inventory, Tab scanner/travel, M map, Esc pause, mouse wheel zoom, F5 save, F6 quicksave, F9 load. The full table is in the notes template.

## What not to worry about

These are known and already on the list. Please note them only if they affect you in a new way.

- **No mouse aim and no right-click menus.** Aiming and interaction are keyboard-only for now; the mouse-driven controls, status icons and a visual map come in Phase 2.
- **Captions are not shown.** The settings toggle exists but nothing displays them (finding UI-1).
- **The "Low Oxygen" tutorial banner stays on screen** after your oxygen recovers (UI-3).
- **Difficulty in Settings is not the run's difficulty.** The run uses the fixed difficulty shown on the New Run screen (UI-2). Biome and difficulty are locked in this build.
- **Audio has barely been heard in play.** Treat sound as unfinished.
- **Placeholder art.** Characters, creatures and props are prototype art.
- **Joining a second ship to your home (the "joined-home flight") is unreliable.** Fewer than half of generated homes have a spot where a wreck can be welded on (LIM-1), and one scripted test of that flow still fails (OPEN-3). If you try it and it does not work, please say so, but it is expected.
- **A plasma cutter is not guaranteed in the home kit.** You do not need it to depart.
- **Book balance, loot odds and survival numbers are placeholders** (OPEN-1). Tell us how they feel; do not assume they are right.
- **Neighbouring seeds can give very similar homes.**

## How to run it

From the repository root, in a Terminal:

```
tools/mac/playtest-prep.sh              # dry run: shows the build, checks it is current, shows what would be cleared
tools/mac/playtest-prep.sh --clear      # backs up, then clears old saves, settings and the log for a fresh install
open builds/StandaloneOSX/dev/TheSynapticSea.app
```

The script refuses to run while the game or a Unity editor is open, and it stops if the build is not the current `main` (rebuild with `tools/mac/build.sh dev`). It never deletes anything without making and checking a backup first.

## How to submit results

1. Quit the game.
2. `tools/mac/playtest-prep.sh --collect`. It copies the log, your saves and the build stamp into `builds/playtest/session-<time>/` and prints the seed(s) you played and any errors.
3. Finish the notes template and attach it together with that session folder.
