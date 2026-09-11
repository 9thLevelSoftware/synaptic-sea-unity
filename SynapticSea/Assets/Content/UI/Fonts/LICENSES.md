# Font licenses

Required by the UI presentation spec (`docs/game/features/ui_presentation_program.md`): record license, source, and version for every font file.

| Family | Files | Version | Source | License |
|---|---|---|---|---|
| Inter | `Inter/Inter-{Regular,Medium,SemiBold,Bold}.ttf` | 4.1 | https://github.com/rsms/inter/releases/tag/v4.1 | SIL Open Font License 1.1 (`Inter/OFL.txt`) |
| JetBrains Mono | `JetBrainsMono/JetBrainsMono-{Regular,Medium,Bold}.ttf` | 2.304 | https://github.com/JetBrains/JetBrainsMono/releases/tag/v2.304 | SIL Open Font License 1.1 (`JetBrainsMono/OFL.txt`) |

Both licenses permit bundling in a commercial game. The OFL text must ship with any distributed font files. The in-game credits must also name the fonts; add a "Typography" entry when the credits screen is ported, through a Unity-side overlay rather than by editing the synced Godot `credits.json`.
