# Changelog

## 1.0.0
- First release: a library for data-only character addons (`npc.json` + pictures). It grew out of Amirkhan_NPC 1.1.0.
- Behaviour: wander, talk in a real player's voice (MirageVoces, host only), lines to nobody, lip-sync, eye-height
  sizing with per-character `heightCm`, meltdown panic run to the ship (riding it on takeoff), HP and death, scan.
- New: fear of monsters (crouch, sneak, flee, calm down) and the witness rule (monsters never kill a pal unseen).
- Optional poses: crouch, crouch-walk, run. Frames of any width; they are padded for compression.
- Per-character config overrides that still follow addon updates (empty / -1 = use npc.json).
- Every hit makes a pal kneel and say a panic scream or a call for help (`voice.hurtCategories`, default `grito,ayuda`).
