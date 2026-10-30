using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Features;

internal static partial class Stash
{
    // ---- Editing the preset ----
    //
    // The right side's edit mode: the whole preset (every folder and tab, whatever the budget buys), each row with a drag
    // handle, its name (click to rename), its colour and a remove button. The built-in preset is never changed: editing
    // it first asks to make a copy and says so. Every edit says what it did, with Undo, and once the items are checked
    // again how many of them now go to another tab. Removing says beforehand what it changes.

    private static bool s_editing, s_confirmCopy;
    private static string s_editNotice = string.Empty, s_editHover = string.Empty;
    private static readonly List<(string Json, string Label)> s_undo = new();
    // A rename in progress: the folder or tab key and the text typed.
    private static (bool Folder, string Key)? s_editRename;
    private static string s_editRenameText = string.Empty;
    // A drag in progress: what is dragged, and where it would land (folder index, and for a tab its place in it).
    private static (bool Folder, string Key)? s_drag;
    private static (int Folder, int Tab)? s_drop;
    // The items' tabs before the last edit, to say afterwards which now go elsewhere.
    private static Dictionary<IntPtr, string>? s_placesBefore;
    private static IReadOnlyList<ItemPlace>? s_placesBeforeSource;
    private static (string Key, string Text)? s_removeCost;
    private static Vector2 s_editScroll;

    private const float HandleWidth = 18f;
    private static GUIStyle? s_removeStyle;
    private static float s_editMarkW = 190f;
    private const int NameLimit = 18;

    private static readonly List<string> s_colourNames = Enumerable.Range(0, 18).Select(i => $"Colour {i + 1}").ToList();
    private static List<Color?>? s_colourSwatches;

    private static void StopEditing()
    {
        s_editing = s_confirmCopy = false;
        s_editRename = null;
        s_drag = null;
        s_editNotice = s_editHover = string.Empty;
        s_undo.Clear();
        s_placesBefore = null;
    }

    // The heading's button: straight into edit mode on the player's preset, or the copy notice on the built-in one.
    private static void EditButton(Rect heading)
    {
        const string Label = "Edit layout";
        float w = Controls.ButtonWidth(Label);
        var r = new Rect(heading.xMax - w, heading.y - 3f, w, Line - 2f);
        string tip = CurrentPreset.BuiltIn
            ? "Move, rename, recolour or remove folders and tabs. The built-in preset isn't changed: you're asked to make a copy first."
            : "Move, rename, recolour or remove this preset's folders and tabs.";
        if (Controls.Button(r, Label, "stash.edit", tip: tip))
        {
            if (CurrentPreset.BuiltIn) s_confirmCopy = true;
            else
            {
                s_editing = true;
                s_editNotice = $"Editing your preset {CurrentPreset.Name}. Changes are saved as you make them.";
            }
        }
    }

    private static void DrawEditor(Rect r)
    {
        var preset = CurrentPreset;
        if (s_confirmCopy)
        {
            CopyNotice(r, preset);
            return;
        }
        if (preset.BuiltIn)
        {
            StopEditing();
            return;
        }
        Heading(new Rect(r.x, r.y, r.width, Line), $"Editing {preset.Name}");
        float bx = r.xMax;
        float doneW = Controls.ButtonWidth("Done");
        if (Controls.Button(new Rect(bx - doneW, r.y - 3f, doneW, Line - 2f), "Done", "stash.edit.done", true, "Back to the preview at this budget"))
            s_pending = StopEditing;
        bx -= doneW + Gap;
        if (s_undo.Count > 0)
        {
            float uw = Controls.ButtonWidth("Undo");
            if (Controls.Button(new Rect(bx - uw, r.y - 3f, uw, Line - 2f), "Undo", "stash.edit.undo", tip: "Undo: " + s_undo[^1].Label))
                s_pending = Undo;
        }
        MovedItemsNotice();
        // What the last edit did, or what the hovered remove would do.
        float ny = r.y + Line + 2f;
        string notice = s_editHover.Length > 0 ? s_editHover : s_editNotice;
        float noticeHeight = Line;
        if (s_editHover.Length > 0) Attention(new Rect(r.x, ny, r.width, noticeHeight), notice);
        else Paint(new Rect(r.x, ny, r.width, noticeHeight), Controls.Fit(notice, HintStyle, r.width), HintStyle);
        if (Event.current.type == EventType.Repaint) s_editHover = string.Empty;
        EditList(new Rect(r.x, ny + noticeHeight + 2f, r.width, r.height - Line - noticeHeight - 4f), preset);
    }

    // The built-in preset is about to be edited: what happens, in the attention colour, and the two ways on.
    private static void CopyNotice(Rect r, Preset preset)
    {
        Heading(new Rect(r.x, r.y, r.width, Line), "Edit layout");
        string name = Presets.NextName(preset.Name);
        var box = new Rect(r.x, r.y + Line + 6f, r.width, Line * 4f);
        if (Event.current.type == EventType.Repaint)
        {
            Theme.Fill(box, new Color(Amber.r, Amber.g, Amber.b, 0.14f));
            Theme.Fill(new Rect(box.x, box.y, 3f, box.height), Amber);
        }
        var style = new GUIStyle(LabelStyle) { wordWrap = true };
        Paint(new Rect(box.x + 12f, box.y + 4f, box.width - 20f, Line * 2.4f),
            $"Editing makes a copy of {preset.Name} named {name}, and switches to it. The built-in preset stays as it is, and you can go back to it from the Preset list.", style);
        float x = box.x + 12f;
        if (Button(ref x, "Make copy and edit", "stash.edit.copy", true, box.y + Line * 2.8f - 2f))
            s_pending = () =>
            {
                var copy = Presets.Copy(preset, name);
                if (!Presets.Save(copy))
                {
                    s_confirmCopy = false;
                    s_message = "The copy couldn't be saved.";
                    return;
                }
                CurrentChoices.Preset = copy.Id;
                ChoicesChanged();
                s_confirmCopy = false;
                s_editing = true;
                s_editNotice = $"Made {copy.Name} and switched to it. Changes are saved as you make them.";
            };
        if (Button(ref x, "Cancel", "stash.edit.cancel", y: box.y + Line * 2.8f - 2f)) s_confirmCopy = false;
    }

    // The preset's folders and tabs, top to bottom as the game files items.
    private static void EditList(Rect view, Preset preset)
    {
        int rows = preset.Folders.Count + preset.Folders.Sum(f => f.Tabs.Count);
        var content = new Rect(0f, 0f, view.width - 16f, rows * Line + Line);
        s_editMarkW = Mathf.Min(190f, content.width * 0.36f);
        s_editScroll = GUI.BeginScrollView(view, s_editScroll, content, false, false);
        Popups.PushOrigin(view.position - s_editScroll, view);
        var lines = new List<(Rect Rect, int Folder, int Tab)>();
        try
        {
            float y = 0f;
            for (int fi = 0; fi < preset.Folders.Count; fi++)
            {
                var f = preset.Folders[fi];
                var line = new Rect(0f, y, content.width, Line - 2f);
                lines.Add((line, fi, -1));
                EditRow(line, preset, f, null);
                y += Line;
                for (int ti = 0; ti < f.Tabs.Count; ti++)
                {
                    var tl = new Rect(Indent, y, content.width - Indent, Line - 2f);
                    lines.Add((tl, fi, ti));
                    EditRow(tl, preset, f, f.Tabs[ti]);
                    y += Line;
                }
            }
            DragFrame(preset, lines);
        }
        finally
        {
            Popups.PopOrigin();
            GUI.EndScrollView();
        }
    }

    private static void EditRow(Rect line, Preset preset, PresetFolder folder, PresetTab? tab)
    {
        bool isFolder = tab == null;
        string key = isFolder ? folder.Key : tab!.Key;
        string name = isFolder ? folder.Name : tab!.Name;
        int colour = isFolder ? folder.ColourId : tab!.ColourId;
        bool dragged = s_drag is { } d && d.Folder == isFolder && d.Key == key;
        // The handle: press and drag to move.
        var handle = new Rect(line.x, line.y, HandleWidth, line.height);
        if (Event.current.type == EventType.Repaint)
        {
            var c = dragged ? Theme.Accent : Theme.Dim;
            for (int i = 0; i < 3; i++) Theme.Fill(new Rect(handle.x + 4f, handle.y + 7f + i * 4f, 10f, 2f), c);
        }
        if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && handle.Contains(Event.current.mousePosition))
        {
            s_drag = (isFolder, key);
            s_drop = null;
            s_editRename = null;
            Event.current.Use();
        }
        Popups.Tip(handle, isFolder ? "Drag to move the folder with its tabs" : "Drag to move the tab, also into another folder");
        // The bar, then the colour swatch and remove just after it, then what it becomes at this budget.
        // Fixed columns for the whole list, so folder and tab bars end at one edge and the swatch, remove and words line up.
        float markW = s_editMarkW;
        const float Inner = 18f;
        float barEnd = line.xMax - markW - Gap - (Inner + 4f) * 2f;
        var bar = new Rect(line.x + HandleWidth + 2f, line.y, barEnd - line.x - HandleWidth - 2f, line.height);
        var swatch = new Rect(bar.xMax + 4f, bar.y + (bar.height - Inner) / 2f, Inner, Inner);
        var remove = new Rect(swatch.xMax + 4f, swatch.y, Inner, Inner);
        bool renaming = s_editRename is { } rn && rn.Folder == isFolder && rn.Key == key;
        if (renaming)
        {
            var e = Event.current;
            var field = bar;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                string text = s_editRenameText.Trim();
                s_editRename = null;
                if (text.Length > 0 && text != name) s_pending = () => Edit($"Renamed {name} to {text}", p => SetName(p, isFolder, key, text));
                e.Use();
            }
            else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                s_editRename = null;
                e.Use();
            }
            else
            {
                GUI.SetNextControlName("stash.edit.rename");
                s_editRenameText = GUI.TextField(field, s_editRenameText, NameLimit);
                if (e.type == EventType.Repaint && GUI.GetNameOfFocusedControl() != "stash.edit.rename") GUI.FocusControl("stash.edit.rename");
            }
        }
        else
        {
            string? tag = !isFolder && tab!.IsMerged ? "merged" : null;
            // The name has the bar to itself; the rank is in the tip.
            DrawEntry(bar, name, isFolder ? folder.IconId : tab!.IconId, colour, isFolder, tag);
            var nameArea = bar;
            if (GUI.Button(nameArea, GUIContent.none, GUIStyle.none) && s_drag == null)
            {
                s_editRename = (isFolder, key);
                s_editRenameText = name;
            }
            Popups.Tip(nameArea, EditRowText(folder, tab).Tip);
        }
        // Colour: the swatch opens the game's colours.
        s_colourSwatches ??= Enumerable.Range(0, 18).Select(i => (Color?)TabColour(i)).ToList();
        var mouse = Event.current.mousePosition;

        // In the same grey box as the remove button.
        if (Event.current.type == EventType.Repaint)
        {
            Theme.DrawSliced(swatch, swatch.Contains(mouse) ? Theme.ButtonHover : Theme.ButtonBg, Theme.ControlRadius);
            Theme.Tinted(new Rect(swatch.x + 4f, swatch.y + 4f, Inner - 8f, Inner - 8f), Theme.Swatch, TabColour(colour));
        }
        if (GUI.Button(swatch, GUIContent.none, GUIStyle.none))
            Popups.Open(new DropdownPopup($"stash.edit.colour.{(isFolder ? "f" : "t")}.{key}", Popups.ToWindow(swatch), s_colourNames, Mathf.Clamp(colour, 0, 17),
                pick => s_pending = () => Edit($"Colour of {name} changed", p => SetColour(p, isFolder, key, pick)), null, s_colourSwatches));
        Popups.Tip(swatch, "Colour");
        // Remove, saying beforehand what it changes.
        bool overRemove = remove.Contains(mouse) && Popups.Shows(mouse) && s_drag == null;
        if (overRemove) s_editHover = RemoveCost(preset, isFolder, key, name);
        if (Event.current.type == EventType.Repaint) DrawRemove(remove, overRemove);
        if (GUI.Button(remove, GUIContent.none, GUIStyle.none))
            s_pending = () => Edit(isFolder ? $"Removed folder {name} and its {folder.Tabs.Count} tab{(folder.Tabs.Count == 1 ? string.Empty : "s")}" : $"Removed {name}",
                p => Remove(p, isFolder, key));
        // What it becomes at this budget, as the preview says.
        var (_, mark, markColour) = EditRowText(folder, tab);
        if (Event.current.type == EventType.Repaint)
        {
            var c = GUI.contentColor;
            GUI.contentColor = markColour;
            Paint(new Rect(line.xMax - markW, line.y, markW, line.height), Controls.Fit(mark, LabelStyle, markW), LabelStyle);
            GUI.contentColor = c;
        }
    }

    // Each row's tip and budget words, worked out once for the preset as edited and the plan: rows draw every frame, and
    // a tab's tip describes its rules (with its unique list).
    private static readonly Dictionary<string, (string Tip, string Mark, Color Colour)> s_rowText = new();
    private static (Preview? Preview, int Edits, string Preset)? s_rowTextFor;
    private static int s_edits;

    private static (string Tip, string Mark, Color Colour) EditRowText(PresetFolder folder, PresetTab? tab)
    {
        var key = (s_preview, s_edits, CurrentPreset.Id);
        if (s_rowTextFor != key)
        {
            s_rowText.Clear();
            s_rowTextFor = key;
        }
        string id = tab == null ? "f:" + folder.Key : "t:" + tab.Key;
        if (s_rowText.TryGetValue(id, out var text)) return text;
        string tip = tab == null ? $"{folder.Name}: click to rename (up to {NameLimit} characters)"
            : $"{tab.Name}: click to rename. {Rules.Describe(RulesOf(tab))}\nRank {tab.Rank}: the budget buys lower ranks first; its place here decides which items it catches first."
              + (tab.IsMerged ? "\n" + MergedTip(tab) : string.Empty);
        var (mark, colour) = EditMark(tab == null ? folder : null, tab);
        text = (tip, mark, colour);
        s_rowText[id] = text;
        return text;
    }

    private static readonly Color RemoveRed = new(0.95f, 0.42f, 0.42f);

    // The remove button: a grey box as the menu's buttons have, its × turning red under the pointer.
    private static void DrawRemove(Rect r, bool hover)
    {
        s_removeStyle ??= new GUIStyle(LabelStyle)
        {
            alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 2), fontSize = LabelStyle.fontSize + 5,
        };
        Theme.DrawSliced(r, hover ? Theme.ButtonHover : Theme.ButtonBg, Theme.ControlRadius);
        var was = GUI.contentColor;
        GUI.contentColor = hover ? RemoveRed : Theme.Secondary;
        Paint(r, "×", s_removeStyle);
        GUI.contentColor = was;
    }

    // A preset folder's or tab's words at this budget: the preview's when it's in it, else why it isn't.
    private static (string Mark, Color Colour) EditMark(PresetFolder? folder, PresetTab? tab)
    {
        var preview = s_preview;
        var snap = s_snap;
        if (preview == null || snap == null) return (string.Empty, Theme.Secondary);
        if (folder != null)
        {
            var f = preview.Folders.FirstOrDefault(p => p.Preset?.Key == folder.Key);
            if (f == null)
            {
                // Its tabs can still be planned, in another folder (a kept one the plan files them into).
                var elsewhere = preview.Targets.FirstOrDefault(t => folder.Tabs.Any(pt => pt.Key == t.Tab.Key));
                return (elsewhere != null ? $"its tabs go to {elsewhere.Folder.Name}" : "not at this budget", Theme.Dim);
            }
            string mark = f.ExistingId == null ? "new folder" : f.Configure ? FolderChange(snap.Folder(f.ExistingId.Value), f) : "done";
            return (mark, Theme.Secondary);
        }
        var x = preview.Targets.FirstOrDefault(t => t.Tab.Key == tab!.Key && t.Copy == 1);
        if (x != null) return TargetMark(snap, x, preview.Warnings.Any(w => w.TargetKey == x.Tab.Key));
        if (tab!.IsMerged && preview.Split.Contains(tab.Key)) return ("split into its tabs here", Theme.Dim);
        var merged = CurrentPreset.AllTabs().Select(p => p.Tab).FirstOrDefault(t => t.MergeOf?.Contains(tab.Key) == true);
        if (merged != null && !preview.Split.Contains(merged.Key) && preview.Targets.Any(t => t.Tab.Key == merged.Key)) return ($"in {merged.Name} here", Theme.Dim);
        return ("not at this budget", Theme.Dim);
    }

    // While dragging: where it would land, drawn as a line; on release, the move.
    private static void DragFrame(Preset preset, List<(Rect Rect, int Folder, int Tab)> lines)
    {
        if (s_drag is not { } drag) return;
        var e = Event.current;
        var mouse = e.mousePosition;
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            s_drag = null;
            e.Use();
            return;
        }
        // The place under the pointer: before the row whose upper half it is in, else after it.
        (int Folder, int Tab)? drop = null;
        float lineY = 0f, lineX = 0f;
        if (drag.Folder)
        {
            var folders = lines.Where(l => l.Tab < 0).ToList();
            int at = folders.Count;
            foreach (var l in folders)
                if (mouse.y < l.Rect.center.y) { at = l.Folder; break; }
            drop = (at, -1);
            lineY = at < folders.Count ? folders[at].Rect.y - 1f : lines[^1].Rect.yMax + 1f;
        }
        else
        {
            foreach (var l in lines)
            {
                if (mouse.y > l.Rect.yMax + 2f) continue;
                if (l.Tab < 0) drop = mouse.y < l.Rect.center.y && l.Folder > 0 ? (l.Folder - 1, preset.Folders[l.Folder - 1].Tabs.Count) : (l.Folder, 0);
                else drop = mouse.y < l.Rect.center.y ? (l.Folder, l.Tab) : (l.Folder, l.Tab + 1);
                break;
            }
            drop ??= (preset.Folders.Count - 1, preset.Folders[^1].Tabs.Count);
            var d = drop.Value;
            var inFolder = lines.Where(l => l.Folder == d.Folder && l.Tab >= 0).ToList();
            var head = lines.First(l => l.Folder == d.Folder && l.Tab < 0);
            lineY = d.Tab < inFolder.Count ? inFolder[d.Tab].Rect.y - 1f : (inFolder.Count > 0 ? inFolder[^1].Rect.yMax : head.Rect.yMax) + 1f;
            lineX = Indent;
        }
        s_drop = drop;
        if (e.type == EventType.Repaint)
        {
            float w = lines.Count > 0 ? lines[0].Rect.width : 100f;
            Theme.Fill(new Rect(lineX, lineY - 1f, w - lineX, 2f), Theme.Accent);
        }
        if (e.rawType == EventType.MouseUp)
        {
            var target = s_drop;
            s_drag = null;
            if (target is { } t) s_pending = () => Move(preset, drag, t);
            e.Use();
        }
    }

    // ---- The edits ----

    // Every edit: kept for Undo, made, saved, planned again; the items' tabs before it kept to say what moved.
    private static void Edit(string label, Action<Preset> change)
    {
        var preset = CurrentPreset;
        if (preset.BuiltIn) return;
        s_undo.Add((Presets.ToJson(preset).ToJsonString(), label));
        if (s_undo.Count > 50) s_undo.RemoveAt(0);
        if (ItemPlaces is { } places)
        {
            s_placesBefore = places.GroupBy(p => p.Entry).ToDictionary(g => g.Key, g => g.First().Into);
            s_placesBeforeSource = places;
        }
        change(preset);
        s_edits++;
        Presets.Save(preset);
        s_editNotice = label + ".";
        s_removeCost = null;
        ChoicesChanged();
    }

    private static void Undo()
    {
        if (s_undo.Count == 0) return;
        var (json, label) = s_undo[^1];
        s_undo.RemoveAt(s_undo.Count - 1);
        var preset = CurrentPreset;
        if (Presets.Parse(System.Text.Json.Nodes.JsonNode.Parse(json)) is not { } old) return;
        preset.Name = old.Name;
        preset.Folders.Clear();
        preset.Folders.AddRange(old.Folders);
        s_edits++;
        Presets.Save(preset);
        s_editNotice = $"Undid: {label}.";
        s_placesBefore = null;
        s_removeCost = null;
        ChoicesChanged();
    }

    // Once the items are checked against the edited preset: how many now go to another tab, and the biggest such move.
    private static void MovedItemsNotice()
    {
        if (s_placesBefore is not { } before || ItemPlaces is not { } now || ReferenceEquals(now, s_placesBeforeSource)) return;
        var changes = now.Where(p => before.TryGetValue(p.Entry, out var was) && was != p.Into)
            .GroupBy(p => (From: before[p.Entry], To: p.Into)).OrderByDescending(g => g.Count()).ToList();
        int moved = changes.Sum(g => g.Count());
        if (moved > 0)
        {
            var top = changes[0];
            string from = top.Key.From.Length > 0 ? top.Key.From : "no tab", to = top.Key.To.Length > 0 ? top.Key.To : "no tab";
            s_editNotice += $" {moved} of your items now go to a different tab" + (changes.Count == 1 ? $": {top.Count()} from {from} to {to}." : $", e.g. {top.Count()} from {from} to {to}.");
        }
        s_placesBefore = null;
    }

    // What removing a folder or tab changes at this budget: tabs, gold and steps, and the items it takes now.
    private static string RemoveCost(Preset preset, bool isFolder, string key, string name)
    {
        if (s_removeCost is { } cached && cached.Key == (isFolder ? "f:" : "t:") + key) return cached.Text;
        string text;
        try
        {
            var copy = Presets.Parse(Presets.ToJson(preset))!;
            copy.Id = preset.Id;
            Remove(copy, isFolder, key);
            var snap = s_snap!;
            var now = s_preview!;
            var after = Plan(InputFor(snap, copy, CurrentChoices, CurrentChoices.Budget), withFull: false);
            int tabs = now.NewTabs - after.NewTabs, steps = StepsLeft(snap, now) - StepsLeft(snap, after);
            long gold = now.Cost - after.Cost;
            var names = isFolder ? preset.Folders.First(f => f.Key == key).Tabs.Select(t => t.Name).ToHashSet() : new HashSet<string> { name };
            int items = ItemPlaces?.Count(p => names.Contains(p.Into)) ?? 0;
            var parts = new List<string>();
            if (tabs != 0) parts.Add($"{Math.Abs(tabs)} tab{(Math.Abs(tabs) == 1 ? string.Empty : "s")} {(tabs > 0 ? "fewer" : "more")} to buy");
            if (gold != 0) parts.Add($"{Math.Abs(gold):N0} gold {(gold > 0 ? "less" : "more")}");
            if (steps != 0) parts.Add($"{Math.Abs(steps)} step{(Math.Abs(steps) == 1 ? string.Empty : "s")} {(steps > 0 ? "fewer" : "more")}");
            if (items > 0) parts.Add($"{items} of your items it would take go to other tabs");
            text = $"Remove {(isFolder ? "folder " : string.Empty)}{name}: " + (parts.Count > 0 ? string.Join(" · ", parts) : "nothing changes at this budget") + ".";
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stash:edit", $"Stash: remove preview failed: {e.Message}", 30);
            text = $"Remove {name}.";
        }
        s_removeCost = ((isFolder ? "f:" : "t:") + key, text);
        return text;
    }

    private static void SetName(Preset p, bool isFolder, string key, string name)
    {
        if (isFolder) { if (p.Folders.FirstOrDefault(f => f.Key == key) is { } f) f.Name = name; }
        else if (p.AllTabs().FirstOrDefault(x => x.Tab.Key == key).Tab is { } t) t.Name = name;
    }

    private static void SetColour(Preset p, bool isFolder, string key, int colour)
    {
        if (isFolder) { if (p.Folders.FirstOrDefault(f => f.Key == key) is { } f) f.ColourId = colour; }
        else if (p.AllTabs().FirstOrDefault(x => x.Tab.Key == key).Tab is { } t) t.ColourId = colour;
    }

    // A removed tab also leaves the merged tabs that stood in for it; a removed folder takes its tabs.
    private static void Remove(Preset p, bool isFolder, string key)
    {
        var gone = new List<string>();
        if (isFolder)
        {
            if (p.Folders.FirstOrDefault(f => f.Key == key) is not { } f) return;
            gone.AddRange(f.Tabs.Select(t => t.Key));
            p.Folders.Remove(f);
        }
        else
        {
            foreach (var f in p.Folders)
                if (f.Tabs.FirstOrDefault(t => t.Key == key) is { } t)
                {
                    f.Tabs.Remove(t);
                    gone.Add(key);
                    break;
                }
        }
        foreach (var (_, t) in p.AllTabs())
            if (t.MergeOf != null) t.MergeOf.RemoveAll(gone.Contains);
    }

    private static void Move(Preset preset, (bool Folder, string Key) drag, (int Folder, int Tab) to)
    {
        if (drag.Folder)
        {
            int from = preset.Folders.FindIndex(f => f.Key == drag.Key);
            if (from < 0 || to.Folder == from || to.Folder == from + 1) return;
            var folder = preset.Folders[from];
            string where = to.Folder < preset.Folders.Count ? $"above {preset.Folders[to.Folder].Name}" : "to the end";
            Edit($"Moved folder {folder.Name} {where}", p =>
            {
                int i = p.Folders.FindIndex(f => f.Key == drag.Key);
                var moving = p.Folders[i];
                p.Folders.RemoveAt(i);
                p.Folders.Insert(to.Folder > i ? to.Folder - 1 : to.Folder, moving);
            });
            return;
        }
        int fromFolder = preset.Folders.FindIndex(f => f.Tabs.Any(t => t.Key == drag.Key));
        if (fromFolder < 0) return;
        int fromTab = preset.Folders[fromFolder].Tabs.FindIndex(t => t.Key == drag.Key);
        if (to.Folder == fromFolder && (to.Tab == fromTab || to.Tab == fromTab + 1)) return;
        var tab = preset.Folders[fromFolder].Tabs[fromTab];
        var into = preset.Folders[to.Folder];
        string label = to.Folder != fromFolder ? $"Moved {tab.Name} into {into.Name}"
            : to.Tab < into.Tabs.Count ? $"Moved {tab.Name} above {into.Tabs[to.Tab].Name}" : $"Moved {tab.Name} to the end of {into.Name}";
        Edit(label, p =>
        {
            var src = p.Folders[fromFolder];
            int i = src.Tabs.FindIndex(t => t.Key == drag.Key);
            var moving = src.Tabs[i];
            src.Tabs.RemoveAt(i);
            var dst = p.Folders[to.Folder];
            int place = to.Folder == fromFolder && to.Tab > i ? to.Tab - 1 : to.Tab;
            dst.Tabs.Insert(Math.Clamp(place, 0, dst.Tabs.Count), moving);
        });
    }
}
