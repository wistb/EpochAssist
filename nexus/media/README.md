# Page images

Feature images (PNG, or GIF or WebP for an animation), used by the README and the Nexus description. A new image adds a row here, a `{{media:<name>}}` line
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
down, where a 1x capture blurs when scaled up. `matte` gives the menu, the HUD or game UI a transparent background,
trimmed to what is drawn; `shot ui <name>` crops to one game window or tooltip, frame included (`tools/shot.sh …`;
shots land in `UserData/EpochAssist/dev/shots/`).

Every feature's images stay in its spoiler; only the lead image shows in place. At most three images per feature, best
first: the feature at work in the game before its settings pages. Status: **new** not taken yet, **retake** taken
before the menu's 2026-10-27 changes (nested Config tree, tree and fold buttons, footer switches, "status: off" tags),
**later** waits for unfinished work, **current** up to date. Menu routes match by prefix; `menu open` says when one
doesn't. The mini HUD stays up with the menu open and overlaps its top-left corner: `set HudEnabled false` for menu shots
(`true` after). For a Config page, fold the tree with the button beside search and open only the page's own section, so
the page shows highlighted.

| Name | Status | Shows | Take it with |
|---|---|---|---|
| `logo` | current | The nebula wordmark, the page's title (not a screenshot) | a copy of `src/Ui/Theme/Assets/logo-wordmark@4x.png` |
| `macros` | retake | Macros › Library with Slots, Potions and a filled-out example set (made for the shot, removed after; slots 1 and 5 ticked) | `menu open macros/library`, `wait 90`, `shot menu macros matte` |
| `macro-editor` | retake | One macro's editor with an all-of tree, a nested any-of group and live values, and its cast step open on its Target rows with a live pick (near a pack; scrolled to the bottom, then one notch up) | `macro edit <set>/<macro>`, `wait 90`, `shot menu macro-editor matte` |
| `enemy-gate` | new | Config › Macros › Enemies: gate on, range and weight sliders, the Now readout and enemy weights (near a pack so Now counts); replaces `shared-target`, whose page no longer sets the gate | `menu open combat/macros/enemies`, `wait 90`, `shot menu enemy-gate matte` |
| `hud` | current | The mini HUD with its quick toggles open (slots 1 and 3 ticked for the shot) | `run macros.slot.1`, `run macros.slot.3`, `hud panel open`, `wait 30`, `shot hud hud matte`, `hud panel close`, the two slots off again |
| `planner-passives` | current | The passive tree on a mastery tab where the build shows every mark state: matches, partial with the next point labelled, to place, not in build (Marksman tab of the Shadow Cascade Rogue, stage 1) | `planner open passive`, `wait 60`, `planner tab <n>`, `wait 40`, `shot ui PassiveTree2021 planner-passives matte`, `planner open close` |
| `planner-skills` | current | One skill's tree with the build's marks, a skill the stage has part-spent (Shift) | `planner open tree <slot>`, `wait 60`, `shot ui "Skills Panel" planner-skills matte`, `planner open close` |
| `planner-weaver` | current | The weaver tree on its own, nodes, lines and item sockets with the build's marks, over the tree's real background (`oYEkqKqJ`-style socket build, one socket filled) | layers composed: `shot ui "Weaver Tree Holder/Weaver Tree" matte hide:Node_,ItemSlot,Connections` (background) and `... matte alone hide:Background,Ornaments` (nodes), lined up against a plain `... matte` shot |
| `planner-blessings` | current | The Blessings tab's active blessings in the inventory panel's own frame, rings by state (matches, partial, not in build) and one slot selected with its border in the state colour | `shot ui InventoryPanel matte hide:Framing,Close_Button` (content) and `shot ui InventoryPanel matte hide:background,ObjectPool,InventoryHolder,BlessingsPanel,CosmeticsPanel,Idol,Tutorial,headerFrame,Container_,Close_,Header` (frame), composed: frame's left half mirrored, cut above the timeline dropdown, joined on the frame's bottom |
| `planner-tab` | current | Planner › Library with the build in use opened, Checklist view: stage ◀ ▶, Checklist/Guide/Loot filter switch, "Open planner page" with its link mark, skill bar, Loot filter row, checklist trees folded with done out of planned (Flay Mana Lich, stage 1) | `menu devpages off`, `set UiScale 2`, `set HudEnabled false`; another page then `menu open planner/library` (resets the scroll), `planner guide view checklist <build>`, `cursor` off the menu, `wait 150`, `shot menu planner-tab matte` |
| `planner-checklist` | current | Same, scrolled to an unfolded tree's next steps in two columns, then Gear and Idols in columns with states and planned items | as `planner-tab`, unfold a tree (its arrow), drag the editor's scroll thumb down, `cursor` off the menu, `wait 60`, `shot menu planner-checklist matte` |
| `planner-guide` | current | Same build, Guide view at its top: Watch showcase, strengths and weaknesses, intro with game icons, a callout | as `planner-tab`, Guide instead of Checklist, `shot menu planner-guide matte` |
| `planner-guide-tips` | current | Guide view scrolled to Skill Tips: bullets with skill and item icons and the guide's own colours | Guide view, scrolled to Skill Tips, `shot menu planner-guide-tips matte`; afterwards `set UiScale 1.25`, `set HudEnabled true`, `menu devpages on` |
| `planner-filter` | current | Same build, Loot filter view at its top: Filter for, Base, Strictness, Name, then Utility Rules (switches, Fewer shards than, shatter tier) and the first hide option (Flay Mana Lich, stage 1, defaults) | as `planner-tab`, `planner filter view`, `cursor` off the menu, `wait 120`, `shot menu planner-filter matte` |
| `planner-filter-rules` | current | Same view scrolled to the planner uniques with game icons (from the third unique), then Rules: count line and rules 1–5 with looks and a capitalized, underlined SHOW | as `planner-filter`, `planner guide scroll row:filter.unique.<third unique id>`, `wait 400`, `shot menu planner-filter-rules matte`; afterwards `set UiScale 1.25`, `set HudEnabled true`, `menu devpages on` |
| `planner-gear` | later | The inventory with planned item icons and one planned item's game tooltip, once planner gear lands | `shot ui tooltip planner-gear matte` with the icon hovered, or the inventory window for the icons |
| `profiles` | current | Config › General › Profiles | `menu open combat/general/profiles`, `wait 90`, `shot menu profiles matte` |
| `appearance` | current | Config › General › Appearance | `menu open combat/general/appearance`, `wait 90`, `shot menu appearance matte` |
| `diagnostics` | current | Config › Diagnostics › Overview, offline (the summary names the mode; a dev build's says "dev build"; take it a while after a reload, whose hitch shows as the worst frame time; an unfocused game runs few frames a second, so the wait takes longer) | `menu open combat/diagnostics/overview`, `wait 900`, `shot menu diagnostics matte` |
| `settings-hud` | current | Config › General › Mini HUD, second image under Mini HUD, now with the Sections order list (Session figures among them; the mini HUD shows the switch on: drag the menu clear of the HUD instead of hiding it) | `menu open combat/general/mini-hud`, `wait 90`, `shot menu settings-hud matte` |
| `interact` | current | Config › Interact › Proximity Interact, every kind on | `menu open combat/interact/proximity-interact`, `wait 90`, `shot menu interact matte` |
| `interact-world` | new | In an echo: reach rings around a shrine and loot, the mini HUD's in-reach count; first image under Proximity Interact | `shot full interact-world`, cropped to the rings and the HUD |
| `map-overlay` | current | The overlay map in an echo with Map Reveal on (Slate look, thick outline, icons shown), menu closed, cropped to the map with a margin of the world and none of the game's UI | a screenshot with Tab open in an echo, cropped to the map |
| `map-look` | current | Config › Map › Look, the Slate style | `menu open combat/map/look`, `wait 90`, `shot menu map-look matte` |
| `map-icons` | current | Config › Map › Icons, a player icon picked | `menu open combat/map/icons`, `wait 90`, `shot menu map-icons matte` |
| `stash-page` | current | Stash › Layouts on a player copy of Epoch Assist at full budget, stash window open: own stash with Keep/Reuse and "· N elsewhere" on the left, the preset with renamed, new and changed marks and unique-list Edit buttons on the right, budget field, gold, item switches | `stash open`, `set UiScale 2`, `menu open stash/layouts`, `cursor` off the menu, `wait 90`, `shot menu stash-page matte`; afterwards `set UiScale 1.25` |
| `stash-apply` | current | Animated WebP: the pointer on Apply all clicks it, the summary card shows, Run all steps is clicked and the full Epoch Assist preset (146 steps) builds in about 12 seconds beside Quick View; transparent background, 50 fps, 17.5 s, about 2.9 MB | stash snapshot first; region recorded with Windows ffmpeg (gdigrab, 60 fps, cursor on) while input.ps1 clicks; frames cut out with a mask made from `shot menu` and `shot ui "Vertical Look Stash Tabs"` mattes; `img2webp -loop 0 -lossy -q 75 -m 2 -d 20 <frames>`; snapshot restored after |
| `session-dashboard` | current | Session › Current session with Sample data: session time, focus chips (deaths off), the eight focus tiles and the favour chart | `set UiScale 2`, `set HudEnabled false`, `sessions sample quiet` (the sample without its dev switch and banner), `menu open session/current-session`, `wait 90`, `shot menu session-dashboard matte` |
| `session-tables` | current | Same page scrolled to the Factions and Echoes tables: rank bars, gains in faction and game colours, echo names, rewards, corruption | as `session-dashboard`, then drag the page's scroll thumb down until Factions is at the top (`cursor`/`button down`/`button up` on the thumb at UI scale 2), `wait 30`, `shot menu session-tables matte` |
| `session-past` | current | Session › Past sessions filtered to the filled character: favour trend columns with the average line, list lines with per-hour figures and best marks | `sessions fill 14 30 6 realistic` (offline slot 9), `menu open session/past-sessions`, Character set to offline slot 9, `wait 90`, `shot menu session-past matte`; afterwards `sessions delete` each fill session, `sessions sample off`, `set UiScale 1.25`, `set HudEnabled true` |
| `session-hud` | current | The session figures alone as a card (Macros pill hidden), Sample data: time, level bar, XP, drops (total and rate), echoes, favour, with pace graphs in game colours, labels on, menu closed | `set UiScale 2`, `sessions sample quiet`, `set HudShowmacros false`, `set SessionHudShow true`, `set SessionHudLook 0`, `set SessionHudLevel 1`, `set SessionHudDrops 3`, `set SessionHudEchoes 2`, `set SessionHudFavour 2`, `set SessionHudGold 0`, `menu close`, `cursor` off the HUD, `wait 90`, `shot hud session-hud matte` |
| `session-hud-detail` | current | The same figures as tiles with the XP tile hovered: its expanded view below (larger graph with peak, three rates) | as `session-hud`, then `set SessionHudLook 2`, `wait 60`, `see ours mini` for the tiles' place, `cursor` on the XP tile (the real mouse must stay off the HUD), `wait 30`, `shot rect` around the tiles and the view `session-hud-detail matte` (matte works on a rect holding only the HUD) |
| `settings-session-hud` | current | Config › Sessions › Mini HUD with every figure at its default (only the switch is on), under the Session images | figures back to defaults (`set SessionHudXp 2`, `Gold 2`, `Drops 0`, `Echoes 1`, `Level 0`, `Favour 0`), the menu dragged clear of the HUD by its title bar, `menu open combat/sessions/mini-hud`, `wait 90`, `shot menu settings-session-hud matte`; afterwards `sessions sample off`, the player's own settings back, `set UiScale 1.25` |

`shared-target.png` goes when `enemy-gate` replaces it. A dev build lists a Dev section in the Config tree; players don't have it: `menu devpages off` hides it for the shots (`menu devpages on` after). Not shipped in the zip. The Nexus gallery images and the page header and banner (`../banner-1300x372.png`,
`../header-1920x1080.png`) are uploaded by hand.
