# PaperPals

**2D paper cut-outs of your pals, walking around the facility.** PaperPals is a library. On its own it does nothing:
each character is a separate **addon** made of pictures and one `npc.json` file, with no code. Install the
addons you want, for example Amirkhan_NPC (Amix), Dima_NPC, Roma_NPC, Alnur_NPC and Ali_NPC.

## What a PaperPal does
- **Wanders** the facility. He's a photo billboard with 4 views, a walk cycle and a mouth that moves while he talks.
  His size follows the player's eye height (`heightCm` 180 = eyes level with yours).
- **Talks in a real player's voice.** He stops to say a line when you're in view, and now and then says one to
  nobody. Lines come from clips that [MirageVoces](https://thunderstore.io/c/lethal-company/) recorded on the
  **host's** PC; the host sends them to everyone. No MirageVoces, or no clips for that player: he's silent, and
  nothing breaks.
- **Gets scared of monsters:** he crouches, sneaks out of sight, says fear lines, runs if one gets close, and calms
  down after a few seconds.
- **Meltdown:** when the apparatus is pulled, every pal panics, shouts, runs out of the main entrance and into the
  ship, and rides it when it takes off.
- **Never hurts anyone. Players walk through him.** Every hit makes him kneel and scream or call for help. He can
  be killed (6 hits by default) and then lies face-down.
- **Witness rule:** monsters never kill a pal unseen. Inside the facility they can't hurt him at all. Outside, only
  while a player is within 15 m or has him in sight, and never in the first 10 s.
- Scannable (red) with his name.

## Requirements
- **Everyone in the lobby needs PaperPals and the same addons**, at the same versions. Every pal is a network enemy.
- BepInExPack, LethalLib. MirageVoces is optional (for voices).

## Config (`BepInEx/config/luver_inn.paperpals.cfg`)
- Global: voice volume, hearing distance, talk range and cooldowns, lines to nobody, walk-animation speed, eye-height
  sizing, meltdown (on/off, run speed, shout pauses), fear (sneak speed, `FearIgnore` = Manticoil and Docile Locust
  Bees by default), witness rule (`WitnessRange` 30, `WitnessCloseRange` 15, `GraceSeconds` 10).
- Per character, section `[id]`: `Enabled`, `TargetPlayer`, `ScanName`, `Rarity`, `HP`, `WalkSpeed`, `HeightCm`,
  `FearEnabled`, `FearRadius`, `FearRunDistance`. Empty or `-1` means "use the addon's npc.json", so addon updates
  still reach you.
- `TargetPlayer` takes a Steam name, a SteamID64, or `yo` (the host's own voice). Only the host's value matters.
- Testing (host, keep off in real games): `OnlyPaperPals`, `GuaranteedSpawn`, `GuaranteedSpawnCount`,
  `SpawnNearEntrance`, `PanicAfterLanding`, `DebugLog`.

## Make your own character
A template ships inside this package: `BepInEx/plugins/Luver_Inn-PaperPals/Template/`. It is also a separate
download: [PaperPals_AddonTemplate.zip](https://github.com/ROMANICH09/PaperPals/releases/download/v1.0.0/PaperPals_AddonTemplate.zip)
(source code: [github.com/ROMANICH09/PaperPals](https://github.com/ROMANICH09/PaperPals)). Its `README.md` explains
everything: the 24 required pictures (`{front,back,left,right}_{idle,step-a,step-b}_{quiet,talking}.png`), the
optional poses (`crouch`, `crouch-a/b` for sneaking, `run-a/b` for running), how to measure `eyeLevel`, and every
`npc.json` key. The template never spawns, because its files are named `npc.template.json`. Copy the folder
somewhere else, replace the pictures, rename the two `.template.json` files, zip it and upload it. Only upload
pictures and voices of people who agreed to it.
