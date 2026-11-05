# Changelog

Generated from the commit history by `tools/release.sh`

## 0.2.1 - 2026-11-04

Tested on Last Epoch 1.5.3.

### Added

- Planned items' tooltips have their own size setting (Planner › Interfaces › Inventory).

## 0.2.0 - 2026-11-02

Tested on Last Epoch 1.5.3.

### Added

- New "Skill switched on" condition, so a macro can keep a toggle skill such as Aura of Decay on.
- Delete and Save buttons at a page's top ask first; deleting confirms with a red button
- Filter page in Stash tab finds items by affix tier, value and roll quality across every stash tab; ~ searches work in stash's own search box
- Copy diagnostics adds movement hiccups (stops, swerves, turns while moving) and what the mod did just before them
- Deleting a macro, set or named condition in the library asks first
- Interact picks up items much faster, about 5 a second
- Interact can pick up woven echoes, with a switch of its own

### Fixed

- Macro targets never pick an invulnerable enemy; a cast that only finds one skips instead of aiming at it.
- Planner guide no longer blanks the game's own icons when it lets go of its skill icons
- Esc closes the menu on Stash tab pages
- An aimed macro with nothing in range no longer stops the macros after it.

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
