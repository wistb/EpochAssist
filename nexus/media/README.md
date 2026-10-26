# Page images

Feature images, used by the README and the Nexus description. A new image adds a row here, a `{{media:<name>}}` line
under its feature in `../description.bbcode` (consecutive lines share one spoiler), and an image in the collapsed block
under the feature's line in the README:

```markdown
  <details><summary>Screenshot</summary>

  ![<alt text>](nexus/media/<name>.png)

  </details>
```

On Nexus, `tools/fill-nexus.sh` turns `{{media:<name>}}` into an image linked from GitHub (at the release tag, or at the
exact commit for a page-only or dry run) and leaves the line out while `<name>.png` doesn't exist. The links only load
for visitors while the repository is public.

Take them in a dev build, offline, in the default look (Carbonfox, every accent on Theme, Pretendard Regular), with no
character or account name visible, at UI scale 2 (`set UiScale 2` first, `set UiScale 1.25` after, the default): the text is drawn at that size, so the images stay sharp when GitHub or Nexus scales them
down, where a 1x capture blurs when scaled up. `matte` gives the menu or HUD a transparent background, trimmed to what
is drawn (`tools/shot.sh …`; shots land in `UserData/EpochAssist/dev/shots/`):

| Name | Shows | Take it with |
|---|---|---|
| `autocast` | Combat › Autocast › Controls, with Slots open in the tree | `menu open combat/autocast/controls`, `wait 90`, `shot menu autocast matte` |
| `hud` | The mini HUD with its quick toggles open | `hud panel open`, `wait 30`, `shot hud hud matte`, `hud panel close` |
| `profiles` | Settings › Profiles | `run menu.profiles`, `wait 90`, `shot menu profiles matte` |
| `appearance` | Settings › Interface › Appearance | `menu open settings 1`, `wait 90`, `shot menu appearance matte` |
| `keybinds` | Settings › Interface › Keybinds | `menu open settings 2`, `wait 90`, `shot menu keybinds matte` |
| `diagnostics` | Settings › Diagnostics › Overview (a dev build's summary says "dev build"; take it a while after a reload, whose hitch shows as the worst frame time) | `run diagnostics.open`, `wait 900`, `shot menu diagnostics matte` |
| `settings-hud` | Settings › Mini HUD, second image under Mini HUD (the HUD preview can poke a few pixels past the menu's left edge; trim the image to the menu's 1600 px) | `menu open settings 0`, `wait 90`, `shot menu settings-hud matte` |

A dev build shows a Dev tab in the menu; players don't have it. Not shipped in the zip. The Nexus gallery images and the page header and banner (`../banner-1300x372.png`,
`../header-1920x1080.png`) are uploaded by hand.
