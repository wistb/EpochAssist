# Epoch Assist

A MelonLoader mod for Last Epoch: per-slot skill auto-cast and an auto health potion with an optional low-ward condition.

Using mods online is against Last Epoch's terms of service. Use at your own risk.

## Install

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.x into the game folder and start the game once.
2. Copy `EpochAssist.dll` into the game's `Mods` folder.
3. Press F9 in game to open the menu.

## Build

Create `build.local.env` next to `build.sh`:

```sh
LE_ML='C:\path\to\Last Epoch\MelonLoader'
NUGET_SOURCE='https://api.nuget.org/v3/index.json'
```

Then run `./build.sh` (Debug), `./build.sh deploy` (Debug, copied to `Mods/`), or `./build.sh release` (writes `dist/EpochAssist-v<version>.zip`).

## License

MIT
