# PaperPals regression checklist (from Amirkhan_NPC 1.1.0, tag `amirkhan-1.1.0`)

Run with an addon installed. Each line says what to look for, in game or in `BepInEx/LogOutput.log`.

| # | Behaviour | How to check |
|---|---|---|
| 1 | Loads, registers, no errors | Log: `PaperPals ... loaded`, `Registered PaperPal 'amix'`, 32/32 (or 24/24+) frames |
| 2 | Zero addons: loads, does nothing | Log: `0 PaperPals found`; no errors |
| 3 | Wanders the facility | Walks between nodes, pauses, walk frames alternate (StepTime 0.32) |
| 4 | Talks to a player in view (12 m) | Log `Saying ... to <player>`; voice from his position; faces you |
| 5 | Talks to nobody now and then (90-240 s) | Log `... to nobody` |
| 6 | Lip-sync | Mouth frame while audio plays |
| 7 | Eye height | Standing in front of him, his eyes level with yours (heightCm 180); Dima (175) visibly shorter |
| 8 | Walk-through | Players walk through him; doors open for him |
| 9 | Scan | Red label `<displayName>` / `<scanSubtext>` |
| 10 | Meltdown run | Pull apparatus: panic lines, runs to main entrance, appears outside, runs into the ship |
| 11 | HP and death | Players' hits count (6 by default); scream; lies face-down on the back sprite |
| 12 | Silent when no clips / wrong name | Warning once in log, no errors, still wanders |
| 13 | Four views | Walk around him: front/back/left/right switch correctly |
| 14 | No voice files on clients | Client hears the line although only the host has MirageVoces clips |
| New | Fear | Monster near: crouch-sneak away, fear lines, run when < 4 m, calm 3 s after |
| New | Witness rule | Outside and unseen: monster hits ignored (DebugLog); seen: hits count; 10 s grace |
| New | Two+ PaperPals | Each with its own voice, own size, no shared state |
| New | Template never spawns | No `Template` / `my_friend` in the log |
