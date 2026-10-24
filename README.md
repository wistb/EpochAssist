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
