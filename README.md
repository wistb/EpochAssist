# Epoch Assist

A [MelonLoader](https://github.com/LavaGang/MelonLoader) mod for Last Epoch.

> Using mods online is against Last Epoch's terms of service. Use at your own risk.

## Install

1. Install MelonLoader 0.7.x into the game folder and start the game once.
2. Copy `EpochAssist.dll` into the game's `Mods` folder.
3. In game, press **F2** to open the menu.

## Autocast

- Hold **Left Ctrl** and press a skill's key to turn autocast on or off for that slot.
- Optional: only cast with enemies nearby, aim at the closest enemy, and a hold-to-pause key.
- Autocast pauses when you change zones or enter the game. Use any skill yourself to resume.

## Menu controls

You can use the mouse, or the keyboard:

| Key | Does |
|---|---|
| Tab / Up / Down | move between controls |
| Enter / Space | press, toggle or pick |
| Left / Right | adjust a slider or option |
| PageUp / PageDown | switch page |
| Esc | close the menu |

## Settings

Open **Settings** with the gear in the sidebar.

- **Profiles**: save different setups and pick one per character. Export and import them to share.
- **Appearance**: themes (Slate, Ember, Epoch, Glass), fonts and animations.
- **Keybinds**: change any key.
- **Diagnostics**: if something breaks, click **Copy diagnostics** and paste it into your bug report. It leaves out
  your user name, character names and folder paths. Nothing is ever sent anywhere.

Everything the mod saves is in `UserData/EpochAssist/` in the game folder.

## Building from source

Create `build.local.env` next to `build.sh`:

```sh
LE_ML='C:\path\to\Last Epoch\MelonLoader'
NUGET_SOURCE='https://api.nuget.org/v3/index.json'
```

Then run `./build.sh` (debug build), `./build.sh deploy` (build and copy to `Mods/`) or `./build.sh release` (zip in `dist/`).

## License

MIT
