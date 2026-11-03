# Sea Power Tactical UI

Interface mod for **Sea Power** (BepInEx 5): radar coverage map, air defence / ASW / SAR / CAP windows, strike planner, overview, command ring, range rings. Works on its own: no bot, no network, no microphone. Languages: English, Russian, Chinese.

- Version: **0.31.2** (Sea Power public beta)
- Steam Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3827955062
- Player guide and install: `SeaPowerTacticalUI/README.md`; history: `SeaPowerTacticalUI/CHANGELOG.md`
- License: GPL-3.0 (`LICENSE`)

## Branches
- `main` — line for the current Sea Power public beta (globe map).
- `stable-0.30` — line for the current stable Sea Power release.

## Build
Requires .NET SDK 8 and a Sea Power install (game assemblies are referenced, not shipped).

```
GAME_DIR="/path/to/Sea Power" ./build.sh
```

Output DLLs go to `out*/`. `package.sh workshop` builds the Workshop package. Folders `*test/` are console test stands for the pure logic (`dotnet run --project <stand> -- <section>`).

`Common/` is shared code also used by a separate, paused project; the UI mod does not use its network parts.
