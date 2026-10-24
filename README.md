# Epoch Assist

A MelonLoader mod for Last Epoch: per-slot skill autocast and an auto health potion with an optional low-ward condition.

Using mods online is against Last Epoch's terms of service. Use at your own risk.

## Install

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.x into the game folder and start the game once.
2. Copy `EpochAssist.dll` into the game's `Mods` folder.
3. Press F2 in game to open the menu.

## Menu keys

While the menu is open and the game window has focus:

| Key | Effect |
|---|---|
| Tab / Down | next control (Shift+Tab or Up for the previous one; Steam's overlay takes Shift+Tab by default) |
| Enter, Space | press the focused button, flip a switch, pick an option |
| Left / Right | move a slider one step, or pick the previous or next option |
| PageUp / PageDown | previous or next page |
| Esc | cancel a key capture, otherwise close the menu |

In the search field, typed keys go to the field; Tab leaves it and Esc closes the menu.

## Profiles

Every setting, from autocast to the HUD, theme and keys, belongs to a profile. **Default** is used out of game and by
every character until it picks another one. Under **Settings → Profiles** (the last item in the Settings sidebar) you can pick which one the character you're
playing uses, make a new profile (a copy of Default), duplicate the one in use, and rename or delete profiles. The sidebar
shows the profile in use; changing any setting changes that profile. Which autocast slots are toggled always stays with
each character.

Pick the profile from the dropdown at the top of Profiles. **Export** writes the parts you choose (Mini HUD,
Appearance, Keys, Log level, and Autocast's on/off, Controls, Pausing, Targeting and Enemy gate) to
`UserData/EpochAssist/exports/` and the clipboard; **Import** reads an export from there or the clipboard and takes
the parts you choose, into the profile in use or as a new one.

Profiles are kept in `UserData/EpochAssist/profiles/` and each character's choice and slots in
`UserData/EpochAssist/characters/`. When you enter the game with a character, autocast starts paused until you use a
skill yourself.

## Diagnostics and crash reports

Settings → Diagnostics (in the sidebar while Settings is open: Overview, Features, Reports) shows what the mod is doing: its build, every feature's state and errors, game members missing
after a game update, and how much frame time each feature takes. **Copy diagnostics** puts it all on the clipboard as
plain text for a bug report, without your user name, character names or folder paths. The Log level setting on the
Overview sets how much goes to the MelonLoader log; Debug is for bug reports.

When a feature stops after an error, a crash report is saved in `UserData/EpochAssist/reports/`. The newest ten are
kept, each is at most 64 KB, and the same error again in one game session counts up in its report instead of adding
another. They are listed under Diagnostics → Reports with a **Copy** button. Nothing is ever sent anywhere.

## Console

Settings → Diagnostics → Console runs commands: type `help` for the list. `set <setting> <value>` changes a setting, `run <action>`
runs anything a key or button can, `log` shows recent log lines, and `state`, `status`, `hotkeys`, `compat` and `theme`
show what the mod sees; `profiles` lists and manages profiles. Bind a key to it under Settings → Keybinds → Console key (not set by default). Enter runs a line;
Up and Down bring back earlier ones.

## Animations

Settings → Appearance has **Animations** (on by default) and **Animation speed** (0.5x to 3x) for the menu's motion,
such as Settings growing out of the gear in the sidebar (click the gear again to close it).

## Themes

Pick a theme under Settings: Slate (default), Ember, Epoch or Glass. To make your own, put a JSON file in
`UserData/EpochAssist/themes/` (in the game folder). It names a built-in theme as its base and changes any of its values:

```json
{
  "version": 1,
  "name": "Slate Amber",
  "base": "Slate",
  "values": { "accent": "#E0A84A", "radius.card": 6 }
}
```

Colours are `#RRGGBB` or `#RRGGBBAA`. A value that can't be used keeps the base theme's, and Settings lists the problem
under the theme choice. Themes set colours, corner radii, card spacing and font sizes; the font itself follows the Font
and Text settings. The mod only reads these files: it never changes, renames or deletes them. Files are read again when
you open Settings.

## Build

Create `build.local.env` next to `build.sh`:

```sh
LE_ML='C:\path\to\Last Epoch\MelonLoader'
NUGET_SOURCE='https://api.nuget.org/v3/index.json'
```

Then run `./build.sh` (Debug), `./build.sh deploy` (Debug, copied to `Mods/`), or `./build.sh release` (writes `dist/EpochAssist-v<version>.zip`).

## License

MIT
