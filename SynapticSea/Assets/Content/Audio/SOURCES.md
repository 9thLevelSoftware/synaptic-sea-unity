# Slice audio content pack sources

Task 1.5 audio clips are **procedural placeholder tones pending final mix**;
licensed as **project-original**. They are generated locally by
`tools/generate_placeholder_audio.py` using only Python's standard-library
`wave` module plus deterministic sine/noise synthesis. No third-party samples,
recordings, or external licensed material are included.

The generator is deterministic and can be re-run to recreate the WAV files.
Each clip is intentionally short and non-silent so the Godot stream catalog and
runtime loading path have real content while authored production mixing is
pending.

## Catalog mapping

| Catalog id | File | Slice role |
|---|---|---|
| `sfx.footstep` | `sfx/footstep.wav` | footsteps |
| `ui.panel.open` | `ui/panel_open.wav` | panel open |
| `ui.panel.close` | `ui/panel_close.wav` | panel close |
| `sfx.tool.pickup` | `sfx/tool_pickup.wav` | tool pickup |
| `sfx.fire.crackle` | `sfx/fire_crackle.wav` | fire |
| `meta.hull.groan` | `sfx/breach_alarm.wav` | breach alarm / hull breach cue |
| `sfx.combat.hit` | `sfx/combat_hit.wav` | combat hit |
| `sfx.combat.threat_alert` | `sfx/threat_alert.wav` | threat alert |
| `sfx.door.open` | `sfx/door_open.wav` | door open |
| `sfx.door.close` | `sfx/door_close.wav` | door close |
| `sfx.dock.land` | `sfx/dock_land.wav` | docking |
| `ui.vitals.low` | `sfx/vitals_low.wav` | death/vitals-low sting |
| `layer.base` | `music/exploration_base.wav` | exploration music |
| `layer.tension_drone` | `music/tension_drone.wav` | tension music |
| `layer.critical_pad` | `music/critical_pad.wav` | critical music |

Coverage is enforced by
`scripts/validation/audio_content_coverage_smoke.gd`, which resolves each
required id through `AudioManager.STREAM_CATALOG`, verifies the file is larger
than a WAV header, and decodes it with `AudioStreamWAV.load_from_file()`.
