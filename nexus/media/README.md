# Page images

Feature images, used by the README and the Nexus description. A new image adds a row here, a `{{media:<name>}}` line
under its feature in `../description.bbcode` (consecutive lines share one spoiler), and an image in the collapsed block
under the feature's line in the README:

```markdown
  <details><summary>Screenshot</summary>

  ![<alt text>](nexus/media/<name>.png)

  </details>
```

A `{{image:<name>}}` line shows the image in place instead of in a spoiler (the page's lead image). On Nexus, `tools/fill-nexus.sh` turns `{{media:<name>}}` into an image linked from GitHub (at the release tag, or at the
exact commit for a page-only or dry run) and leaves the line out while `<name>.png` doesn't exist. The links only load
for visitors while the repository is public.

Take them in a dev build, offline, in the default look (Carbonfox, every accent on Theme, Pretendard Regular), with no
character or account name visible, at UI scale 2 (`set UiScale 2` first, `set UiScale 1.25` after, the default): the text is drawn at that size, so the images stay sharp when GitHub or Nexus scales them
down, where a 1x capture blurs when scaled up. `matte` gives the menu or HUD a transparent background, trimmed to what
is drawn (`tools/shot.sh …`; shots land in `UserData/EpochAssist/dev/shots/`):

| Name | Shows | Take it with |
|---|---|---|
| `logo` | The nebula wordmark, the page's title (not a screenshot) | a copy of `src/Ui/Theme/Assets/logo-wordmark@4x.png` |
| `macros` | Macros › Library with Slots, Potions and a filled-out example set (made for the shot, removed after; slots 1 and 5 ticked) | `menu open macros/library/library`, `wait 90`, `shot menu macros matte` |
| `macro-editor` | One macro's editor with an all-of tree, a nested any-of group and live values, and its cast step open on its Target rows with a live pick (near a pack; scrolled to the bottom, then one notch up) | `macro edit <set>/<macro>`, `wait 90`, `shot menu macro-editor matte` |
| `shared-target` | Macros › Library › Slots › Shared Conditions: the enemy gate and the Shared target, set for the shot to the rarest enemy within 15 m (near a pack so its live pick shows; put back after) | `set SlotTargets {"v":1,"shared":{"pick":"rarest","range":15,"else":"cursor"}}`, `macro edit slots/every`, `wait 40`, `shot menu shared-target matte`, `set SlotTargets {"v":1}` |
| `hud` | The mini HUD with its quick toggles open (slots 1 and 3 ticked for the shot) | `run macros.slot.1`, `run macros.slot.3`, `hud panel open`, `wait 30`, `shot hud hud matte`, `hud panel close`, the two slots off again |
| `profiles` | Config › General › Profiles | `menu open combat/general/profiles`, `wait 90`, `shot menu profiles matte` |
| `appearance` | Config › General › Interface › Appearance | `menu open combat/general/interface/appearance`, `wait 90`, `shot menu appearance matte` |
| `diagnostics` | Config › General › Diagnostics › Overview, offline (the summary names the mode; a dev build's says "dev build"; take it a while after a reload, whose hitch shows as the worst frame time; an unfocused game runs few frames a second, so the wait takes longer) | `menu open combat/general/diagnostics/overview`, `wait 900`, `shot menu diagnostics matte` |
| `settings-hud` | Config › General › Interface › Mini HUD, second image under Mini HUD (the HUD preview pokes 4 px past the menu's left and top edges; trim the image to the menu's 1600 × 1360) | `menu open combat/general/interface/mini-hud`, `wait 90`, `shot menu settings-hud matte` |

A dev build lists a Dev section in the Config tree; players don't have it: `menu devpages off` hides it for the shots (`menu devpages on` after). Not shipped in the zip. The Nexus gallery images and the page header and banner (`../banner-1300x372.png`,
`../header-1920x1080.png`) are uploaded by hand.
