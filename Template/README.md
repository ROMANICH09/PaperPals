# PaperPals addon template

This folder shows how to build a PaperPals character. PaperPals never loads it: the library only reads files named
exactly `npc.json`, and the files here are called `npc.template.json` and `manifest.template.json`. The example
character in `frames/` is Amix (from the Amirkhan_NPC addon, shared with his permission): a complete, correct set
to compare your own frames against. Replace all of them with your character; never re-upload Amix's pictures.

An addon is data only: pictures plus two JSON files. You do not need to write or build any code.

## Make your own character
1. Copy this whole `Template` folder somewhere outside the game and rename it, for example `MyFriend_NPC`.
2. Replace every PNG in `frames/` with your character's picture of the same name (rules below).
3. Rename `npc.template.json` to `npc.json` and fill it in (reference below).
4. Rename `manifest.template.json` to `manifest.json` and fill it in.
5. Replace `icon.png` with a 256x256 PNG (a face crop works well) and rewrite this README for your character.
6. Test locally: zip it (step 7) and import the zip into r2modman as a local mod. Host a game with
   PaperPals' `GuaranteedSpawn = true` and land on any moon.
7. Zip the CONTENTS of the folder (select the files, not the folder): `icon.png`, `README.md`, `manifest.json`,
   `npc.json`, `frames/`. Upload to Thunderstore. Upload only pictures and voices of people who agreed to it.

## Frames
### Required: 24 files
`{view}_{pose}_{mouth}.png` with:
- view: `front`, `back`, `left`, `right`
- pose: `idle` (standing), `step-a` and `step-b` (the two walking steps)
- mouth: `quiet` (closed) and `talking` (open)

### Optional poses (used only if ALL files of that pose exist)
| Pose | Files | Used for | Without it |
|---|---|---|---|
| `crouch` | 8: `{view}_crouch_{mouth}.png` | holding still while scared, kneeling for a moment after every hit | idle frames |
| `crouch-a`, `crouch-b` | 16 | sneaking away from monsters | the crouch frame slides slowly; normal walk if no crouch |
| `run-a`, `run-b` | 16 | meltdown panic, fleeing a monster that is too close | walk frames played faster |

This template includes the 24 required frames and `crouch`.

### Picture rules
- PNG with a transparent background.
- All frames of one character have the same canvas size and the same scale.
- The feet stand on the BOTTOM edge of the canvas, and the body is centred left to right. PaperPals puts the
  bottom-centre point on the ground.
- Crouch frames keep the standing scale: the crouching body is simply shorter on the same canvas.
- Which side is which: `right` means the character's nose points to the right of your screen, `left` to the left.
  Both side views must exist as separate files (mirror one to make the other if needed).
- `back` frames: quiet and talking can be the same picture.
- Size: about 900 to 1000 px tall is plenty. This template's frames are 470x904. Bigger only costs memory.

### Measuring `eyeLevel`
Open the `front_idle_quiet` frame, find the row of pixels through the eyes, then:
`eyeLevel = (canvas height - eye row from the top) / canvas height`.
Example: Amix's canvas is 904 px tall and his eyes are at about row 78, so (904 - 78) / 904 = 0.914.

## npc.json reference
| Key | Default | Meaning |
|---|---|---|
| `format` | 1 | Format version. Always 1 for now. |
| `id` | (required) | Unique name: lowercase letters, digits, `_`. Also the config section `[id]`. Never change it after release. |
| `displayName` | id | Name shown when scanned. |
| `scanSubtext` | "" | Second line when scanned. |
| `voice.targetPlayer` | "" | Whose MirageVoces clips he speaks: Steam name (as in `Mirage/Centralizado/jugadores.json`), a SteamID64, or `yo` for the host. Empty = silent. |
| `voice.categories` | see template | Clip categories (file-name prefix before `__`) for normal talk. |
| `voice.panicCategories` | `grito,ayuda,aviso` | Shouts during meltdown and when hurt. |
| `voice.fearCategories` | `ayuda,aviso,grito` | Lines when a monster is near. |
| `voice.hurtCategories` | `grito,ayuda` | Line on every hit he takes (a panic scream or a call for help), while he kneels. |
| `sprite.eyeLevel` | 0.912 | See "Measuring eyeLevel". |
| `sprite.heightCm` | 180 | How tall he looks in game. 180 = eyes level with a player. Kept between 1 and 200. |
| `poses.crouchChance` | 0 | Chance (0 to 1) to crouch at random when he stops. 0 keeps crouching for fear only. |
| `fear.enabled` | true | React to monsters. |
| `fear.radius` | 10 | Metres at which a visible monster scares him. |
| `fear.calmDelay` | 3 | Seconds after the monster is gone before he relaxes. |
| `fear.runDistance` | 4 | Closer than this he stops sneaking and runs. |
| `stats.hp` | 6 | Hits he survives (a shovel hit is 1). |
| `stats.walkSpeed` | 2.6 | Metres per second. |
| `stats.rarity` | 25 | Indoor spawn weight (vanilla monsters are about 1 to 100). 0 = never spawns naturally. |
| `features.meltdownPanic` | true | Panic and run to the ship when the apparatus is pulled. |
| `features.deathScream` | true | Scream when he dies. |

The host can override most of these per character in `BepInEx/config/luver_inn.paperpals.cfg`, section `[id]`.

## Voice
PaperPals plays clips that MirageVoces recorded on the host's PC:
`<game folder>/Mirage/Centralizado/Efectos/<SteamID64>/*.wav`. The addon itself never contains voice recordings.
Without MirageVoces, or with no clips for `targetPlayer`, the character is silent and nothing breaks.

## Rules for every player
- Everyone in the lobby needs the same addons and the same PaperPals version.
- A character never harms players. Monsters only hurt him outside, and only while a player can see it.
