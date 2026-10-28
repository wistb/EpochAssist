using EpochAssist.Core;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;
using S = EpochAssist.Features.PlannerSettings;

namespace EpochAssist.Features;

internal static partial class Planner
{
    // An icon whose planned item this game version lacks: grey, not a state colour.
    private static readonly Color UnknownColour = new(0.541f, 0.541f, 0.541f, 1f);

    /// <summary>The paper doll slots' names as the tab and the mini HUD say them.</summary>
    public static string SlotName(string slot) => slot switch
    {
        "helmet" => "Helmet", "armor" => "Body armor", "weapon" => "Weapon", "offhand" => "Off-hand", "amulet" => "Amulet",
        "leftRing" => "Left ring", "rightRing" => "Right ring", "belt" => "Belt", "gloves" => "Gloves", "boots" => "Boots",
        "relic" => "Relic", "altar" => "Idol altar", _ => slot,
    };

    // ---- icons on the inventory ----

    // The icons, what they were made from, and the elements they sit on (found once per inventory open).
    private static readonly List<PlannerMark> s_gearMarks = new();
    private static readonly List<GameObject> s_cellHolders = new();
    private static readonly Dictionary<IntPtr, Transform> s_slotElements = new();
    private static GearGuidance? s_gearMarked;
    private static (float Size, string Corner, bool Matches, bool Idols, int Shown, uint Colours, int Icon) s_gearLook;

    // The icon settings' state, so a change redraws the icons.
    private static int IconKey() => HashCode.Combine(S.InventoryIconGlyph.Value, S.InventoryIconBackground.Value, S.InventoryIconOpacity.Value,
        S.InventoryIconOffsetX.Value, S.InventoryIconOffsetY.Value, S.InventoryIdolIconSize.Value, S.InventoryIdolOffsetX.Value, S.InventoryIdolOffsetY.Value)
        ^ (S.InventoryAltar.Value ? 1 : 0);
    private static int s_gearPass;
    private static int s_blessingSlots = -1, s_blessingList;
    private static float s_blessingCheck;

    // The Blessings tab's timeline slots, found once (hidden ones too) and kept while they live: an object search scans
    // the whole scene. Looked for again at most every few seconds while none are known.
    private static readonly List<InventoryBlessingSlotUI> s_blessingUis = new();
    // The Discovered Blessings list's holder, whose entries the game fills for the selected timeline.
    private static Transform? s_discovered;
    private const float BlessingFindSeconds = 5f;

    private static List<InventoryBlessingSlotUI> BlessingUis()
    {
        if (s_blessingUis.Count > 0 && s_blessingUis.TrueForAll(ui => Game.Alive(ui))) return s_blessingUis;
        s_blessingUis.Clear();
        s_selectedBorders.Clear();
        if (Clock.Now < s_blessingCheck) return s_blessingUis;
        s_blessingCheck = Clock.Now + BlessingFindSeconds;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<InventoryBlessingSlotUI>(), true))
        {
            var ui = obj.TryCast<InventoryBlessingSlotUI>();
            if (!Game.Alive(ui)) continue;
            if (ui!.blessingUIContainer?.Container != null) s_blessingUis.Add(ui);
            else if (ui.transform.parent is { name: DiscoveredHolderName } holder) s_discovered = holder;
        }
        return s_blessingUis;
    }

    // Each timeline slot's selected border (shown or not), found once per slot: a search of its row's images.
    private static readonly Dictionary<IntPtr, Transform?> s_selectedBorders = new();

    private static Transform? SelectedBorder(InventoryBlessingSlotUI ui, Transform row)
    {
        if (s_selectedBorders.TryGetValue(ui.Pointer, out var known) && (known == null || Game.Alive(known))) return known;
        ui.transform.TryCast<RectTransform>()!.GetWorldCorners(s_altarCorners);
        var centre = (s_altarCorners[0] + s_altarCorners[2]) / 2f;
        Transform? found = null;
        foreach (var image in row.GetComponentsInChildren<Image>(true))
            if (Game.Alive(image) && image.name == BlessingSelectedName && Covers(image.rectTransform, centre)) { found = image.transform; break; }
        s_selectedBorders[ui.Pointer] = found;
        return found;
    }

    private const string DiscoveredHolderName = "DiscoveredBlessings", DiscoveredPath = "TimelineBlessingsContainer/DiscoveredBlessings";

    // The timeline (from 0) whose slot is selected for the list, or -1, and the list's entries mixed into one number:
    // either changing marks the tab again.
    private static int BlessingListKey(out int selected)
    {
        selected = -1;
        if (!S.BlessingMarks.Value) return 0;
        foreach (var ui in BlessingUis())
        {
            if (!ui.gameObject.activeInHierarchy || ui.transform.parent is not { } row) continue;
            if (SelectedBorder(ui, row) is { } chosen && chosen.gameObject.activeInHierarchy)
            {
                selected = ui.blessingUIContainer?.Container?.TryCast<BlessingContainer>() is { } box ? (int)box.TimelineID - 1 : -1;
                break;
            }
        }
        int h = selected;
        // Beside the slots on the tab: looked for up a slot's parents while it isn't known.
        if (!Game.Alive(s_discovered) && BlessingUis().Count > 0)
            for (var at = BlessingUis()[0].transform; at != null && !Game.Alive(s_discovered); at = at.parent)
                s_discovered = at.Find(DiscoveredPath);
        if (Game.Alive(s_discovered) && s_discovered!.gameObject.activeInHierarchy)
            for (int i = 0; i < s_discovered.childCount; i++)
            {
                var child = s_discovered.GetChild(i);
                if (!child.gameObject.activeSelf || child.GetComponent<InventoryBlessingSlotUI>() is not { } entry || entry.itemData is not { } data) continue;
                h = h * 31 + data.subType;
            }
        return h;
    }

    /// <summary>The selected timeline and the list as the marks see them, for the dev bridge.</summary>
    internal static string BlessingListText
    {
        get
        {
            int key = BlessingListKey(out int selected);
            return $"selected timeline {selected}, list {(Game.Alive(s_discovered) ? $"{s_discovered!.name} {s_discovered.childCount} entries, shown {s_discovered.gameObject.activeInHierarchy}" : "not found")}, key {key}";
        }
    }

    // How many timeline slots the Blessings tab shows now.
    private static int BlessingSlotsShown()
    {
        if (!S.BlessingMarks.Value) return 0;
        int n = 0;
        foreach (var ui in BlessingUis())
            if (ui.gameObject.activeInHierarchy) n++;
        return n;
    }
    // The inventory canvas's camera (null for an overlay canvas), found once per marking pass for the hover's hit test.
    private static Camera? s_gearCamera;

    /// <summary>
    /// While the inventory is open, one icon per planned slot and idol at the chosen corner, in its state colour; made on
    /// open, recoloured when the guidance changes, cleared on close or when switched off.
    /// </summary>
    private static void MarkGear(Run run)
    {
        bool on = S.Enabled.Value && (S.InventoryMarks.Value || S.BlessingMarks.Value) && run.InventoryOpen && run.Gear != null && run.Feature.Has(InventoryUiGroup);
        if (!on)
        {
            ClearGearMarks();
            return;
        }
        var look = (S.InventoryIconSize.Value, S.InventoryIconCorner.Value, S.InventoryMatches.Value, S.InventoryIdols.Value,
            (S.ShowMatches.Value ? 1 : 0) | (S.ShowPartial.Value ? 2 : 0) | (S.ShowToPlace.Value ? 4 : 0) | (S.ShowOffPlan.Value ? 8 : 0)
            | (S.BlessingMarks.Value ? 16 : 0), ColourKey(), IconKey());
        // The Blessings tab draws its slots only while it's shown: its tab switch is caught by a count of them.
        int blessingSlots = BlessingSlotsShown();
        int blessingList = BlessingListKey(out int selectedTimeline);
        if (run.Gear == s_gearMarked && look == s_gearLook && blessingSlots == s_blessingSlots && blessingList == s_blessingList && s_gearMarks.TrueForAll(OnTop)) return;
        s_blessingSlots = blessingSlots;
        s_blessingList = blessingList;
        s_gearMarked = run.Gear;
        s_gearLook = look;
        int pass = ++s_gearPass;
        var corner = Corner(S.InventoryIconCorner.Value);
        float size = S.InventoryIconSize.Value;
        var gear = run.Gear!;
        s_gearCamera = null;
        if (SlotElement(SlotContainer("helmet")) is { } any && any.GetComponentInParent<Canvas>() is { } canvas && Game.Alive(canvas)
            && canvas.rootCanvas is { } root && root.renderMode != RenderMode.ScreenSpaceOverlay)
            s_gearCamera = root.worldCamera;
        foreach (var (slot, guide) in gear.Slots)
        {
            if (!S.InventoryMarks.Value || !IconShown(guide) || SlotElement(SlotContainer(slot)) is not { } element) continue;
            // The altar's own ring, in the panel's background art, takes the colour instead of a badge.
            if (slot == "altar")
            {
                if (!S.InventoryAltar.Value) continue;
                if (AltarRing(element, IconColour(guide)) is { } ring)
                {
                    ring.Hint = new MarkHint(HintKind.EquipSlot, AbilityId: slot);
                    KeepGear(ring, pass);
                }
                continue;
            }
            if (Badge(element, IconColour(guide), corner, size, new Vector2(S.InventoryIconOffsetX.Value, S.InventoryIconOffsetY.Value)) is not { } mark) continue;
            mark.Hint = new MarkHint(HintKind.EquipSlot, AbilityId: slot);
            KeepGear(mark, pass);
        }
        if (S.InventoryMarks.Value && S.InventoryIdols.Value && ItemContainersManager.Instance?.idols is { } idols && SlotElement(idols.TryCast<IItemContainer>()) is { } grid)
        {
            var gridRect = grid.TryCast<RectTransform>();
            float w = gridRect != null ? gridRect.rect.width / Math.Max(1, idols.size.x) : 0f, h = gridRect != null ? gridRect.rect.height / Math.Max(1, idols.size.y) : 0f;
            foreach (var ((x, y), guide) in gear.Idols)
            {
                if (!IconShown(guide) || w <= 0f) continue;
                var cell = CellHolder(grid, x, y, w, h);
                // Its own size and offset (idol cells are smaller than slots), and no bigger than a cell.
                var idolOffset = new Vector2(S.InventoryIdolOffsetX.Value, S.InventoryIdolOffsetY.Value);
                if (Badge(cell, IconColour(guide), corner, Math.Min(S.InventoryIdolIconSize.Value, Math.Min(w, h)), idolOffset) is not { } mark) continue;
                mark.Hint = new MarkHint(HintKind.IdolCell, Slot: y * IdolGridWidth + x);
                KeepGear(mark, pass);
            }
        }
        // The Blessings tab: each timeline's slot, by the timeline its container holds.
        if (S.BlessingMarks.Value && run.Feature.Has(BlessingUiGroup) && gear.Blessings.Count > 0)
        {
            foreach (var ui in BlessingUis())
            {
                if (!Game.Alive(ui) || !ui.gameObject.activeInHierarchy) continue;
                int timeline = ui.blessingUIContainer?.Container?.TryCast<BlessingContainer>() is { } box ? (int)box.TimelineID - 1 : -1;
                if (timeline < 0 || !gear.Blessings.TryGetValue(timeline, out var guide) || !IconShown(guide)) continue;
                // The slot's round frame takes the colour, as the altar's ring does.
                if (BlessingRing(ui, IconColour(guide)) is not { } mark) continue;
                mark.Hint = new MarkHint(HintKind.BlessingSlot, Slot: timeline);
                KeepGear(mark, pass);
            }
            // The list under the slots holds the selected timeline's blessings: the one the build plans there is marked,
            // in the matches colour once it's in the slot, else the to-place colour. No hint: the game's tooltip stays.
            if (selectedTimeline >= 0 && gear.Blessings.TryGetValue(selectedTimeline, out var wanted) && !wanted.Unknown && Game.Alive(s_discovered))
                for (int i = 0; i < s_discovered!.childCount; i++)
                {
                    var child = s_discovered.GetChild(i);
                    if (!child.gameObject.activeInHierarchy || child.GetComponent<InventoryBlessingSlotUI>() is not { } entry
                        || entry.itemData is not { } data || data.subType != wanted.Planned.SubType || data.itemType != wanted.Planned.ItemType) continue;
                    var colour = StateColour(wanted.State == GuidanceState.Matches ? GuidanceState.Matches : GuidanceState.ToPlace);
                    if (BlessingRing(entry, colour) is { } mark) KeepGear(mark, pass);
                }
        }
        for (int i = s_gearMarks.Count - 1; i >= 0; i--)
        {
            if (s_gearMarks[i].Pass == pass) continue;
            Marks.Clear(s_gearMarks[i]);
            s_gearMarks.RemoveAt(i);
        }
    }

    // An item put into a slot adds its element as the slot's last child, over the icon: then it's put back on top.
    private static bool OnTop(PlannerMark mark)
    {
        if (!Game.Alive(mark.Object)) return false;
        var t = mark.Object.transform;
        return t.parent == null || t.GetSiblingIndex() == t.parent.childCount - 1;
    }

    private static void KeepGear(PlannerMark mark, int pass)
    {
        if (mark.Pass != pass && !s_gearMarks.Contains(mark)) s_gearMarks.Add(mark);
        mark.Pass = pass;
    }

    private static bool IconShown(ItemGuide guide)
    {
        if (guide.Unknown) return true;
        if (guide.State == GuidanceState.Matches && !S.InventoryMatches.Value) return false;
        return guide.State switch
        {
            GuidanceState.Matches => S.ShowMatches.Value,
            GuidanceState.Partial => S.ShowPartial.Value,
            GuidanceState.ToPlace => S.ShowToPlace.Value,
            _ => S.ShowOffPlan.Value,
        };
    }

    private static Color IconColour(ItemGuide guide) => guide.Unknown ? UnknownColour : StateColour(guide.State);

    // The state colours change the icons too.
    private static uint ColourKey() =>
        (uint)HashCode.Combine(StateColour(GuidanceState.Matches), StateColour(GuidanceState.Partial), StateColour(GuidanceState.ToPlace), StateColour(GuidanceState.NotInBuild));

    private static Vector2 Corner(string corner) => corner switch
    {
        "topleft" => new Vector2(0f, 1f),
        "bottomright" => new Vector2(1f, 0f),
        "bottomleft" => new Vector2(0f, 0f),
        _ => new Vector2(1f, 1f),
    };

    private const string BadgeBack = "T_UI_Button_Small_Background_Circle_Default";
    // The idol altar's base type, and its ring in the idol panel's art: inner and outer radius as shares of the altar
    // slot's half width.
    private const byte AltarType = 41;
    private const float AltarRingInner = 0.92f, AltarRingOuter = 1.3f;

    // The ring is part of the idol panel's background image ("Background" under the altar's panel): that part of its art
    // takes the colour.
    private static PlannerMark? AltarRing(Transform altar, Color colour)
    {
        var panel = altar.parent;
        var holder = panel != null ? panel.Find("Border and Background") : null;
        var background = holder != null ? holder.Find("Background")?.GetComponent<Image>() : null;
        if (!Game.Alive(background) || altar.TryCast<RectTransform>() is not { } slot) return null;
        slot.GetWorldCorners(s_altarCorners);
        var bg = background!.rectTransform;
        Vector2 low = bg.InverseTransformPoint(s_altarCorners[0]), high = bg.InverseTransformPoint(s_altarCorners[2]);
        float half = (high.x - low.x) / 2f;
        return Marks.ArtRing(background, (low + high) / 2f, half * AltarRingInner, half * AltarRingOuter, colour);
    }

    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> s_altarCorners = new(4);

    // A blessing slot's round frame is a "BlessingBackgroundBlack" image beside the slot's element, at its place; the
    // band from BlessingRingInner of its radius outward is the ring, still seen round a locked slot's padlock. An
    // equipped blessing's roll gauge ("power-roll/power-roll-fill"; a Grand one's gold frame is drawn after it) is drawn over that band, so its ring goes just outside the gauge
    // instead, at BlessingGaugeRing of the gauge's radius, and the roll stays readable.
    private const float BlessingRingInner = 0.86f, BlessingGaugeRing = 1.12f;

    private static PlannerMark? BlessingRing(InventoryBlessingSlotUI ui, Color colour)
    {
        var row = ui.transform.parent;
        if (row == null || ui.transform.TryCast<RectTransform>() is not { } slot) return null;
        slot.GetWorldCorners(s_altarCorners);
        var centre = (s_altarCorners[0] + s_altarCorners[2]) / 2f;
        var images = row.GetComponentsInChildren<Image>(false);
        // The slot picked for the list below shows the game's selected border: the state takes that border's shape.
        foreach (var image in images)
            if (Game.Alive(image) && image.name == BlessingSelectedName && Covers(image.rectTransform, centre))
                return Marks.ArtCopy(image, colour);
        foreach (var image in images)
        {
            if (!Game.Alive(image) || image.name != BlessingGaugeName || image.transform.parent?.name != BlessingGaugeHolder || !Covers(image.rectTransform, centre)) continue;
            var gauge = image.rectTransform;
            return Marks.CircleRing(gauge, Mathf.Min(gauge.rect.width, gauge.rect.height) / 2f * BlessingGaugeRing, colour);
        }
        foreach (var image in images)
        {
            if (!Game.Alive(image) || image.name != BlessingFrameName || !Game.Alive(image.sprite)) continue;
            var rect = image.rectTransform;
            rect.GetWorldCorners(s_altarCorners);
            if (centre.x < s_altarCorners[0].x || centre.x > s_altarCorners[2].x || centre.y < s_altarCorners[0].y || centre.y > s_altarCorners[2].y) continue;
            float half = Mathf.Min(rect.rect.width, rect.rect.height) / 2f;
            return Marks.ArtRing(image, rect.rect.center, half * BlessingRingInner, half, colour, onTop: true);
        }
        return null;
    }

    private const string BlessingFrameName = "BlessingBackgroundBlack", BlessingGaugeName = "power-roll-fill", BlessingGaugeHolder = "power-roll",
        BlessingSelectedName = "SelectedBlessing";

    // Whether an element's drawn area holds a screen point.
    private static bool Covers(RectTransform rect, Vector3 point)
    {
        rect.GetWorldCorners(s_altarCorners);
        return point.x >= s_altarCorners[0].x && point.x <= s_altarCorners[2].x && point.y >= s_altarCorners[0].y && point.y <= s_altarCorners[2].y;
    }

    // Each icon choice's game sprite, and its share of the badge on the stone background.
    private static (string Sprite, float Scale) Glyph(string choice) => choice switch
    {
        "gem" => ("ProgressDot_Silver", 0.6f),
        "star" => ("Icon_Star", 0.62f),
        _ => ("quest-main-available", 0.62f),
    };

    /// <summary>The icon choice's game art for its dropdown, or null while the game hasn't loaded it.</summary>
    internal static (Texture Texture, Rect Uv)? GlyphPreview(string choice)
    {
        if (FindSprite(Glyph(choice).Sprite) is not { } sprite || !Game.Alive(sprite.texture)) return null;
        var area = sprite.textureRect;
        var texture = sprite.texture;
        return (texture, new Rect(area.x / texture.width, area.y / texture.height, area.width / texture.width, area.height / texture.height));
    }
    // Game sprites by name; looked up once each, once loaded.
    private static readonly Dictionary<string, Sprite?> s_sprites = new();

    private static Sprite? FindSprite(string name)
    {
        if (s_sprites.TryGetValue(name, out var known) && Game.Alive(known)) return known;
        Sprite? found = null;
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()))
        {
            var sprite = obj.TryCast<Sprite>();
            if (Game.Alive(sprite) && sprite!.name == name)
            {
                found = sprite;
                break;
            }
        }
        if (found != null) s_sprites[name] = found;
        return found;
    }

    // The icon: the chosen glyph from the game's art in the state colour, on the game's small round stone button
    // unless that's off, inside the element's corner. With the glyph missing, the plain square.
    private static PlannerMark? Badge(Transform element, Color colour, Vector2 corner, float size, Vector2 offset)
    {
        var (name, scale) = Glyph(S.InventoryIconGlyph.Value);
        bool back = S.InventoryIconBackground.Value;
        if (FindSprite(name) is { } glyph)
            return Marks.Badge(element, back ? FindSprite(BadgeBack) : null, glyph, back ? scale : 1f, colour, corner, size, offset, S.InventoryIconOpacity.Value);
        return Marks.Icon(element, colour, corner, size);
    }

    // A container's own element in the inventory, shown even when empty. The off-hand's is a "Ghost" child of the slot,
    // so its icon goes on the parent.
    private static Transform? SlotElement(IItemContainer? box)
    {
        if (box == null) return null;
        if (s_slotElements.TryGetValue(box.Pointer, out var known) && Game.Alive(known) && known.gameObject.activeInHierarchy) return known;
        // One object search (it scans the whole scene) finds every container's element at once, at most once a second.
        if (Clock.Now < s_slotSearch) return null;
        s_slotSearch = Clock.Now + 1f;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<ItemContainerUI>(), false))
        {
            var ui = obj.TryCast<ItemContainerUI>();
            if (!Game.Alive(ui) || !ui!.gameObject.activeInHierarchy || ui.Container == null) continue;
            var element = ui.transform;
            if (element.name.EndsWith("Ghost", StringComparison.Ordinal) && element.parent != null) element = element.parent;
            s_slotElements[ui.Container.Pointer] = element;
        }
        return s_slotElements.TryGetValue(box.Pointer, out var found) && Game.Alive(found) ? found : null;
    }

    private static float s_slotSearch;

    // The idol grid has no element per cell: a mod-owned holder the size of the cell carries its icon.
    private static Transform CellHolder(Transform grid, int x, int y, float w, float h) =>
        Holder(grid, $"Cell {x},{y}", new Vector2(0f, 1f), new Vector2(w, h), new Vector2(x * w, -y * h));

    // A mod-owned, invisible holder under an element: pivoted and anchored at one point, sized and placed from there.
    private static Transform Holder(Transform parent, string what, Vector2 at, Vector2 size, Vector2 offset)
    {
        string name = $"{Marks.MarkName}.{what}";
        var grid = parent;
        var found = grid.Find(name);
        GameObject go;
        if (found != null && Game.Alive(found))
        {
            go = found.gameObject;
        }
        else
        {
            go = new GameObject(name);
            go.AddComponent<RectTransform>().SetParent(grid, false);
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            // Invisible, so the reload sweep finds it by name.
            var clear = go.AddComponent<Image>();
            clear.raycastTarget = false;
            clear.color = Color.clear;
            s_cellHolders.Add(go);
        }
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = at;
        rect.sizeDelta = size;
        rect.anchoredPosition = offset;
        rect.SetAsLastSibling();
        return go.transform;
    }

    private static void ClearGearMarks()
    {
        // A tooltip the build view or a weaver socket opened is theirs to close.
        if (s_menuShown == null && s_gearHover?.Hint.Kind != HintKind.WeaverItem) CloseGearTip();
        if (s_gearMarks.Count == 0 && s_cellHolders.Count == 0) return;
        foreach (var mark in s_gearMarks) Marks.Clear(mark);
        s_gearMarks.Clear();
        foreach (var holder in s_cellHolders)
            if (Game.Alive(holder)) UnityEngine.Object.Destroy(holder);
        s_cellHolders.Clear();
        s_gearMarked = null;
    }

    // ---- the planned item as the game's tooltip ----

    private static PlannerMark? s_gearHover;
    private static float s_gearSince;
    // The made item shown as the game's tooltip, and the icon whose tooltip the game closed (not reopened until the
    // pointer leaves it).
    private static ItemDataUnpacked? s_tipItem;
    private static PlannerMark? s_tipDone;
    private static bool s_noteShown;

    /// <summary>
    /// While the inventory is open: the icon under the pointer, and once it has rested there for the tooltip delay, the
    /// planned item as the game's own tooltip with its comparison against the equipped item.
    /// </summary>
    private static void GearHover(Run run)
    {
        PlannerMark? mark = null;
        bool sockets = run.WeaverMarkedItems != null && run.WeaverMarks.Count > 0;
        if ((s_gearMarks.Count > 0 || sockets) && run.Gear != null)
        {
            var point = InputState.Pointer;
            var topLeft = new Vector2(point.x, Screen.height - point.y);
            bool overMenu = InputGate.Menu && InputGate.MenuHitTest?.Invoke(topLeft) == true;
            if (!overMenu && InputGate.HudHitTest?.Invoke(topLeft) != true)
            {
                foreach (var m in s_gearMarks)
                    if (Marks.Contains(m, point, s_gearCamera))
                    {
                        mark = m;
                        break;
                    }
                // The weaver page pans and zooms in its own canvas: its sockets are found by the game's raycast.
                if (mark == null && sockets && Marks.Under(point) is { Hint.Kind: HintKind.WeaverItem } socket) mark = socket;
            }
        }
        if (mark != s_gearHover)
        {
            CloseGearTip();
            if (s_noteShown)
            {
                s_noteShown = false;
                HudHint.Clear(run.Feature);
            }
            s_gearHover = mark;
            s_gearSince = Clock.Now;
            s_tipDone = null;
        }
        if (mark == null) return;
        if (s_tipItem != null)
        {
            // The game opened its own tooltip for something else: ours counts as closed.
            var open = Tooltips();
            if (open == null || open.ActiveItem?.Pointer != s_tipItem.Pointer)
            {
                ForgetTip(open);
                s_tipDone = mark;
            }
            else
            {
                HidePrompts();
            }
            return;
        }
        if (s_tipDone == mark || Clock.Now - s_gearSince < S.InventoryTooltipDelay.Value) return;
        s_tipDone = mark;
        if (GuideOf(run.Gear, mark.Hint) is not { } guide) return;
        // A socket's card follows the trees' hover switch; the inventory icons have their own.
        bool weaver = mark.Hint.Kind == HintKind.WeaverItem;
        bool card = weaver ? S.HoverHints.Value : S.InventoryHints.Value;
        if (guide.Unknown)
        {
            if (card)
            {
                HudHint.Show(run.Feature, CardTitle(run, guide.Planned), UnknownWords(guide), UnknownColour, S.InventoryHintPosition.Value == "tooltip");
                s_noteShown = true;
            }
            return;
        }
        // The mini HUD card says what the build wants here, as the trees' marks do; the game's tooltip shows the item.
        if (card)
        {
            // An item on the cursor is the one being weighed up: the card compares it instead of the worn one.
            var held = CarriedItem();
            var shown = held != null ? GuideAgainst(guide.Planned, held) : guide;
            var (state, text) = GearWords(run, shown, mark.Hint, held);
            HudHint.Show(run.Feature, CardTitle(run, guide.Planned) + (held != null ? " · carried" : string.Empty), Said(state, text), StateColour(state),
                S.InventoryHintPosition.Value == "tooltip");
            s_noteShown = true;
        }
        // The game draws no item tooltip while it carries an item on the cursor.
        if (!run.Feature.Has(ItemDataGroup) || !run.Feature.Has(ItemTooltipGroup) || Carrying()) return;
        // A socket has no equipped item for the game to compare with: the socketed one is given instead.
        OpenPlanned(guide.Planned, mark.Node.gameObject, weaver ? WornItem(guide.Planned, mark.Hint) : null);
        s_tipDone = null;
    }

    private static ItemGuide? GuideOf(GearGuidance? gear, MarkHint hint)
    {
        if (gear == null) return null;
        return hint.Kind switch
        {
            HintKind.EquipSlot when hint.AbilityId != null => SlotGuide(gear, hint.AbilityId),
            HintKind.IdolCell => gear.Idols.TryGetValue((hint.Slot % IdolGridWidth, hint.Slot / IdolGridWidth), out var idol) ? idol : null,
            HintKind.BlessingSlot => gear.Blessings.TryGetValue(hint.Slot, out var blessing) ? blessing : null,
            HintKind.WeaverItem => gear.WeaverItems.TryGetValue(hint.Slot, out var socketed) ? socketed : null,
            _ => null,
        };
    }

    // A gear icon's card: the planned item and what's off, then (with the list on) each planned affix: its tier met
    // (the worn tier, green), below it (orange) or missing (red) on the worn item; on an empty slot or another item, the
    // tier to look for.
    private static (GuidanceState State, string Text) GearWords(Run run, ItemGuide guide, MarkHint hint, ItemDataUnpacked? held = null)
    {
        string planned = PlannedName(guide.Planned);
        var worn = held ?? WornItem(guide.Planned, hint);
        string head = guide.State switch
        {
            GuidanceState.Matches => $"{planned}, as planned.",
            GuidanceState.Partial when guide.Planned.Place == PlannedPlace.Blessing && worn != null => $"{worn.FullName}; {planned} planned.",
            GuidanceState.Partial => $"{planned}; not all its affixes yet.",
            GuidanceState.ToPlace => $"{planned}.",
            _ when !S.InventoryHintWorn.Value || worn == null => $"The build wants {planned}.",
            _ => $"{worn.FullName}; the build wants {planned}.",
        };
        if (!S.InventoryHintMissing.Value || guide.Planned.Affixes.Count == 0) return (guide.State, head);
        bool compare = guide.State is GuidanceState.Matches or GuidanceState.Partial && worn != null;
        var text = new System.Text.StringBuilder(head);
        foreach (var affix in guide.Planned.Affixes)
        {
            text.Append('\n').Append(AffixName(affix.Id)).Append(": ");
            int want = affix.Tier + 1;
            if (!compare)
            {
                text.Append('T').Append(want);
                continue;
            }
            int have = WornTier(worn!, affix.Id);
            if (have < 0) text.Append(Coloured(GuidanceState.NotInBuild, "missing")).Append($" (T{want})");
            else if (have + 1 < want) text.Append(Coloured(GuidanceState.Partial, $"T{have + 1} of T{want}"));
            else text.Append(Coloured(GuidanceState.Matches, $"T{have + 1}")).Append(have + 1 > want ? $" (T{want} planned)" : string.Empty);
        }
        return (guide.State, text.ToString());
    }

    private static string Coloured(GuidanceState state, string text) => $"<color=#{ColorUtility.ToHtmlStringRGB(StateColour(state))}>{text}</color>";

    // The worn item's best tier of an affix (from 0), or -1 without it.
    private static int WornTier(ItemDataUnpacked worn, ushort id)
    {
        int best = -1;
        var affixes = worn.affixes;
        for (int i = 0; affixes != null && i < affixes.Count; i++)
            if (affixes[i] is { } affix && affix.affixId == id) best = Math.Max(best, affix.affixTier);
        return best;
    }

    // The planned item's own name: the made item's full name once it's made, else the base or unique.
    private static string PlannedName(PlannedItem item) =>
        Made(item) is { } made && !string.IsNullOrEmpty(made.FullName) ? made.FullName : item.Name.Length > 0 ? item.Name : "The planned item";

    private static string AffixName(ushort id)
    {
        try
        {
            return AffixList.get()?.GetAffixName(id) is { Length: > 0 } name ? name : $"affix {id}";
        }
        catch (Exception)
        {
            return $"affix {id}";
        }
    }

    // What the character has where an icon is: the slot's item, or the idol whose cell it is.
    private static ItemDataUnpacked? WornItem(PlannedItem planned, MarkHint hint)
    {
        IItemContainer? box = planned.Place switch
        {
            PlannedPlace.Slot => SlotContainer(hint.AbilityId ?? planned.Slot),
            PlannedPlace.Idol => ItemContainersManager.Instance?.idols?.TryCast<IItemContainer>(),
            PlannedPlace.WeaverNode => SocketContainer(planned.Socket),
            _ => null,
        };
        var content = box?.GetContent();
        for (int i = 0; content != null && i < content.Count; i++)
        {
            var entry = content[i];
            if (planned.Place == PlannedPlace.Idol && (entry?.Position.x != planned.X || entry.Position.y != planned.Y)) continue;
            if (entry?.data?.getAsUnpacked() is { } item) return item;
        }
        return null;
    }

    /// <summary>Why a grey icon's planned item can't be shown.</summary>
    public static string UnknownWords(ItemGuide guide) =>
        guide.Locked ? $"{PlaceName(guide.Planned)}: on a cell not unlocked yet." : $"{PlaceName(guide.Planned)}: not in this game version.";

    /// <summary>A planned item's place as the player reads it: its slot, "Idol", "Blessing" or "Weaver item".</summary>
    public static string PlaceName(PlannedItem item) => item.Place switch
    {
        PlannedPlace.Slot => SlotName(item.Slot),
        PlannedPlace.Idol => "Idol",
        PlannedPlace.WeaverNode => "Weaver item",
        _ => "Blessing",
    };

    // A hover card's title: the place, with a weaver socket's node name before it.
    private static string CardTitle(Run run, PlannedItem item) =>
        item.Place == PlannedPlace.WeaverNode ? $"{SocketName(run.Feature, item.Socket)} · {PlaceName(item)}" : PlaceName(item);

    // The game's tooltip manager has no static instance; it's found in the scene like the panels.
    private static TooltipItemManager? Tooltips()
    {
        var tooltips = Panels.Find<TooltipItemManager>();
        return Game.Alive(tooltips) ? tooltips : null;
    }

    /// <summary>
    /// Opens a planned item as the game's tooltip at an element (or at a screen point with none), with the game's own
    /// comparison against the equipped item, or against <paramref name="against"/> when given. The game's prompts are
    /// hidden while it shows, since they act on the real item under the pointer.
    /// </summary>
    private static bool OpenPlanned(PlannedItem planned, GameObject? opener, ItemDataUnpacked? against, Vector3? at = null, bool alone = false)
    {
        if (Tooltips() is not { } tooltips || Made(planned) is not { } item) return false;
        try
        {
            var size = opener != null ? opener.GetComponent<RectTransform>()?.rect.size ?? Vector2.zero : Vector2.zero;
            var position = at ?? (opener != null ? opener.transform.position : new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
            // Alone (beside the menu, with no game element behind it) it's shown as equipped: the game hides an inventory
            // tooltip that has no element, and there's nothing to compare it with there.
            var type = planned.Place == PlannedPlace.Blessing ? TooltipItemManager.SlotType.BLESSING
                : alone ? TooltipItemManager.SlotType.EQUIPPED
                : planned.Place == PlannedPlace.WeaverNode ? TooltipItemManager.SlotType.WEAVER_TREE_NODE
                : TooltipItemManager.SlotType.INVENTORY;
            tooltips.OpenItemTooltip(item, type, Vector2.zero, position, opener, size);
            if (against != null && tooltips.activeParameters is { } shown && tooltips.SetEquippedItemTextForComparison(against, shown))
                tooltips.ShowComparison(shown);
            s_tipItem = item;
            HidePrompts();
            return true;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:tooltip", $"Planner: the planned item's tooltip couldn't be opened: {e.Message}", 30);
            return false;
        }
    }

    /// <summary>Closes the planned item's tooltip if it's still the one shown, and gives the game back its prompts.</summary>
    private static void CloseGearTip()
    {
        if (s_tipItem == null) return;
        var tooltips = Tooltips();
        try
        {
            if (tooltips != null && tooltips.ActiveItem?.Pointer == s_tipItem.Pointer) tooltips.CloseTooltip();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:tooltip", $"Planner: the planned item's tooltip couldn't be closed: {e.Message}", 30);
        }
        ForgetTip(tooltips);
    }

    private static void ForgetTip(TooltipItemManager? tooltips)
    {
        s_tipItem = null;
        if (s_hiddenPrompts is { } prompts && Game.Alive(prompts)) prompts.SetActive(true);
        s_hiddenPrompts = null;
    }

    /// <summary>
    /// A previous load (a dev reload, which doesn't stop the old one) may have left a planned item's tooltip open and the
    /// game's prompts hidden: any item tooltip is closed (the game opens its own again on the next hover) and the prompts
    /// shown.
    /// </summary>
    private static void SweepTooltip()
    {
        try
        {
            if (UITooltipItem.instance?._itemTooltipPrompts is { } prompts && Game.Alive(prompts) && !prompts.gameObject.activeSelf)
                prompts.gameObject.SetActive(true);
            // Whichever manager shows it (the scene can hold more than one); only while one shows an item: before the
            // game's UI is up (a load at character select) ForceClose has no manager to close and throws.
            if (Tooltips() is { ActiveItem: not null }) TooltipItemManager.ForceClose();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:sweep", $"Planner: an earlier load's tooltip couldn't be closed: {e.Message}", 30);
        }
    }

    // The game's prompts under the item tooltip (equip, drop, move) act on the real item under the pointer, so they're
    // hidden while a planned item shows. The tooltip turns them on again as it builds, so this runs every frame.
    private static GameObject? s_hiddenPrompts;

    private static void HidePrompts()
    {
        if (!S.InventoryHidePrompts.Value)
        {
            if (s_hiddenPrompts is { } shown && Game.Alive(shown)) shown.SetActive(true);
            s_hiddenPrompts = null;
            return;
        }
        var prompts = UITooltipItem.instance?._itemTooltipPrompts;
        if (!Game.Alive(prompts)) return;
        var go = prompts!.gameObject;
        if (go.activeSelf) go.SetActive(false);
        s_hiddenPrompts = go;
    }

    // Whether the game carries an item on the cursor, and that item. Only read: the mod never picks up, drops or moves
    // anything.
    private static bool Carrying() => CarriedItem() != null;

    private static ItemDataUnpacked? CarriedItem()
    {
        var content = ItemContainersManager.Instance?.cursor?.TryCast<IItemContainer>()?.GetContent();
        return content != null && content.Count > 0 ? content[0]?.data?.getAsUnpacked() : null;
    }

    // ---- the compare hotkey ----

    /// <summary>
    /// The compare hotkey: with the game's item tooltip open, the planned item for the hovered item's slot shown against
    /// the hovered item. Nothing happens with no item tooltip open.
    /// </summary>
    private static void CompareHovered()
    {
        if (s_run is not { } run || !S.Enabled.Value || !run.Feature.Has(ItemTooltipGroup) || !run.Feature.Has(ItemDataGroup)) return;
        if (Tooltips() is not { } tooltips || tooltips.ActiveItem is not { } hovered) return;
        if (s_tipItem != null && hovered.Pointer == s_tipItem.Pointer) return;
        var guide = HoveredGuide(run.Gear, hovered.itemType);
        if (guide == null || guide.Unknown)
        {
            HudHint.Show(run.Feature, "Compare with build", guide == null ? "No planned item for this slot." : $"{PlaceName(guide.Planned)}: not in this game version.",
                UnknownColour, S.HintPosition.Value == "cursor");
            return;
        }
        // The game builds the comparison from the equipped item whatever it's handed, so the planned item shows against
        // the equipped one, where the hovered item's tooltip was. With no element to open it from, the game shows nothing.
        var opener = HoveredElement() ?? (guide.Planned.Place == PlannedPlace.Slot ? SlotElement(SlotContainer(guide.Planned.Slot))?.gameObject : null);
        if (opener == null) return;
        OpenPlanned(guide.Planned, opener, null);
    }

    private static readonly Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult> s_hoverHits = new();
    private static UnityEngine.EventSystems.PointerEventData? s_hoverPointer;

    // The inventory item element under the pointer (bag, stash or slot), or null.
    private static GameObject? HoveredElement()
    {
        var events = UnityEngine.EventSystems.EventSystem.current;
        if (!Game.Alive(events)) return null;
        if (s_hoverPointer == null || !Game.Alive(s_hoverPointer)) s_hoverPointer = new UnityEngine.EventSystems.PointerEventData(events);
        s_hoverPointer.position = InputState.Pointer;
        s_hoverHits.Clear();
        events!.RaycastAll(s_hoverPointer, s_hoverHits);
        for (int i = 0; i < s_hoverHits.Count && i < 4; i++)
        {
            var hit = s_hoverHits[i].gameObject;
            var ui = Game.Alive(hit) ? hit.GetComponentInParent<InventoryItemUI>() : null;
            if (Game.Alive(ui)) return ui!.gameObject;
        }
        return null;
    }

    // The slot an item of this base type goes in, and its planned item; of two planned rings, the one further from its
    // plan.
    private static ItemGuide? HoveredGuide(GearGuidance? gear, byte type)
    {
        if (gear == null) return null;
        string? slot = type switch
        {
            0 => "helmet", 1 => "armor", 2 => "belt", 3 => "boots", 4 => "gloves", 20 => "amulet", 22 => "relic", AltarType => "altar",
            17 or 18 or 19 => "offhand",
            >= 5 and <= 16 or 23 or 24 => "weapon",
            _ => null,
        };
        if (type == 21)
        {
            var left = SlotGuide(gear, "leftRing");
            var right = SlotGuide(gear, "rightRing");
            if (left == null || right == null) return left ?? right;
            return Score(left) <= Score(right) ? left : right;
        }
        return slot != null ? SlotGuide(gear, slot) : null;
    }
}
