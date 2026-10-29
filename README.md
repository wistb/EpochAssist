# Epoch Assist

Macro suite, build planner overlay, map reveal, session tracking, and much more. A
[MelonLoader](https://github.com/LavaGang/MelonLoader) mod for Last Epoch.

> Using mods online is against Last Epoch's terms of service.

## Features

- **Macros**: highly configurable macro editor with various condition rules.
  - **Skill Slots**: **Left Ctrl** + skill key toggles a slot. Per-slot conditions, plus **Shared Conditions** (e.g.
    enemy gate) for all.
  - **Macros**: sets per class or all classes. Start alone, on key press, while held or on toggle; steps cast, drink,
    town portal, wait, wait until, message.
  - **Targets**: cursor, nearest, rarest, weakest or pack centre, limited by range, rarity, mod, ailment.
  - **Conditions**: health, mana, ward, buffs, skills, potions, enemies (health, rarity, mods, ailments), zone and
    more; all of, any of, none of, optional hold time.
  - **Sharing**: **Copy share code** and **Import set**.
  - **Potions**: ready-made Auto potion for low health or a full potion's missing heal.
  - **Debugging**: footer switches show macro state, enemy ranges and targets, plus a notice per trigger.
  <details><summary>Screenshots</summary>

  ![A macro's editor](nexus/media/macro-editor.png)

  ![Shared Conditions with the shared target](nexus/media/shared-target.png)

  </details>
- **Planner**: imported build marked across interfaces, with a built-in guide viewer.
  - **Import**: Maxroll export, Last Epoch Tools link or build data, pasted on Planner tab; builds kept in a library.
  - **Marks**: outlines by state (matches, partial, to place, not in build); next point labelled; hover hints.
  - **Gear**: badge on each planned equipment slot and idol, coloured ring on idol altar and blessing slots, outline on weaver tree item sockets; hover shows planned item as game's own tooltip beside equipped one, with a card listing planned affixes and tiers.
  - **Compare with build**: optional hotkey shows planned item for hovered item's slot.
  - **Stages**: one build and stage per character, each with its planned level; changed only with ◀ and ▶, hint once next stage's level is reached; Last Epoch Tools variant links gathered as stages of one build, each with own gear and link.
  - **Checklist**: done out of planned per tree, skill bar, gear, idols, blessings, weaver tree and weaver items, with next steps per tree; hover a gear row for planned item.
  - **Guide**: Maxroll build's own guide in game, per stage, with game icons for skills, passives and items; hover a name for its game tooltip; menu search finds its text; Last Epoch Tools builds open their guide page, set by guide link when build data names none.
  - **Loot filter**: loot filter from a stage's plan: uniques by potential, slot rules on planner affixes and tiers, idols, levelling rules, rest hidden; built on EA · Base or any listed filter; strictness Levelling to Very strict; per-rule looks; saved to game's Filters folder, made active only on Use now.
  - **Drawing only**: nothing spent, chosen, equipped or moved; files written only for build library and on loot filter Make; on by default.
  <details><summary>Screenshots</summary>

  ![Passive tree with Planner marks](nexus/media/planner-passives.png)

  ![Skill tree with Planner marks](nexus/media/planner-skills.png)

  ![Weaver tree with Planner marks on nodes and item sockets](nexus/media/planner-weaver.png)

  ![Active blessings with Planner rings, one slot selected](nexus/media/planner-blessings.png)

  ![Planner tab: stage, skill bar and checklist](nexus/media/planner-tab.png)

  ![Planner checklist: next steps and gear in columns](nexus/media/planner-checklist.png)

  ![Planner guide: Maxroll build guide in game](nexus/media/planner-guide.png)

  ![Planner guide: skill tips with game icons](nexus/media/planner-guide-tips.png)

  </details>
- **Map Reveal**: reveal entire map on overlay and minimap. Customize map appearance.
  - **Icons**: shrines, rune prisons, caches, tomb chests, omen windows and more, shown on zone entry.
  - **Zoom**: overlay zooms past game's widest; separate corner minimap zoom.
  - **Look**: Slate, Clear or game style, thicker outline.
  - **Player marker**: any game icon, rotated, tinted, scaled, offset.
  - **Drawing only**: explored map and game settings untouched.
  <details><summary>Screenshots</summary>

  ![The overlay map with Map Reveal](nexus/media/map-overlay.png)

  ![Config: Map › Look](nexus/media/map-look.png)

  ![Config: Map › Icons](nexus/media/map-icons.png)

  </details>
- **Proximity Interact**: automatically interact with objects as they come into range.
  - **Use**: shrines, chests, caches, rune prisons, forges, unstable rifts, blood crystals.
  - **Pick up**: keys, crafting materials, loot filter picks.
  - **In reach**: never moves the character or reaches past game range.
  - **Options**: hold key, mini HUD count, per-use notice, reach rings.
  <details><summary>Screenshot</summary>

  ![Config: Proximity Interact](nexus/media/interact.png)

  </details>
- **Session**: dashboard of each play session and every past one on Session tab, in game colours.
  - **Focus**: chosen figures as tiles and charts: favour, reputation, memory amber, echoes, level, drops, XP, gold, deaths.
  - **Live**: per-hour and last 10 minutes rates, time in and out of town, level progress, time to next level and faction rank.
  - **Charts**: figure over session, timeline of zones, rare drops and deaths, drops by rarity, time by zone.
  - **Tables**: factions with rank progress; echoes with clear time, gains, modifier, reward, corruption; deaths, monolith picks, loot filter drops.
  - **Past sessions**: filter by character and range, per-hour trend, best marks, same dashboard per session; leave out of trends or delete.
  - **New session**: ends session and starts next in place, for timed runs.
  - **History**: kept in `UserData/EpochAssist/sessions/` within set limits, off under History; Export CSV writes two spreadsheet files. Nothing sent anywhere.
  <details><summary>Screenshots</summary>

  ![Session: Current session with focus tiles and chart](nexus/media/session-dashboard.png)

  ![Session: factions and echoes tables](nexus/media/session-tables.png)

  ![Session: Past sessions with trend](nexus/media/session-past.png)

  </details>

## Config

Highly configurable settings available via the Config tab. General options:

- **Mini HUD**: macro state and slots while menu is closed; click for quick toggles. Notifications: own position,
  duration, count, kinds and order.
  <details><summary>Screenshots</summary>

  ![The mini HUD with its quick toggles](nexus/media/hud.png)

  ![Config: Mini HUD](nexus/media/settings-hud.png)

  </details>
- **Appearance**: themes (Carbonfox, Slate, Ember, Epoch, Glass), fonts, animations. **Colour vision**: rarity and
  danger colours kept apart for deuteranopia, protanopia, tritanopia in mod overlays.
  <details><summary>Screenshot</summary>

  ![Config: Appearance](nexus/media/appearance.png)

  </details>
- **Diagnostics**: **Copy diagnostics** copies mod state for bug reports, without names or paths. **Mods** lists
  other loaded mods and shared patch targets. Nothing sent anywhere.
  <details><summary>Screenshot</summary>

  ![Config: Diagnostics](nexus/media/diagnostics.png)

  </details>
- **Profiles**: one setup per character; export and import to share.
  <details><summary>Screenshot</summary>

  ![Config: Profiles](nexus/media/profiles.png)

  </details>

## Install

1. Install [latest MelonLoader](https://github.com/LavaGang/MelonLoader/releases/latest) into game folder; start game
   once.
2. Extract [release](https://github.com/wistb/EpochAssist/releases) zip into Last Epoch folder (next to
   `Last Epoch.exe`).
3. In game, **F2** opens menu.

All data saved in `UserData/EpochAssist/` in game folder.

## Bug reports

**Config › General › Diagnostics › Copy diagnostics**, then paste into an [issue](https://github.com/wistb/EpochAssist/issues).
Crash reports: `UserData/EpochAssist/reports/`.

## Menu controls

| Key | Does |
|---|---|
| Tab / Up / Down | move between controls |
| Enter / Space | press, toggle or pick |
| Left / Right | adjust a slider or option |
| PageUp / PageDown | switch page |
| Esc | close the menu |

## Building from source

Create `build.local.env` next to `build.sh`:

```sh
LE_ML='C:\path\to\Last Epoch\MelonLoader'
NUGET_SOURCE='https://api.nuget.org/v3/index.json'
```

Then run `./build.sh` (debug build), `./build.sh deploy` (build and copy to `Mods/`) or `./build.sh release` (zip in `dist/`).

`./build.sh deploy-release` installs the release build in `Mods/`, `./build.sh status` shows which build is installed,
and `./build.sh dev` switches back.

## License

GPL-3.0 (see [LICENSE](LICENSE)).
