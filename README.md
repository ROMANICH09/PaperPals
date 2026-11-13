# PaperPals

A Lethal Company (v81, BepInEx 5) library that turns your friends into harmless 2D paper cut-out NPCs.
Each character is a data-only **addon**: pictures plus one `npc.json`, no code.

- **Mod:** `Luver_Inn-PaperPals` on Thunderstore.
- **Make a character:** download
  [PaperPals_AddonTemplate.zip](https://github.com/ROMANICH09/PaperPals/releases/download/v1.0.0/PaperPals_AddonTemplate.zip)
  and follow its [README](Template/README.md). It covers the frames, poses, `eyeLevel` and every `npc.json` key.
- **What the NPCs do:** see the [package README](package/README.md).

## Repository layout
| Path | Contents |
|---|---|
| `src/` | The BepInEx plugin (C#, netstandard2.1). |
| `Template/` | The addon template (example character: Amix, shared with his permission). |
| `package/` | Thunderstore files for the library: manifest, README, changelog, icon. |
| `tools/make_packages.py` | Builds and validates the Thunderstore zips into `dist/`. |
| `CHECKLIST.md` | The in-game regression checklist. |

## Build
Requires the .NET SDK, Lethal Company, and a mod-manager profile with BepInExPack and LethalLib.
```
cd src
dotnet build -c Release -p:LCDir="<game folder>" -p:Profile="<mod manager profile folder>"
cd ..
python tools/make_packages.py
```
`-p:InstallToProfile=true` also copies the DLL into the profile for testing.

## How it works (short)
- Every `npc.json` under `BepInEx/plugins` becomes one enemy, registered in a fixed order (sorted by `id`) through
  LethalLib with a runtime prefab. No AssetBundles, no NetcodePatcher.
- The host picks voice clips (MirageVoces' `Mirage/Centralizado/Efectos/<SteamID64>/*.wav`) and sends PCM to everyone
  through NGO named messages (`PaperPals.Voice`, `PaperPals.Teleport`). Clients need no voice files.
- Behaviour states (normal, panic, fear sneak/hold/run, idle crouch, in ship) sync through EnemyAI's own state RPC.
- Witness rule: monster damage and instant kills are ignored indoors, during a 10 s grace and when no player is
  close or watching.

Photos and voices belong to the people shown. Only make addons of people who agreed to it.
