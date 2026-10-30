# Changelog

Generated from the commit history by `tools/release.sh`

## 0.1.0 - 2026-10-30

Tested on Last Epoch 1.5.3.

First public release.

### Added

- Macros: Left Ctrl + skill key toggles a skill slot, each with its own conditions, plus Shared Conditions such as an enemy gate; macro sets per class cast, drink, portal and wait by rules.
- Conditions on health, mana, ward, buffs, skills, potions, enemies and zone; named conditions are written once and used by any macro; sets are shared by code.
- Planner imports Maxroll and Last Epoch Tools builds and marks the plan on passive and skill trees, skills, gear, idols, blessings and weaver tree, with a checklist, variants per character and the build's guide in game.
- Loot filter made from the planned build, for one setup or levelling to endgame.
- Stash layout presets build folders and tabs within a gold budget, one step at a time; items in the wrong tab are outlined, never moved.
- Sessions record zones, XP, gold, drops, deaths, echoes and faction progress, with a live dashboard, past sessions, CSV export and figures on the mini HUD.
- Map Reveal reveals the zone map, adds map zoom and reskins player icons and map outlines.
- Proximity Interact picks up and opens nearby interactables automatically.
- Modules: each part loads only when selected; unloaded parts show nowhere and cost nothing. First start asks which to load.
- Profiles per character, with export and import.
- Mini HUD with each feature's state and quick toggles; notifications placed and timed as set.
- Themes, fonts and colour vision modes; Diagnostics copies bug-report text and keeps crash reports.
