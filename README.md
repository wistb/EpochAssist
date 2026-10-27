# Epoch Assist

A [MelonLoader](https://github.com/LavaGang/MelonLoader) mod suite for Last Epoch.

> Using mods online is against Last Epoch's terms of service.

## Install

1. Install [latest MelonLoader](https://github.com/LavaGang/MelonLoader/releases/latest) into game folder; start game
   once.
2. Extract [release](https://github.com/wistb/EpochAssist/releases) zip into Last Epoch folder (next to
   `Last Epoch.exe`).
3. In game, **F2** opens menu.

## Features

- **Macros**: all automation on Macros tab.
  - **Skill Slots**: **Left Ctrl** + skill key toggles a slot. Per-slot conditions, plus **Shared Conditions** (e.g.
    enemy gate) for all.
  - **Macros**: sets per class or all classes. Start alone, on key press, while held or on toggle; steps cast, drink,
    town portal, wait, wait until, message.
  - **Targets**: cursor, nearest, rarest, weakest or pack centre, limited by range, rarity, mod, ailment.
  - **Conditions**: health, mana, ward, buffs, skills, potions, enemies (health, rarity, mods, ailments), zone and
    more; all of, any of, none of, optional hold time.
  - **Sharing**: **Copy share code** and **Import set** (imports start off).
  - **Potions**: ready-made Auto potion for low health or a full potion's missing heal. Starts off.
  - **Debugging**: footer switches show macro state, enemy ranges and targets, plus a notice per trigger.
  <details><summary>Screenshots</summary>

  ![The Macros tab](nexus/media/macros.png)

  ![A macro's editor](nexus/media/macro-editor.png)

  ![Shared Conditions with the shared target](nexus/media/shared-target.png)

  </details>
- **Proximity Interact**: uses nearby objects and loot, without moving.
  - **Use**: shrines, chests, caches, rune prisons, forges, unstable rifts, blood crystals.
  - **Pick up**: keys, crafting materials, loot filter picks.
  - **In reach**: never moves the character or reaches past game range. Every kind starts off.
  - **Options**: hold key, mini HUD count, per-use notice, reach rings.
  <details><summary>Screenshot</summary>

  ![Config: Proximity Interact](nexus/media/interact.png)

  </details>
- **Map Reveal**: whole zone on overlay and corner minimap.
  - **Icons**: shrines, rune prisons, caches, tomb chests, omen windows and more, shown on zone entry.
  - **Zoom**: overlay zooms past game's widest; separate corner minimap zoom.
  - **Look**: Slate, Clear or game style, thicker outline.
  - **Player marker**: any game icon, rotated, tinted, scaled, offset.
  - Changes drawing only: explored map and game settings untouched.
  <details><summary>Screenshots</summary>

  ![The overlay map with Map Reveal](nexus/media/map-overlay.png)

  ![Config: Map › Look](nexus/media/map-look.png)

  ![Config: Map › Icons](nexus/media/map-icons.png)

  </details>
- **Session**: per-session stats on Session tab.
  - **Tracks**: XP and gold per hour, drops by rarity, loot filter drops, deaths, echoes.
  - **History**: past sessions in `UserData/EpochAssist/sessions/`, within set limits; off under History.
  - **Export CSV**: two spreadsheet files.

## Settings

Config tab at top of menu; search box above tree finds any setting.

- **Profiles**: one setup per character; export and import to share.
  <details><summary>Screenshot</summary>

  ![Settings: Profiles](nexus/media/profiles.png)

  </details>
- **Mini HUD**: macro state and slots while menu is closed; click for quick toggles. Notifications: own position,
  duration, count, kinds and order.
  <details><summary>Screenshots</summary>

  ![The mini HUD with its quick toggles](nexus/media/hud.png)

  ![Settings: Mini HUD](nexus/media/settings-hud.png)

  </details>
- **Appearance**: themes (Carbonfox, Slate, Ember, Epoch, Glass), fonts, animations. **Colour vision**: rarity and
  danger colours kept apart for deuteranopia, protanopia, tritanopia in mod overlays.
  <details><summary>Screenshot</summary>

  ![Settings: Appearance](nexus/media/appearance.png)

  </details>
- **Diagnostics**: **Copy diagnostics** copies mod state for bug reports, without names or paths. **Mods** lists
  other loaded mods and shared patch targets. Nothing sent anywhere.
  <details><summary>Screenshot</summary>

  ![Settings: Diagnostics](nexus/media/diagnostics.png)

  </details>

All data saved in `UserData/EpochAssist/` in game folder.

## Menu controls

| Key | Does |
|---|---|
| Tab / Up / Down | move between controls |
| Enter / Space | press, toggle or pick |
| Left / Right | adjust a slider or option |
| PageUp / PageDown | switch page |
| Esc | close the menu |

## Bug reports

Config → General → Diagnostics → **Copy diagnostics**, then paste into an [issue](https://github.com/wistb/EpochAssist/issues).
Crash reports: `UserData/EpochAssist/reports/`.

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
