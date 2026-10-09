# Playtest 1 notes (60-minute session)

Copy this file to `docs/playtest/playtest-1-notes-<date>-<name>.md` and fill it in during or straight after the session. Short, honest notes beat polished ones. Times are wall-clock minutes from the first moment of play (m:ss).

## Session

| Field | Value |
| --- | --- |
| Date | |
| Who | |
| Build SHA (printed by `playtest-prep.sh`) | |
| Seed (shown on the New Run screen; also in the collected summary) | |
| Class | |
| Time scale (default 60x = 1 minute per game hour) | |
| Fresh install (saves cleared with `--clear`)? | yes / no |
| Session length actually played | |

## Controls reminder (keyboard only)

| Key | Action |
| --- | --- |
| W A S D (or arrows) | Move |
| E (or Enter / Space) | Interact: loot, repair, press terminals, open doors, ramps |
| F | Attack (toward the direction you last moved; there is no mouse aim) |
| R | Reload |
| C | Field craft |
| 1 / 2 / 3 | Hotbar |
| Ctrl | Crouch |
| I | Inventory |
| Tab | Scanner / travel panel (near the lifeboat bridge) |
| M | Map |
| O | Wounds |
| U | Ship modification |
| F1 | Codex |
| Esc | Pause menu |
| Mouse wheel | Camera zoom |
| F5 / F6 / F9 | Save / quicksave / load (development builds only) |

Not built yet (Phase 2, please do not report as bugs): mouse aim and mouse-driven interaction, right-click context menus, status icons (moodles), a visual map.

## Objectives checklist (write the time when it happened)

| Milestone | m:ss | Notes |
| --- | --- | --- |
| Found the repair kit / supplies in the home | | |
| First repair done | | |
| Onboarding objectives finished (how many of 4?) | | |
| Lifeboat departed the home | | |
| First wreck boarded | | |
| First loot from the wreck | | |
| First fight | | |
| First near-death (health under 25%) | | |
| First death (if any) | | |
| Save test: F5 save, quit to the Title, Continue (same place, same state?) | | |
| Returned to the home from the wreck? | | |

## Survival log

- Hunger: when did you first feel hungry, what did you eat, did food ever run short?
- Thirst: same questions for water.
- Oxygen: did you ever run low? Where? Did the lifeboat refill work the way you expected?
- Radiation, fire, wounds, sanity: anything that hurt you, and was it clear why?
- What killed you, or nearly killed you? Could you have seen it coming?
- Did anything feel unfair, or did anything feel too easy?

## Clarity and UX friction

- HUD: could you tell your state at a glance? What was missing or confusing?
- Camera: any moment you could not see your character, a wall blocked the view, or the zoom felt wrong?
- Interaction: did any press of E do the wrong thing, do nothing, or start something you did not intend? (A known class of bug: two things at one spot, one swallows the key. Note the place and what you pressed.)
- Did you ever not know what to do next? Where and why?
- Text, prompts, tooltips: anything unreadable or misleading?
- Menus and settings: anything awkward?

## Economy feel

- Was the starting kit enough to repair the lifeboat? Too much? Did you have spare parts?
- Food and water at the start: enough for the first excursion? Too generous?
- Did the wreck feel worth the risk? What did you find?
- Weight and carrying: did the bag limit get in the way?

## Generation feel

- Home layout: did it make sense? Was anything unreachable, cramped, or oddly placed?
- Did the lifeboat dock look right (position, walls, doorway)?
- Were the objectives and supplies easy to find or hidden?
- Wreck layout: did it feel different from the home? Any dead ends?

## Bugs

Add one block per bug. Attach the Player.log excerpt from the collected summary where there is one.

```
Bug <n>: <one-line title>
When (m:ss):
Steps:
Expected:
Actual:
Player.log excerpt (or "none"):
Severity: blocks play / hurts play / cosmetic
```

## Would you keep playing?

- After 60 minutes, did you want to go on? What pulled you forward, what pushed you away?
- Best moment:
- Worst moment:
- One thing that would improve the next session the most:

## After the session

1. Quit the game (do not leave it running).
2. Run `tools/mac/playtest-prep.sh --collect`.
3. Attach this file and the printed session folder (`builds/playtest/session-<time>/`) to the report.
