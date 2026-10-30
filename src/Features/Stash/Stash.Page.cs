using EpochAssist.Core;
using EpochAssist.Ui;
using Il2Cpp;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Features;

internal static partial class Stash
{
    private const float Line = 24f, Gap = 6f, Indent = 18f, IconSize = 18f;

    // Rows are made once per read and preview; drawing only reads them.
    private sealed record LeftRow(bool Folder, int Id, string Name, int IconId, int ColourId, string Items, string? Warning, int Index)
    {
        /// <summary>A short tag after the name ("merged"), with its explanation.</summary>
        public string? Tag { get; init; }
        public string? TagTip { get; init; }
    }
    private sealed record RightRow(bool Folder, string Key, string Name, int IconId, int ColourId, string Mark, Color MarkColour, string Tip, int Index, Target? Target);

    private static List<LeftRow>? s_left;
    private static List<RightRow>? s_right;
    private static string s_summary = string.Empty;
    private static Vector2 s_leftScroll, s_rightScroll;
    private static string s_message = string.Empty;
    private static bool s_deleting, s_renaming;
    private static string s_renameText = string.Empty;
    private static Action? s_pending;
    private static string? s_editUniques;

    private static readonly string[] s_choiceNames = { "Keep", "Reuse", "Reuse if empty" };

    private static void ResetPage()
    {
        s_left = null;
        s_right = null;
        s_message = string.Empty;
        s_deleting = s_renaming = false;
        s_pending = null;
        s_editUniques = null;
        StopEditing();
    }

    private static void InvalidateRows()
    {
        s_left = null;
        s_right = null;
    }

    private static void MenuBlocksRegister() => MenuBlocks.Register(PageBlock, (rect, _) => DrawPage(rect), _ => BridgeLines());

    // ---- Rows ----

    internal static void BuildRows()
    {
        var snap = s_snap;
        var preview = s_preview;
        s_left = new List<LeftRow>();
        s_right = new List<RightRow>();
        if (snap == null || preview == null) return;
        var choices = CurrentChoices;
        var mergedTabs = CurrentPreset.AllTabs().Where(p => p.Tab.IsMerged).Select(p => (p.Tab, Rules: RulesOf(p.Tab))).ToList();
        int n = 0;
        foreach (var f in snap.Folders)
        {
            s_left.Add(new LeftRow(true, f.Id, f.Name, f.IconId, f.ColourId, string.Empty, null, n++));
            foreach (var t in snap.TabsIn(f.Id))
            {
                string? itemsWarning = t.ItemCount > 0 && choices.Of(t) != TabChoice.Keep ? "⚠ its items stay; new items follow the new rules" : null;
                // A tab that is one of the preset's merged tabs (same name and rules): it stands in for its group.
                var merged = mergedTabs.FirstOrDefault(m => m.Tab.Name == t.Name && t.Rules.Equals(m.Rules));
                s_left.Add(new LeftRow(false, t.Id, t.Name, t.IconId, t.ColourId, t.ItemCount == 1 ? "1 item" : $"{t.ItemCount} items", itemsWarning, n++)
                {
                    Tag = merged.Tab != null ? "merged" : null,
                    TagTip = merged.Tab != null ? MergedTip(merged.Tab) : null,
                });
            }
        }
        n = 0;
        var warnings = preview.Warnings.ToDictionary(w => w.TargetKey, w => w);
        foreach (var f in preview.Folders)
        {
            // A folder the plan leaves as it is: the player's own when kept, else simply not used.
            string folderMark = f.Keep ? f.ExistingId is { } own && snap.Folder(own) is { } fs && CurrentChoices.OfFolder(snap, fs) == TabChoice.Keep ? "yours" : "not used"
                : f.ExistingId == null ? "new folder"
                : f.Spare ? f.Configure ? $"spare ({snap.Folder(f.ExistingId.Value)?.Name} →)" : "spare"
                : f.Configure ? FolderChange(snap.Folder(f.ExistingId.Value), f) : string.Empty;
            s_right.Add(new RightRow(true, "f" + (f.ExistingId?.ToString() ?? f.Preset?.Key), f.Name, f.IconId, f.ColourId, folderMark, Theme.Secondary, string.Empty, n++, null));
            if (f.Keep)
                foreach (var t in snap.TabsIn(f.ExistingId!.Value).Where(t => preview.Targets.All(x => x.TabId != t.Id)))
                    s_right.Add(new RightRow(false, "t" + t.Id, t.Name, t.IconId, t.ColourId, "untouched (Keep)", Theme.Secondary, Rules.Describe(t.Rules), n++, null));
            else if (f.ExistingId is { } fid)
                foreach (var t in snap.TabsIn(fid).Where(t => preview.Targets.All(x => x.TabId != t.Id)))
                    s_right.Add(new RightRow(false, "t" + t.Id, t.Name, t.IconId, t.ColourId, choices.Of(t) == TabChoice.Keep ? "untouched (Keep)" : "untouched", Theme.Secondary, Rules.Describe(t.Rules), n++, null));
            foreach (var x in preview.Targets.Where(x => x.Folder == f).OrderBy(x => x.Index))
            {
                string name = x.Copy > 1 ? $"{x.Tab.Name}  {x.Copy} of {x.Of}" : x.Tab.Name;
                var (mark, colour) = TargetMark(snap, x, warnings.ContainsKey(x.Tab.Key));
                warnings.TryGetValue(x.Tab.Key, out var w);
                s_right.Add(new RightRow(false, x.Tab.Key + "#" + x.Copy, name, x.Tab.IconId, x.Tab.ColourId, mark, colour, TipFor(x, w), n++, x));
            }
        }
        int warn = preview.Warnings.Count;
        s_summary = $"Whole preset {preview.FullCost:N0} ({preview.FullTabs} tabs)"
            + (CurrentChoices.FullLayout ? $" · +{preview.ExtraTabs} tab{(preview.ExtraTabs == 1 ? string.Empty : "s")} for your items {preview.ExtraCost:N0}" : string.Empty)
            + $" · At this budget {preview.Cost:N0} ({preview.NewTabs} tabs)"
            + (warn > 0 ? $" · ⚠ {warn} order warning{(warn == 1 ? string.Empty : "s")}" : string.Empty);
    }

    // A planned tab's words beside it: bought (price), changed (from what) or done; a warning turns it red.
    private static (string Mark, Color Colour) TargetMark(StashSnapshot snap, Target x, bool warned)
    {
        string mark;
        Color colour;
        switch (x.Fill)
        {
            case Fill.Buy:
                mark = x.Copy > 1 ? $"new · {x.Price:N0} · for your items"
                    : x.Tab.IsMerged ? $"new · {x.Price:N0} · stands in for {x.Tab.MergeOf!.Count} tabs" : $"new · {x.Price:N0}";
                colour = Theme.Accent;
                break;
            case Fill.Reuse:
                var old = snap.Tab(x.TabId!.Value)!;
                mark = old.Name != x.Tab.Name ? $"changed ({old.Name} →)" : $"changed ({string.Join(", ", x.Changes)})";
                colour = Theme.AccentText;
                break;
            default:
                mark = x.Tab.IsMerged ? $"done · stands in for {x.Tab.MergeOf!.Count} tabs" : "done";
                colour = Theme.StateText(StatusState.Active);
                break;
        }
        if (warned)
        {
            mark = "⚠ " + mark;
            colour = Theme.Error;
        }
        return (mark, colour);
    }

    // What a reused folder changes: "renamed from …" when its name does, else what else does.
    private static string FolderChange(FolderSnap? old, PlanFolder f)
    {
        if (old == null) return string.Empty;
        if (old.Name != f.Name) return $"renamed from {old.Name}";
        var parts = new List<string>();
        if (old.IconId != f.IconId) parts.Add("icon");
        if (old.ColourId != f.ColourId) parts.Add("colour");
        return $"changed ({string.Join(", ", parts)})";
    }

    // What a merged tab stands in for, and what its Keep / Reuse choice does when the group splits.
    private static string MergedTip(PresetTab merged)
    {
        var names = CurrentPreset.AllTabs().Select(p => p.Tab).Where(t => merged.MergeOf!.Contains(t.Key)).Select(t => t.Name).ToList();
        return $"{merged.Name} stands in for {names.Count} tabs until the budget buys them all: {string.Join(", ", names)}.\n"
            + "Then Reuse makes it one of them (its items stay); Keep leaves it below them, catching what they don't.";
    }

    private static string TipFor(Target x, OrderWarning? warning)
    {
        var lines = new List<string> { Rules.Describe(x.Rules) };
        if (x.Tab.IsMerged) lines.Add(MergedTip(x.Tab));
        if (x.Tab.Uniques != null)
        {
            var ids = ByName(x.Rules.Uniques);
            lines.Add(x.Tab.Uniques.Kind == UniqueSource.Builds ? $"Uniques used by builds in your Planner library ({ids.Length})" : $"Uniques ({ids.Length})");
            if (ids.Length == 0) lines.Add("No uniques yet: import builds on the Planner tab, or add uniques here.");
            if (CurrentPreset.BuiltIn) lines.Add("Duplicate the preset to edit this list.");
            else lines.Add(string.Join(", ", ids.Take(30).Select(Rules.UniqueName)) + (ids.Length > 30 ? $" and {ids.Length - 30} more" : string.Empty));
        }
        if (x.Fill == Fill.Buy) lines.Add($"Buying it costs {x.Price:N0} gold.");
        if (warning != null) lines.Add($"⚠ {warning.TakenByName} takes {warning.Example} first.");
        return string.Join("\n", lines);
    }

    // ---- Drawing ----

    private static void DrawPage(Rect rect)
    {
        MarkDrawn();
        if (s_pending != null && Event.current.type == EventType.Layout)
        {
            var pending = s_pending;
            s_pending = null;
            pending();
        }
        if (s_snap == null) Refresh();
        if (s_left == null || s_right == null) BuildRows();
        GUILayout.BeginArea(rect);
        Popups.PushOrigin(rect.position);
        try
        {
            var r = new Rect(0f, 0f, rect.width, rect.height);
            if (s_snap == null || s_preview == null)
            {
                Paint(new Rect(0f, 8f, r.width, Line), "Load a character to read its stash.", Style(() => Theme.Hint, GUI.skin.label));
                return;
            }
            float y = TopBar(r);
            float bottomHeight = Line * 3 + Gap * 3;
            var mid = new Rect(0f, y, r.width, r.height - y - bottomHeight);
            float half = (mid.width - Gap) / 2f;
            DrawLeft(new Rect(mid.x, mid.y, half, mid.height));
            DrawRight(new Rect(mid.x + half + Gap, mid.y, half, mid.height));
            BottomBar(new Rect(0f, r.height - bottomHeight + Gap, r.width, bottomHeight - Gap));
        }
        finally
        {
            Popups.PopOrigin();
            GUILayout.EndArea();
        }
    }

    private static readonly Dictionary<GUIStyle, GUIStyle> s_middle = new();

    // The style with its text centred on the line, as the controls beside it are.
    private static GUIStyle Middle(GUIStyle style)
    {
        if (!s_middle.TryGetValue(style, out var middle))
            s_middle[style] = middle = new GUIStyle(style) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0) };
        return middle;
    }

    // The gold: the amount right-aligned, the coin after it, as the game's inventory has them.
    private static void GoldPanel(Rect r, long gold)
    {
        if (Event.current.type == EventType.Repaint)
        {
            float cx = r.xMax - 16f;
            if (GoldIcon() is { } coin) GUI.DrawTextureWithTexCoords(new Rect(cx, r.y + (r.height - 16f) / 2f, 16f, 16f), coin.Texture, coin.Uv);
            s_goldText ??= new GUIStyle(Middle(LabelStyle)) { alignment = TextAnchor.MiddleRight };
            Paint(new Rect(r.x, r.y, cx - 6f - r.x, r.height), gold.ToString("N0"), s_goldText);
        }
        Popups.Tip(r, "Your gold");
    }

    private static GUIStyle? s_goldText;

    private static (Texture Texture, Rect Uv)? s_gold;
    private static bool s_goldIsInventory;
    private static float s_goldNextLook;

    // The game's gold coin, as its inventory shows it beside the gold amount. Until the inventory has loaded (just after
    // a character loads) the item tooltip's gold stands in, and the inventory's is looked for again once a second.
    private static (Texture Texture, Rect Uv)? GoldIcon()
    {
        if (s_gold is { } cached && Game.Alive(cached.Texture) && (s_goldIsInventory || Time.unscaledTime < s_goldNextLook)) return cached;
        s_goldNextLook = Time.unscaledTime + 1f;
        if (!Has(IconsGroup)) return null;
        try
        {
            Sprite? sprite = null;
            foreach (var text in Resources.FindObjectsOfTypeAll<UIGoldText>())
            {
                if (!Game.Alive(text) || text.gameObject.scene.name == null) continue;
                var root = text.transform.parent != null ? text.transform.parent : text.transform;
                foreach (var image in root.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                    if (image.name == "Gold Coins Icon" && Game.Alive(image.sprite)) sprite = image.sprite;
                if (sprite != null) break;
            }
            s_goldIsInventory = sprite != null;
            if (sprite == null) sprite = UITooltipItem.instance?.goldIcon;
            if (!Game.Alive(sprite) || !Game.Alive(sprite!.texture)) return s_gold;
            var tex = sprite.texture;
            var tr = sprite.textureRect;
            s_gold = (tex, new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height));
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stash:gold", $"Stash: gold icon unreadable: {e.Message}", 30);
        }
        return s_gold;
    }

    private static GUIStyle? s_chip;

    // The tag style, centred, white on the chip's dark, with the padding of a small chip.
    private static GUIStyle Chip => s_chip ??= new GUIStyle(Style(() => Theme.Tag, GUI.skin.label))
    {
        alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 0), normal = { textColor = Color.white },
    };

    private static GUIStyle LabelStyle => Style(() => Theme.Label, GUI.skin.label);
    private static GUIStyle HintStyle => Style(() => Theme.Hint, GUI.skin.label);

    // What the page shows about items in the wrong tab for the preview, right-aligned at the end of the line given: a
    // switch and its word for each. Returns the width taken.
    private static float ItemSwitches(float right, float y)
    {
        var switches = new (Toggle Setting, string Word, string Key)[]
        {
            (StashSettings.ItemCounts, "Count", "stash.items.count"),
            (StashSettings.ItemOutlines, "Outline", "stash.items.outline"),
            (StashSettings.ItemLabels, "Label", "stash.items.label"),
        };
        float SwitchW((Toggle Setting, string Word, string Key) s) => Controls.SwitchWidth + 6f + TextCache.Width(s.Word, LabelStyle);
        const string Title = "Items in the wrong tab";
        float tw = TextCache.Width(Title, HintStyle) + 8f;
        float x = right - switches.Sum(SwitchW) - Gap * 2f * (switches.Length - 1);
        float start = x - tw;
        Paint(new Rect(start, y, tw, Line), Title, Middle(HintStyle));
        foreach (var sw in switches)
        {
            float w = SwitchW(sw);
            bool now = sw.Setting.Value;
            bool next = Controls.Switch(new Rect(x, y, w, Line), now, sw.Key, sw.Setting.Help);
            Paint(new Rect(x + Controls.SwitchWidth + 6f, y, w - Controls.SwitchWidth - 6f, Line), sw.Word, Middle(LabelStyle));
            if (next != now) sw.Setting.Value = next;
            x += w + Gap * 2f;
        }
        return right - start;
    }

    private static float TopBar(Rect r)
    {
        var presets = Presets.All;
        var current = CurrentPreset;
        float x = 0f;
        Paint(new Rect(x, 0f, 54f, Line), "Preset", LabelStyle);
        x += 58f;
        // The buttons are measured first; the dropdown (or the rename field) takes the width left, so the row fits.
        float Width(params string[] labels) => labels.Sum(l => Controls.ButtonWidth(l) + Gap);
        const string SaveLabel = "Save stash as preset";
        float buttons = s_renaming ? Width("Save name", "Cancel")
            : Line + Gap + Width(SaveLabel, "Duplicate") + (current.BuiltIn ? 0f : s_deleting ? Width("Yes", "No") + TextCache.Width("Delete?", LabelStyle) + Gap : Width("Delete"));
        // The row ends at the page's right edge, as the row under it does: the field fills the room, and past its widest
        // the buttons move right.
        float field = Mathf.Clamp(r.width - x - buttons - (s_renaming ? 0f : 4f - Gap), 140f, 480f);
        if (s_renaming)
        {
            s_renameText = GUI.TextField(new Rect(x, 0f, field, Line), s_renameText, 40);
            x += field + Gap;
            if (Button(ref x, "Save name", "stash.rename.save", true))
                s_pending = () =>
                {
                    current.Name = s_renameText.Trim().Length > 0 ? s_renameText.Trim() : current.Name;
                    Presets.Save(current);
                    s_renaming = false;
                    InvalidateRows();
                };
            if (Button(ref x, "Cancel", "stash.rename.cancel")) s_renaming = false;
        }
        else
        {
            var names = presets.Select(p => p.Name).ToList();
            int selected = Math.Max(0, presets.ToList().FindIndex(p => p.Id == current.Id));
            Dropdown.Draw(new Rect(x, 0f, field, Line), "stash.preset", names, selected, i => s_pending = () =>
            {
                CurrentChoices.Preset = presets[i].Id;
                s_deleting = false;
                StopEditing();
                ChoicesChanged();
            });
            x += field + 4f;
            if (GlyphButton(new Rect(x, 0f, Line, Line), Theme.Pencil, "stash.rename", current.BuiltIn ? "The built-in preset can't be renamed; duplicate it first." : "Rename", !current.BuiltIn))
            {
                s_renaming = true;
                s_renameText = current.Name;
                s_deleting = false;
            }
            x += Line + Gap;
            x = Mathf.Max(x, r.width - (buttons - Line - Gap) + Gap);
            if (Button(ref x, SaveLabel, "stash.save", tip: "A new preset with the stash's folders and tabs as they are"))
                s_pending = () =>
                {
                    if (s_snap != null && Capture(s_snap, $"My stash {DateTime.Now:yyyy-MM-dd HH.mm}") is { } p)
                    {
                        CurrentChoices.Preset = p.Id;
                        ChoicesChanged();
                        s_message = $"Saved as {p.Name}.";
                    }
                };
            if (Button(ref x, "Duplicate", "stash.copy", tip: "A copy of this preset you can change, named with the next number"))
                s_pending = () =>
                {
                    var copy = Presets.Copy(current, Presets.NextName(current.Name));
                    if (Presets.Save(copy))
                    {
                        CurrentChoices.Preset = copy.Id;
                        ChoicesChanged();
                    }
                };
            if (!current.BuiltIn)
            {
                if (s_deleting)
                {
                    // The dropdown shows which preset; the question stays short.
                    float qw = TextCache.Width("Delete?", LabelStyle);
                    Paint(new Rect(x, 0f, qw, Line), "Delete?", Middle(LabelStyle));
                    x += qw + Gap;
                    if (Button(ref x, "Yes", "stash.delete.yes", tip: $"Deletes {current.Name}", danger: true))
                        s_pending = () =>
                        {
                            Presets.Delete(current);
                            StopEditing();
                            CurrentChoices.Preset = Presets.BuiltInId;
                            s_deleting = false;
                            ChoicesChanged();
                        };
                    if (Button(ref x, "No", "stash.delete.no")) s_deleting = false;
                }
                else if (Button(ref x, "Delete", "stash.delete")) s_deleting = true;
            }
        }
        float y = Line + Gap;
        // Tabs per section: one, or enough to hold what the stash already has for each section.
        bool canFull = Has(TestGroup);
        string[] options = { "Single", "Fit items" };
        float ox = r.width + Gap;
        for (int i = options.Length - 1; i >= 0; i--) ox -= Controls.ButtonWidth(options[i]) + Gap;
        float labelW = TextCache.Width("Tabs per section", LabelStyle) + 8f;
        float rightW = r.width - ox + labelW;
        Paint(new Rect(ox - labelW, y, labelW, Line), "Tabs per section", LabelStyle);
        float bx = ox;
        for (int i = 0; i < options.Length; i++)
        {
            bool on = (i == 1) == CurrentChoices.FullLayout;
            float w = Controls.ButtonWidth(options[i]);
            bool was = GUI.enabled;
            GUI.enabled = was && (canFull || i == 0);
            string tip = i == 0 ? "A single tab per section of the preset."
                : canFull ? "Adds tabs to a section until they hold the items your stash already has for it, by the game's own rules."
                : "The game's rule test isn't available.";
            if (Controls.Button(new Rect(bx, y, w, Line), options[i], "stash.tabs." + i, on, tip) && !on)
            {
                CurrentChoices.FullLayout = i == 1;
                s_pending = ChoicesChanged;
            }
            GUI.enabled = was;
            bx += w + Gap;
        }
        Paint(new Rect(0f, y, r.width - rightW - Gap, Line), Controls.Fit(s_summary, HintStyle, r.width - rightW - Gap), HintStyle);
        Popups.Tip(new Rect(0f, y, r.width - rightW - Gap, Line), s_summary);
        y += Line + Gap;

        if (s_snap!.SharedIds.Count > 0)
        {
            var c = GUI.contentColor;
            GUI.contentColor = Theme.Error;
            Paint(new Rect(0f, y, r.width, Line), Controls.Fit("⚠ " + SharedIdsText(s_snap), LabelStyle, r.width), LabelStyle);
            GUI.contentColor = c;
            Popups.Tip(new Rect(0f, y, r.width, Line), SharedIdsText(s_snap));
            y += Line;
        }
        foreach (string note in s_preview!.Notes.Distinct())
        {
            Paint(new Rect(0f, y, r.width, Line), note, HintStyle);
            y += Line;
        }
        return y + 2f;
    }

    // A small square button with a glyph, as the list rows' buttons; greyed when it can't act.
    private static bool GlyphButton(Rect r, Texture2D glyph, string key, string tip, bool can)
    {
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && can;
        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint && Themed)
        {
            bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
            Theme.DrawSliced(r, hover ? Theme.ButtonHover : Theme.ButtonBg, Theme.ControlRadius);
            Theme.Tinted(new Rect(Mathf.Round(r.center.x - glyph.width / 2f), Mathf.Round(r.center.y - glyph.height / 2f), glyph.width, glyph.height), glyph,
                GUI.enabled ? Theme.Secondary : Theme.Faint);
        }
        Popups.Tip(r, tip, key);
        bool pressed = Focus.Item(key, r) || clicked;
        GUI.enabled = wasEnabled;
        return pressed && can;
    }

    private static bool Button(ref float x, string label, string key, bool primary = false, float y = 0f, bool enabled = true, string? tip = null, bool danger = false)
    {
        float w = Controls.ButtonWidth(label);
        bool was = GUI.enabled;
        GUI.enabled = was && enabled;
        bool pressed = Controls.Button(new Rect(x, y, w, Line), label, key, primary, tip, danger: danger);
        GUI.enabled = was;
        x += w + Gap;
        return pressed;
    }

    private static void Heading(Rect r, string text)
    {
        if (Event.current.type != EventType.Repaint) return;
        Paint(r, text.ToUpperInvariant(), Style(() => Theme.Heading, GUI.skin.label));
        if (Themed) Theme.Fill(new Rect(r.x, r.yMax - 2f, r.width, 1f), Theme.DividerColor);
    }

    private static void DrawLeft(Rect r)
    {
        Heading(new Rect(r.x, r.y, r.width, Line), "Your stash");
        var view = new Rect(r.x, r.y + Line + 2f, r.width, r.height - Line - 2f);
        var rows = s_left!;
        var content = new Rect(0f, 0f, view.width - 16f, rows.Count * Line + Line);
        s_leftScroll = GUI.BeginScrollView(view, s_leftScroll, content, false, false);
        Popups.PushOrigin(view.position - s_leftScroll, view);
        try
        {
            var choices = CurrentChoices;
            int first = Mathf.Max(0, (int)(s_leftScroll.y / Line) - 1), last = Mathf.Min(rows.Count, first + (int)(view.height / Line) + 3);
            for (int i = first; i < last; i++)
            {
                var row = rows[i];
                var line = new Rect(row.Folder ? 0f : Indent, i * Line, content.width - (row.Folder ? 0f : Indent), Line - 2f);
                float choiceW = 128f;
                var entry = new Rect(line.x, line.y, line.width - choiceW - Gap, line.height);
                // With the items checked (and the count on): how many the game would file elsewhere once the preview is made.
                int away = !row.Folder && StashSettings.ItemCounts.Value ? ElsewhereCounts?.GetValueOrDefault(row.Id) ?? 0 : 0;
                DrawEntry(entry, row.Name, row.IconId, row.ColourId, row.Folder, row.Tag, row.Folder ? null : away > 0 ? $"{row.Items} · {away} elsewhere" : row.Items);
                if (row.Tag != null) Popups.Tip(entry, row.TagTip);
                if (!row.Folder)
                {
                    string? tip = away > 0
                        ? (row.Warning != null ? row.Warning + "\n" : string.Empty)
                          + $"{away} of its items would go to other tabs once this preview is made. Items are never moved: store them again and the game files each one."
                        : row.Warning;
                    if (tip != null) Popups.Tip(line, tip);
                }
                var snap = s_snap!;
                int choice;
                if (row.Folder) choice = (int)choices.OfFolder(snap, snap.Folder(row.Id)!);
                else choice = (int)choices.Of(snap.Tab(row.Id)!);
                int id = row.Id;
                bool folder = row.Folder;
                Dropdown.Draw(new Rect(line.xMax - choiceW, line.y, choiceW, line.height), $"stash.choice.{(folder ? "f" : "t")}{id}", s_choiceNames, choice,
                    pick => s_pending = () =>
                    {
                        if (s_snap == null) return;
                        if (folder) CurrentChoices.SetFolder(s_snap, id, (TabChoice)pick);
                        else CurrentChoices.Tabs[id] = (TabChoice)pick;
                        ChoicesChanged();
                    }, tip: folder ? "Sets the folder and all its tabs." : row.Warning);
            }
        }
        finally
        {
            Popups.PopOrigin();
            GUI.EndScrollView();
        }
    }

    private static void DrawRight(Rect r)
    {
        if (s_editing || s_confirmCopy)
        {
            DrawEditor(r);
            return;
        }
        Heading(new Rect(r.x, r.y, r.width, Line), "After applying");
        EditButton(new Rect(r.x, r.y, r.width, Line));
        var view = new Rect(r.x, r.y + Line + 2f, r.width, r.height - Line - 2f);
        var rows = s_right!;
        float contentWidth = view.width - 16f;
        var edited = s_editUniques != null ? rows.FirstOrDefault(row => row.Target?.Tab.Key == s_editUniques)?.Target : null;
        float editorHeight = edited != null ? UniqueEditorHeight(edited, contentWidth - Indent) : 0f;
        var content = new Rect(0f, 0f, contentWidth, rows.Count * Line + Line + editorHeight);
        s_rightScroll = GUI.BeginScrollView(view, s_rightScroll, content, false, false);
        Popups.PushOrigin(view.position - s_rightScroll, view);
        try
        {
            float y = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var line = new Rect(row.Folder ? 0f : Indent, y, content.width - (row.Folder ? 0f : Indent), Line - 2f);
                y += Line;
                bool editing = s_editUniques != null && row.Target?.Tab.Key == s_editUniques;
                if (line.yMax < s_rightScroll.y || line.y > s_rightScroll.y + view.height)
                {
                    // The row is out of view, but its editor under it may still be in view.
                    if (editing)
                    {
                        if (y + editorHeight >= s_rightScroll.y && y <= s_rightScroll.y + view.height)
                            UniqueEditor(new Rect(line.x, y, line.width, editorHeight - Gap), row.Target!);
                        y += editorHeight;
                    }
                    continue;
                }
                float markW = Mathf.Min(220f, line.width * 0.45f);
                // The built-in preset can't be changed: its unique lists show on hover, and a copy can be edited.
                bool uniques = row.Target?.Tab.Uniques != null && !CurrentPreset.BuiltIn;
                float editW = uniques ? 62f : 0f;
                DrawEntry(new Rect(line.x, line.y, line.width - markW - editW - Gap, line.height), row.Name, row.IconId, row.ColourId, row.Folder);
                var markStyle = LabelStyle;
                if (Event.current.type == EventType.Repaint)
                {
                    var c = GUI.contentColor;
                    GUI.contentColor = row.MarkColour;
                    Paint(new Rect(line.xMax - markW - editW, line.y, markW, line.height), Controls.Fit(row.Mark, markStyle, markW), markStyle);
                    GUI.contentColor = c;
                }
                if (uniques)
                {
                    float bx = line.xMax - editW + 4f;
                    string key = row.Target!.Tab.Key;
                    if (Button(ref bx, s_editUniques == key ? "Done" : "Edit", "stash.uniques." + key, y: line.y, tip: "Edit this tab's unique list"))
                        s_editUniques = s_editUniques == key ? null : key;
                }
                if (row.Tip.Length > 0) Popups.Tip(line, row.Tip);
                if (editing)
                {
                    UniqueEditor(new Rect(line.x, y, line.width, editorHeight - Gap), row.Target!);
                    y += editorHeight;
                }
            }
        }
        finally
        {
            Popups.PopOrigin();
            GUI.EndScrollView();
        }
    }

    // One folder or tab: the game's colour as a bar, its icon, its name.
    // The bar's right end can be kept clear (reserve) for controls the caller draws there.
    private static void DrawEntry(Rect r, string name, int iconId, int colourId, bool folder, string? tag = null, string? count = null, float reserve = 0f)
    {
        if (Event.current.type != EventType.Repaint) return;
        var colour = TabColour(colourId);
        Theme.Fill(new Rect(r.x, r.y + 2f, 4f, r.height - 4f), colour);
        Theme.Fill(new Rect(r.x + 4f, r.y + 2f, r.width - 4f, r.height - 4f), new Color(colour.r, colour.g, colour.b, folder ? 0.22f : 0.12f));
        r = new Rect(r.x, r.y, r.width - reserve, r.height);
        float right = r.xMax;
        // The item count at the bar's right end, then the tag before it; the name keeps clear of both.
        if (count != null)
        {
            var hint = Middle(HintStyle);
            float cw = TextCache.Width(count, hint) + 8f;
            Paint(new Rect(r.xMax - cw, r.y, cw - 4f, r.height), count, hint);
            right = r.xMax - cw - 4f;
        }
        // A tag sits inside the bar, as a chip in the menu's tag style; the name keeps clear of it.
        if (tag != null)
        {
            var style = Chip;
            float tw = TextCache.Width(tag, style) + 12f;
            var chip = new Rect(right - tw - 4f, r.y + 5f, tw, r.height - 10f);
            if (Themed)
            {
                var was = GUI.color;
                // A see-through dark, so the bar's colour shows through.
                GUI.color = new Color(0f, 0f, 0f, 0.45f);
                Theme.DrawSliced(chip, Theme.TagBg, Theme.TagRadius);
                GUI.color = was;
            }
            Paint(chip, tag, style);
            right = chip.x - 4f;
        }
        float x = r.x + 8f;
        if (TabIcon(iconId) is { } icon)
        {
            GUI.DrawTextureWithTexCoords(new Rect(x, r.y + (r.height - IconSize) / 2f, IconSize, IconSize), icon.Texture, icon.Uv);
            x += IconSize + 4f;
        }
        else if (iconId != 0 && !Has(IconsGroup))
        {
            Paint(new Rect(x, r.y, 26f, r.height), $"#{iconId}", HintStyle);
            x += 28f;
        }
        Paint(new Rect(x, r.y, right - x, r.height), Controls.Fit(name, LabelStyle, right - x), LabelStyle);
    }

    private static void BottomBar(Rect r)
    {
        var steps = CurrentSteps();
        var next = steps.FirstOrDefault();
        long budgetMax = Math.Max(0, Math.Min(s_preview!.FullCost, s_snap!.Gold));
        var choices = CurrentChoices;
        Paint(new Rect(r.x, r.y, 60f, Line), "Budget", LabelStyle);
        float value = Mathf.Clamp(choices.Budget, 0, budgetMax);
        var spec = new NumberSpec(0f, Mathf.Max(1f, budgetMax), Mathf.Max(1f, budgetMax / 200f), Short, v => Mathf.Round(v), t => long.TryParse(t.Replace(",", string.Empty), out long l) ? l : null);
        const float goldWidth = 150f, fieldWidth = 130f;
        void SetBudget(float v)
        {
            long b = (long)Math.Round(Mathf.Clamp(v, 0f, budgetMax));
            if (b == CurrentChoices.Budget) return;
            CurrentChoices.Budget = b;
            s_pending = ChoicesChanged;
        }
        string ofText = $"of {budgetMax:N0}";
        float ofWidth = TextCache.Width(ofText, HintStyle) + 12f;
        var sliderRect = new Rect(r.x + 64f, r.y, r.width - 64f - fieldWidth - ofWidth - goldWidth - 2f * Gap + SliderValueWidth, Line);
        Slider.Draw(sliderRect, "stash.budget", value, spec, SetBudget);
        // The gold amount as a field over the slider's own short value: click to type an amount (Enter or leaving
        // commits, snapped and clamped; Esc cancels), the steppers and arrow keys step it.
        var fieldRect = new Rect(sliderRect.xMax - SliderValueWidth, r.y, fieldWidth, Line);
        var fieldSpec = new NumberSpec(0f, Mathf.Max(1f, budgetMax), Mathf.Max(1f, StepFor(budgetMax)), v => $"{(long)v:N0}", v => Mathf.Round(v),
            ParseGold);
        NumberField.Draw(fieldRect, "stash.budget.field", value, fieldSpec, SetBudget, "Type a gold amount, or step it with the arrows");
        Paint(new Rect(fieldRect.xMax + Gap, r.y, ofWidth, Line), ofText, Middle(HintStyle));
        GoldPanel(new Rect(r.xMax - goldWidth, r.y, goldWidth, Line), s_snap.Gold);
        float y = r.y + Line + Gap, x = r.x;
        string? blocked = next != null ? Blocked(next, s_snap) : null;
        if (StepsBusy)
        {
            float barWidth = Mathf.Min(r.width - 90f, 620f);
            ProgressBar(new Rect(x, y, barWidth, Line));
            x += barWidth + Gap;
            if (RunningAll && Button(ref x, "Stop", "stash.stop", y: y)) StopAll("stopped by you");
        }
        else if (next == null)
        {
            Paint(new Rect(x, y, r.width, Line), "Your stash matches the preview.", LabelStyle);
        }
        else
        {
            string says = next.Says;
            Paint(new Rect(x, y, 70f, Line), "Next:", HintStyle);
            float sw = Mathf.Min(r.width * 0.45f, TextCache.Width(says, LabelStyle) + 8f);
            Paint(new Rect(x + 44f, y, sw, Line), Controls.Fit(says, LabelStyle, sw), LabelStyle);
            x += 44f + sw + Gap;
            if (Button(ref x, "Apply", "stash.apply", true, y, blocked == null, blocked ?? "Sends this one step."))
                s_pending = () => ApplyNext(out s_message);
            long total = s_preview!.Cost;
            int count = StepsLeft(s_snap, s_preview!);
            float aw = Controls.ButtonWidth($"Apply all · {count} steps · {total:N0} gold");
            var allRect = new Rect(x, y, aw, Line);
            if (Button(ref x, $"Apply all · {count} steps · {total:N0} gold", "stash.all", y: y, enabled: blocked == null,
                tip: "Shows what every step at this budget does and costs, then runs them if you say so."))
                Popups.Open(new ApplyAllPopup(Popups.ToWindow(allRect), ApplyAllLines(s_snap, s_preview!, count)));
        }
        y += Line + Gap;
        bool stopped = State == ApplyState.Stopped && StopReason.Length > 0;
        // A purchase says what it costs and the gold it leaves.
        string sends = next == null ? s_message
            : next.Gold > 0 ? $"Sends: {next.Sends} · costs {next.Gold:N0}, leaving {s_snap!.Gold - next.Gold:N0} gold" : $"Sends: {next.Sends}";
        string status = stopped ? $"Stopped: {StopReason}" : blocked ?? sends;
        // A reason the next step can't go (or why a run stopped) asks for attention; anything else is a quiet hint.
        // The item switches at this line's right end; the words before them.
        float statusW = r.width - ItemSwitches(r.xMax, y) - Gap * 2f;
        if (stopped || blocked != null) Attention(new Rect(r.x, y, statusW, Line), status);
        else Paint(new Rect(r.x, y, statusW, Line), Controls.Fit(status, HintStyle, statusW), HintStyle);
    }

    // Attention in every theme: amber, apart from the theme's own state colours.
    private static readonly Color Amber = new(1f, 0.69f, 0.2f);

    private static string s_attentionText = string.Empty;
    private static float s_attentionSince;

    // A strip in the state colour for waiting: a bar and the warning mark at its left, the words in the label colour,
    // pulsing for its first two seconds after it appears.
    private static void Attention(Rect r, string text)
    {
        if (text != s_attentionText)
        {
            s_attentionText = text;
            s_attentionSince = Time.unscaledTime;
        }
        float width = Mathf.Min(r.width, TextCache.Width(text, LabelStyle) + 44f);
        var strip = new Rect(r.x, r.y, width, r.height);
        if (Event.current.type == EventType.Repaint)
        {
            var colour = Amber;
            float age = Time.unscaledTime - s_attentionSince;
            float pulse = age < 2f ? 0.5f + 0.5f * Mathf.Cos(age * Mathf.PI * 2f) : 0f;
            Theme.Fill(strip, new Color(colour.r, colour.g, colour.b, 0.14f + 0.16f * pulse));
            Theme.Fill(new Rect(strip.x, strip.y, 3f, strip.height), colour);
            if (Themed) Theme.Tinted(new Rect(strip.x + 10f, strip.y + (strip.height - 16f) / 2f, 16f, 16f), Theme.IconWarning, colour);
        }
        var style = Middle(LabelStyle);
        Paint(new Rect(strip.x + 32f, strip.y, strip.width - 36f, strip.height), Controls.Fit(text, style, strip.width - 36f), style);
    }

    // A typed gold amount: digits with commas or spaces, a decimal point, and k or m for thousands and millions
    // ("50k", "1.5m", "2,050,000"); null when it isn't one.
    private static float? ParseGold(string text)
    {
        string t = text.Trim().ToLowerInvariant().Replace(",", string.Empty).Replace(" ", string.Empty);
        double scale = 1d;
        if (t.EndsWith("k")) { scale = 1_000d; t = t[..^1]; }
        else if (t.EndsWith("m")) { scale = 1_000_000d; t = t[..^1]; }
        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v >= 0d
            ? (float)Math.Round(v * scale) : null;
    }

    // The slider's own value text sits in its right end; the gold field covers it.
    private const float SliderValueWidth = 58f;

    // A step that moves the budget by about a tab's worth at a time: 1/200 of the range, rounded to 500 gold.
    private static float StepFor(float max) => Mathf.Max(500f, Mathf.Round(max / 200f / 500f) * 500f);

    // Gold in a few characters, for the slider's value.
    private static string Short(float v) => v >= 1_000_000f ? $"{v / 1_000_000f:0.##}M" : v >= 10_000f ? $"{v / 1000f:0.#}k" : $"{v:0}";

    // Apply's progress as a loading bar: filled by the steps done, a light band sweeping across while a step waits for
    // the game, a thin line running down the pause before the next; the step's words on top.
    private static void ProgressBar(Rect r)
    {
        var (done, total) = AllProgress;
        bool all = RunningAll;
        total = Math.Max(all ? total : 1, done + 1);
        var (says, waiting, pause) = StepNow;
        float filled = (float)done / total;
        if (Event.current.type == EventType.Repaint)
        {
            var accent = Theme.Accent;
            Theme.Fill(r, new Color(accent.r, accent.g, accent.b, 0.14f));
            Theme.Fill(new Rect(r.x, r.y, r.width * filled, r.height), new Color(accent.r, accent.g, accent.b, 0.55f));
            // The step in flight: its share of the bar, with a band running across it.
            var current = new Rect(r.x + r.width * filled, r.y, r.width / total, r.height);
            if (waiting)
            {
                Theme.Fill(current, new Color(accent.r, accent.g, accent.b, 0.28f));
                float band = Mathf.Max(18f, r.width * 0.12f);
                float t = Time.unscaledTime * 0.9f % 1f;
                float bx = r.x - band + (r.width + band) * t;
                float left = Mathf.Max(r.x, bx), right = Mathf.Min(r.xMax, bx + band);
                if (right > left) Theme.Fill(new Rect(left, r.y, right - left, r.height), new Color(1f, 1f, 1f, 0.10f));
            }
            else Theme.Fill(new Rect(current.x, r.yMax - 3f, current.width * pause, 3f), accent);
        }
        string text = all ? $"Step {Math.Min(done + 1, total)} of {total} · {says}" : says;
        var style = Middle(LabelStyle);
        Paint(new Rect(r.x + 8f, r.y, r.width - 16f, r.height), Controls.Fit(text, style, r.width - 16f), style);
    }


    // ---- Unique list editor ----

    private const int FoldedTagLines = 2;
    private const float TagGap = 4f;

    // The tab whose whole unique list shows; folded, two lines of it show with a count of the rest.
    private static string? s_uniquesUnfolded;

    // The tags' places: one per unique that shows, wrapping at the width; folded at two lines, the last keeping room for
    // the button that unfolds the rest. The count shown and the lines used come back with them.
    private static (List<(int Id, Rect Rect)> Tags, int Lines) TagPlaces(int[] ids, float x, float y, float width, bool unfolded)
    {
        var tags = new List<(int, Rect)>();
        float bx = x, by = y;
        int line = 1;
        for (int i = 0; i < ids.Length; i++)
        {
            float w = Controls.ButtonWidth(TagLabel(ids[i]));
            bool last = !unfolded && line == FoldedTagLines;
            // On the last folded line, room for "+N more" unless this is the final tag.
            float room = last && i < ids.Length - 1 ? MoreWidth : 0f;
            if (bx > x && bx + w > x + width - room)
            {
                if (last) break;
                bx = x;
                by += Line;
                line++;
                if (!unfolded && line == FoldedTagLines && i < ids.Length - 1 && bx + w > x + width - MoreWidth) break;
            }
            tags.Add((ids[i], new Rect(bx, by, Mathf.Min(w, width), Line - 2f)));
            bx += w + TagGap;
        }
        return (tags, ids.Length == 0 ? 0 : line);
    }

    private const float MoreWidth = 96f;

    // Folding the list in or out: words in the theme's teal with no button behind them, underlined under the pointer,
    // so they read apart from the uniques' own buttons.
    private static GUIStyle? s_foldLink;

    private static bool FoldLink(Rect r, string label, string key, string tip)
    {
        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint)
        {
            s_foldLink ??= new GUIStyle(Middle(LabelStyle)) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(4, 4, 0, 0) };
            bool hover = r.Contains(Event.current.mousePosition);
            // The theme's teal, muted toward the hint colour at rest; its full teal under the pointer.
            var teal = Themed ? Theme.HighlightText : Color.cyan;
            var colour = hover ? teal : Color.Lerp(teal, Themed ? Theme.Dim : Color.grey, 0.4f);
            var was = GUI.contentColor;
            GUI.contentColor = colour;
            Paint(r, label, s_foldLink);
            GUI.contentColor = was;
            if (hover)
            {
                float w = TextCache.Width(label, s_foldLink) - s_foldLink.padding.horizontal;
                Theme.Fill(new Rect(r.x + s_foldLink.padding.left, Mathf.Round(r.center.y + 8f), w, 1f), colour);
            }
        }
        Popups.Tip(r, tip, key);
        return Focus.Item(key, r) || clicked;
    }

    // A unique list in name order, for showing; kept while the list itself is the same.
    private static (int[] From, int[] Sorted)? s_byName;

    private static int[] ByName(int[] ids)
    {
        if (s_byName is { } cached && ReferenceEquals(cached.From, ids)) return cached.Sorted;
        var sorted = ids.OrderBy(Rules.UniqueName, StringComparer.OrdinalIgnoreCase).ToArray();
        s_byName = (ids, sorted);
        return sorted;
    }

    private static string TagLabel(int id) => Rules.UniqueName(id) + " ×";

    // The title, the tags' lines (with a line for "Show less" when the whole list shows), and the add row.
    private static float UniqueEditorHeight(Target x, float width)
    {
        var ids = ByName(x.Rules.Uniques);
        bool unfolded = s_uniquesUnfolded == x.Tab.Key;
        int lines = Math.Max(1, TagPlaces(ids, 0f, 0f, width, unfolded).Lines);
        return Line * (2 + lines) + (unfolded ? Line : 0f) + Gap;
    }

    private static void UniqueEditor(Rect r, Target x)
    {
        var preset = CurrentPreset;
        if (preset.BuiltIn) return;
        var tab = preset.AllTabs().First(p => p.Tab.Key == x.Tab.Key).Tab;
        var source = tab.Uniques!;
        var ids = ByName(x.Rules.Uniques);
        string key = x.Tab.Key;
        bool unfolded = s_uniquesUnfolded == key;
        var title = new Rect(r.x, r.y, r.width, Line);
        Paint(title, source.Kind == UniqueSource.Builds ? $"Uniques used by builds in your Planner library ({ids.Length})" : $"Uniques ({ids.Length})", LabelStyle);
        if (source.Kind == UniqueSource.Builds)
            Popups.Tip(title, "Every unique the builds imported on the Planner tab use, over all their stages. The list follows the library as builds are imported or removed; what you add or remove here stays.");
        // Each unique as a small button that removes it; under the pointer, the game's tooltip for it shows beside the menu.
        var (tags, lines) = TagPlaces(ids, r.x, r.y + Line, r.width, unfolded);
        var mouse = Event.current.mousePosition;
        foreach (var (id, rect) in tags)
        {
            // Not through a popup over it (the add dropdown's list), nor while scrolled out of the list's view.
            if (Event.current.type == EventType.Repaint && !Popups.IsOpen && rect.Contains(mouse) && Popups.Shows(mouse)) Planner.HoverUnique((ushort)id);
            if (Controls.Button(rect, Controls.Fit(TagLabel(id), Theme.Label, rect.width - 12f), $"stash.unique.{key}.{id}", tip: "Remove from this tab's list"))
            {
                int remove = id;
                s_pending = () =>
                {
                    // One added by hand is only taken back off; one from the source is kept out.
                    if (!source.Added.Remove(remove) && !source.Removed.Contains(remove)) source.Removed.Add(remove);
                    Presets.Save(preset);
                    ChoicesChanged();
                };
            }
        }
        if (ids.Length == 0) Paint(new Rect(r.x, r.y + Line, r.width, Line), "None yet: add one below.", HintStyle);
        float tagsBottom = r.y + Line + Math.Max(1, lines) * Line;
        if (!unfolded && tags.Count < ids.Length)
        {
            float mx = tags.Count > 0 ? tags[^1].Rect.xMax + TagGap : r.x;
            if (FoldLink(new Rect(mx, tagsBottom - Line, Mathf.Min(MoreWidth, r.xMax - mx), Line - 2f), $"+{ids.Length - tags.Count} more", "stash.unique.more." + key, "Show the whole list"))
                s_uniquesUnfolded = key;
        }
        else if (unfolded)
        {
            if (FoldLink(new Rect(r.x, tagsBottom, MoreWidth, Line - 2f), "Show less", "stash.unique.less." + key, "Fold the list to two lines")) s_uniquesUnfolded = null;
            tagsBottom += Line;
        }
        // Added from the game's own unique list: a dropdown filtered by typing, leaving out the ones already listed.
        float ay = tagsBottom;
        var options = AddOptions(ids);
        // Lined up with the tags above it.
        Dropdown.Draw(new Rect(r.x, ay, Mathf.Min(320f, r.width), Line - 2f), "stash.unique.add." + key, options.Names, -1, i =>
        {
            int add = options.Ids[i];
            s_pending = () =>
            {
                source.Removed.Remove(add);
                if (!source.Added.Contains(add)) source.Added.Add(add);
                Presets.Save(preset);
                ChoicesChanged();
            };
        }, shown: options.Names.Count > 0 ? "Add a unique…" : "No uniques to add", tip: "The game's uniques; type to filter");
    }

    // The game's uniques not hidden from players, by name, a set item marked; made once, as the list only changes with
    // a game update.
    private static List<(int Id, string Name)>? s_allUniques;
    private static (int[] Listed, (List<string> Names, List<int> Ids) Options)? s_addOptions;

    private static (List<string> Names, List<int> Ids) AddOptions(int[] listed)
    {
        if (s_addOptions is { } cached && ReferenceEquals(cached.Listed, listed)) return cached.Options;
        var all = s_allUniques ??= AllUniques();
        var skip = new HashSet<int>(listed);
        var names = new List<string>();
        var ids = new List<int>();
        foreach (var (id, name) in all)
        {
            if (skip.Contains(id)) continue;
            names.Add(name);
            ids.Add(id);
        }
        // An empty game list (read before the game loaded it) is read again next time.
        if (all.Count == 0) s_allUniques = null;
        s_addOptions = (listed, (names, ids));
        return (names, ids);
    }

    private static List<(int Id, string Name)> AllUniques()
    {
        var all = new List<(int, string)>();
        try
        {
            var list = UniqueList.get()?.uniques;
            if (list == null) return all;
            for (int i = 0; i < list.Count; i++)
            {
                var u = list[i];
                if (u == null || u.hideFromPlayers) continue;
                string name = !string.IsNullOrWhiteSpace(u.displayName) ? u.displayName : u.name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                all.Add((u.uniqueID, u.isSetItem ? name + " (set)" : name));
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stash:uniquelist", $"Stash: unique list unreadable: {e.Message}", 30);
        }
        all.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
        return all;
    }

    // ---- Bridge ----

    private static IEnumerable<string> BridgeLines()
    {
        var snap = s_snap;
        var preview = s_preview;
        if (snap == null || preview == null)
        {
            yield return "stash none";
            yield break;
        }
        if (s_left == null || s_right == null) BuildRows();
        yield return $"stash source={snap.Source} tabs={snap.TabCount} folders={snap.Folders.Count} gold={snap.Gold} fp={snap.Fingerprint:x}";
        var p = CurrentPreset;
        yield return $"preset id={p.Id} name={RowText.Quote(p.Name)} full={preview.FullCost} budget={CurrentChoices.Budget} cost={preview.Cost} new={preview.NewTabs} full-layout={(CurrentChoices.FullLayout ? "on" : "off")}";
        int n = 1;
        foreach (var row in s_left!)
            yield return row.Folder
                ? $"left #{n++} folder={row.Id} {RowText.Quote(row.Name)} choice={ChoiceText(CurrentChoices.OfFolder(snap, snap.Folder(row.Id)!))}"
                : $"left #{n++} tab={row.Id} {RowText.Quote(row.Name)} items={snap.Tab(row.Id)?.ItemCount} choice={ChoiceText(CurrentChoices.Of(snap.Tab(row.Id)!))}";
        n = 1;
        foreach (var row in s_right!)
        {
            if (row.Target is { } x)
                yield return $"right #{n++} key={x.Tab.Key} copy={x.Copy} fill={FillText(x)} folder={x.Folder.ExistingId?.ToString() ?? "new"} {RowText.Quote(row.Name)}";
            else yield return $"right #{n++} {(row.Folder ? "folder" : "tab")} {RowText.Quote(row.Name)} {RowText.Quote(row.Mark)}";
        }
        foreach (var w in preview.Warnings) yield return $"warn key={w.TargetKey} taken-by={w.TakenById} {RowText.Quote(w.TakenByName)}";
        var next = CurrentSteps().FirstOrDefault();
        if (next != null) yield return $"step next kind={next.Kind} says={RowText.Quote(next.Says)} gold={next.Gold}";
        yield return $"apply state={State.ToString().ToLowerInvariant()} reason={RowText.Quote(StopReason)}";
    }

    public static string ChoiceText(TabChoice c) => c switch { TabChoice.Keep => "keep", TabChoice.Reuse => "reuse", _ => "reuse-if-empty" };

    public static string FillText(Target x) => x.Fill switch { Fill.Done => $"done:{x.TabId}", Fill.Reuse => $"reuse:{x.TabId}", _ => $"buy:{x.Price}" };

    // ---- The game's icons and colours ----

    private static List<(Texture Texture, Rect Uv)?>? s_icons;
    private static float s_iconsCheckedAt = -10f;
    private static List<Color>? s_colours;

    // The game's 18 tab colours (F8), used when its lists can't be read.
    private static readonly string[] s_fallbackColours =
    {
        "#8E8E8E", "#FFFFFF", "#F0FF7C", "#FFCC88", "#FFA68A", "#FF88A7", "#FB99FF", "#C19EFF", "#9EC8FF",
        "#A5FFF4", "#53FF2F", "#F7FF64", "#FFAA40", "#FF5574", "#FF5AE8", "#AC61FF", "#53A1FF", "#2AFFCF",
    };

    private static void LoadIcons()
    {
        // Checked once a second: every row draws an icon, and each check asks the game.
        if (s_icons != null && (Time.unscaledTime < s_iconsCheckedAt + 1f || s_icons.All(i => i == null || Game.Alive(i.Value.Texture))))
        {
            if (Time.unscaledTime >= s_iconsCheckedAt + 1f) s_iconsCheckedAt = Time.unscaledTime;
            return;
        }
        s_iconsCheckedAt = Time.unscaledTime;
        s_icons = new List<(Texture, Rect)?>();
        s_colours = new List<Color>();
        if (!Has(IconsGroup)) return;
        try
        {
            var ui = TabsUi();
            if (ui == null) return;
            if (ui.tabColors != null) s_colours.AddRange(ui.tabColors.ToArray());
            if (ui.tabIcons != null)
                foreach (var sprite in ui.tabIcons.ToArray())
                {
                    if (!Game.Alive(sprite) || !Game.Alive(sprite.texture))
                    {
                        s_icons.Add(null);
                        continue;
                    }
                    var tex = sprite.texture;
                    var tr = sprite.textureRect;
                    s_icons.Add((tex, new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height)));
                }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stash:icons", $"Stash: icons unreadable: {e.Message}", 30);
        }
    }

    private static (Texture Texture, Rect Uv)? TabIcon(int id)
    {
        if (id <= 0) return null;
        LoadIcons();
        return s_icons != null && id < s_icons.Count ? s_icons[id] : null;
    }

    private static Color TabColour(int id)
    {
        LoadIcons();
        if (s_colours != null && id >= 0 && id < s_colours.Count) return s_colours[id];
        return id >= 0 && id < s_fallbackColours.Length && ColorUtility.TryParseHtmlString(s_fallbackColours[id], out var c) ? c : Color.grey;
    }

    // The sprites and colours are the game's own; the page only drops its references.
    private static void ReleaseIcons()
    {
        s_icons = null;
        s_colours = null;
        s_gold = null;
        s_chip = null;
        s_goldIsInventory = false;
        s_middle.Clear();
    }
}
