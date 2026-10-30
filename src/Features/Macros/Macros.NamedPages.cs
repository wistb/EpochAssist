using System.Text.Json.Nodes;
using EpochAssist.Core;
using S = EpochAssist.Features.MacroSettings;

namespace EpochAssist.Features;

// Named conditions in the Macros tab (contracts/named-format.md): a "Named conditions" line above the sets with a line
// per named condition under it; the list page with "New named condition"; and a page per named condition (name, its
// tree, "Keep true until", where it is used, Delete). Every change goes through the setting (undo snapshots first), and
// an imported copy whose name the player already had offers "Use mine".
internal static partial class Macros
{
    private const string NamedPageKey = "named";

    private static bool IsNamedPage(string key) => key == NamedPageKey || key.StartsWith(NamedPageKey + "/", StringComparison.Ordinal);

    private static string? NamedIdOfPage(string key) => key.StartsWith(NamedPageKey + "/", StringComparison.Ordinal) ? key[(NamedPageKey.Length + 1)..] : null;

    // ---- Library lines ----

    private static void AddNamedLines(List<LibraryLine> lines)
    {
        var doc = S.NamedConditions.Doc;
        var ids = NamedConditions.IdsOf(doc).ToList();
        int used = ids.Count(InUse);
        lines.Add(new(NamedPageKey, 0, "Named conditions", ids.Count == 0 ? "none yet" : $"{ids.Count}, {used} in use", used > 0 ? StatusState.Active : StatusState.Off, null, true));
        foreach (string id in ids)
        {
            // In use: a macro, slot list or Shared Conditions reaches it; one used only by unused named conditions isn't.
            int uses = UsesCount(id);
            lines.Add(new($"{NamedPageKey}/{id}", 1, NamedConditions.Name(doc, id), uses > 0 ? $"used by {uses}" : "unused", InUse(id) ? StatusState.Active : StatusState.Off, null, false));
        }
    }

    private static int UsesCount(string id) => s_named?.UsesOf(id).Count ?? 0;

    private static bool InUse(string id) => s_named?.InUse(id) ?? false;

    private static IReadOnlyList<ActionButton> NamedActions(string key)
    {
        bool editable = !S.NamedConditions.Doc.ReadOnly;
        if (NamedIdOfPage(key) is not { } id)
        {
            return new[]
            {
                Button("Edit", "edit", () => Edit(key)),
                Button("New named condition", "newnamed", NewNamed, () => editable && NamedConditions.Count(S.NamedConditions.Doc) < NamedConditions.MaxNamed,
                    NamedConditions.LimitReason),
            };
        }
        return new[]
        {
            Button("Edit", "edit", () => Edit(key)),
            Button("Delete", "delete", () => DeleteNamed(id), () => editable, "Undo brings it back"),
        };
    }

    private static string? NamedHint(string key)
    {
        if (S.NamedConditions.Doc.ReadOnly) return "These named conditions were made by a newer version of Epoch Assist; they are kept as they are and can't be changed here.";
        if (NamedIdOfPage(key) is not { } id) return "A named condition is a tree of lines with a name; any macro line can say \"<name> is true\". Change it once and every macro using it follows.";
        int uses = UsesCount(id);
        return uses == 0 ? "No macro uses it yet: add a line \"" + NamedConditions.Name(S.NamedConditions.Doc, id) + " is true\" to one." : null;
    }

    // ---- Changes ----

    // Changes the named conditions' document through the setting; a toast when it can't. watch false leaves the undo
    // snapshot to the caller (a change of the library and the named conditions together is one Undo).
    private static bool EditNamed(Func<ConditionDoc, bool> change, bool watch = true)
    {
        if (watch) History.Watch();
        var doc = ConditionDoc.Parse(S.NamedConditions.Value, out string reason);
        if (doc == null || doc.ReadOnly)
        {
            Toasts.Show("macros", $"The named conditions can't be changed: {(doc == null ? reason : "made by a newer version")}.", ToastKind.Warning);
            return false;
        }
        if (!change(doc)) return false;
        if (S.NamedConditions.SetText(doc.ToText(), out reason)) return true;
        Toasts.Show("macros", $"The named conditions can't be changed: {reason}.", ToastKind.Warning);
        return false;
    }

    private static void SetField(ConditionList list, string name, JsonNode? value)
    {
        list.Extra.RemoveAll(e => e.Key == name);
        if (value != null) list.Extra.Add(new(name, value));
    }

    private static void NewNamed()
    {
        string? made = null;
        if (!EditNamed(doc =>
        {
            if (NamedConditions.Count(doc) >= NamedConditions.MaxNamed)
            {
                Report(NamedConditions.LimitReason + ".", ToastKind.Warning);
                return false;
            }
            made = NamedConditions.NewId(doc);
            var list = new ConditionList();
            SetField(list, "name", NamedConditions.FreeName(doc, "New named condition"));
            doc.Set(made, list);
            return true;
        }) || made == null) return;
        View.Selected = $"{NamedPageKey}/{made}";
        Edit(View.Selected);
    }

    private static void Rename(string id, string name)
    {
        name = name.Trim();
        s_nameProblem = NamedConditions.NameProblem(S.NamedConditions.Doc, name, id);
        if (s_nameProblem != null) return;
        EditNamed(doc =>
        {
            if (!NamedConditions.IsNamed(doc, id)) return false;
            SetField(doc.List(id), "name", name);
            return true;
        });
    }

    private static string? s_nameProblem;
    // The named condition whose Delete waits for Yes or No.
    private static string? s_deleting;

    // An unused one goes at once; a used one asks first, in its page's header.
    private static void DeleteNamed(string id)
    {
        if (UsesCount(id) > 0 && s_deleting != id)
        {
            s_deleting = id;
            if (s_editing != $"{NamedPageKey}/{id}") Edit($"{NamedPageKey}/{id}");
            return;
        }
        s_deleting = null;
        if (!EditNamed(doc =>
        {
            doc.Lists.Remove(id);
            doc.Order.Remove(id);
            doc.Lists.Remove(id + NamedConditions.EndsSuffix);
            doc.Order.Remove(id + NamedConditions.EndsSuffix);
            return true;
        })) return;
        s_clashes.RemoveAll(c => c.CopyId == id || c.MineId == id);
        if (s_editing == $"{NamedPageKey}/{id}") CloseEditor();
    }

    // ---- Pages ----

    private static LibraryEditor? NamedEditor(string key)
    {
        if (NamedIdOfPage(key) is not { } id) return NamedListEditor();
        if (!NamedConditions.IsNamed(S.NamedConditions.Doc, id)) return null;
        var editor = new LibraryEditor(key, () => NamedConditions.Name(S.NamedConditions.Doc, id), () => NamedRows(key, id))
        {
            Place = () => "Named conditions",
            Note = () => s_deleting == id ? $"Delete? Used in {UsesCount(id)} place{(UsesCount(id) == 1 ? string.Empty : "s")}."
                : ClashOf(id) is { } clash ? $"Imported with {clash.SetName}; you already have {NamedConditions.Name(S.NamedConditions.Doc, clash.MineId)}." : null,
        };
        editor.Buttons.Add(Button("Undo", "undo", Undo, () => History.CanUndo, "Takes back the last change on this tab"));
        editor.Buttons.Add(new ActionButton("Use mine", "macros.usemine") { OnClick = () => UseMine(id), VisibleWhen = () => s_deleting != id && ClashOf(id) != null,
            Tip = "Points the imported set at your named condition of that name and deletes this copy" });
        editor.Buttons.Add(new ActionButton("Keep this copy", "macros.keepcopy") { OnClick = () => s_clashes.RemoveAll(c => c.CopyId == id), VisibleWhen = () => s_deleting != id && ClashOf(id) != null });
        editor.Buttons.Add(new ActionButton("Delete", "macros.delete") { OnClick = () => DeleteNamed(id), VisibleWhen = () => s_deleting != id,
            EnabledWhen = () => !S.NamedConditions.Doc.ReadOnly, Tip = "Undo brings it back" });
        editor.Buttons.Add(new ActionButton("Yes", "macros.deleteyes") { OnClick = () => DeleteNamed(id), Danger = true, VisibleWhen = () => s_deleting == id, Tip = "Lines using it then read \"missing named condition\"" });
        editor.Buttons.Add(new ActionButton("No", "macros.deleteno") { OnClick = () => s_deleting = null, VisibleWhen = () => s_deleting == id });
        editor.Buttons.Add(Button("Close", "close", CloseEditor));
        return editor;
    }

    private static LibraryEditor NamedListEditor()
    {
        var editor = new LibraryEditor(NamedPageKey, () => "Named conditions", NamedListRows);
        editor.Buttons.Add(Button("Undo", "undo", Undo, () => History.CanUndo, "Takes back the last change on this tab"));
        editor.Buttons.Add(Button("New named condition", "newnamed", NewNamed,
            () => !S.NamedConditions.Doc.ReadOnly && NamedConditions.Count(S.NamedConditions.Doc) < NamedConditions.MaxNamed, NamedConditions.LimitReason));
        editor.Buttons.Add(Button("Close", "close", CloseEditor));
        return editor;
    }

    private static string? s_namedListText;
    private static int s_namedListPlaces = -1, s_namedListClashes = -1;
    private static List<MenuRow> s_namedListRows = new();

    private static IReadOnlyList<MenuRow> NamedListRows()
    {
        string text = S.NamedConditions.Value;
        int places = PlacesGeneration(), clashes = Clashes().Count;
        if (ReferenceEquals(text, s_namedListText) && places == s_namedListPlaces && clashes == s_namedListClashes) return s_namedListRows;
        s_namedListText = text;
        s_namedListPlaces = places;
        s_namedListClashes = clashes;
        var rows = new List<MenuRow>
        {
            new HintRow(() => "A tree of lines with a name. Any macro line can say \"<name> is true\"; change it here and every macro using it follows.") { Key = "hint" },
        };
        var doc = S.NamedConditions.Doc;
        // An import's open choices: the copy it added, or the player's own of that name.
        foreach (var clash in Clashes())
        {
            string copy = clash.CopyId;
            rows.Add(new BannerRow(() => ClashOf(copy) is { } c ? $"Imported with {c.SetName}; you already have {NamedConditions.Name(S.NamedConditions.Doc, c.MineId)}." : null,
                new ActionButton("Use mine", "named.usemine." + copy) { OnClick = () => UseMine(copy), Tip = "Points the imported set at your named condition and deletes the copy" },
                new ActionButton("Keep this copy", "named.keep." + copy) { OnClick = () => s_clashes.RemoveAll(c => c.CopyId == copy) }) { Key = "clash." + copy });
        }
        foreach (string id in NamedConditions.IdsOf(doc))
        {
            string named = id;
            rows.Add(new TextButtonRow(NamedConditions.Name(doc, id), () => UsesCount(named) is int n && n > 0 ? $"used by {n}" : "unused",
                new ActionButton("Edit", "named.edit." + id) { OnClick = () => EditFromHere($"{NamedPageKey}/{named}") }) { Key = "named." + id });
        }
        if (rows.Count == 1) rows.Add(new HintRow(() => "None yet: New named condition makes one.") { Key = "none" });
        s_namedListRows = rows;
        return rows;
    }

    private static (string Key, string Text, int Places, string? Open)? s_namedBuiltFor;
    private static List<MenuRow> s_namedRows = new();

    // Rows rebuilt when the named conditions, the places using them or the open line change.
    private static IReadOnlyList<MenuRow> NamedRows(string key, string id)
    {
        string text = S.NamedConditions.Value;
        int places = PlacesGeneration();
        if (s_namedBuiltFor is { } was && was.Key == key && ReferenceEquals(was.Text, text) && was.Places == places && was.Open == s_openLine) return s_namedRows;
        s_namedBuiltFor = (key, text, places, s_openLine);
        var rows = new List<MenuRow>();
        rows.Add(new HintRow(() => "No live data: enter the game to see values.") { Key = "nolive", VisibleWhen = () => !Player.Exists });
        rows.Add(new ValueTextField("Name", () => NamedConditions.Name(S.NamedConditions.Doc, id), name => Rename(id, name), NamedConditions.MaxName) { Key = "name" });
        rows.Add(new HintRow(() => s_nameProblem is { } problem ? $"Not renamed: {problem}." : $"Names: {NamedConditions.NameRule}.") { Key = "name.rule" });
        rows.Add(new LiveText("Now", () => NamedNow(id)) { VisibleWhen = () => Player.Exists });

        rows.Add(new HeadingRow("True when"));
        RuleTreeRows.Add(rows, NamedTree("named", id, () => id, start: true));

        rows.Add(new HeadingRow("Keep true"));
        rows.Add(new ValueToggle("Keep true until", () => Sticky(id), on => SetSticky(id, on)) { Key = "sticky" });
        rows.Add(new HintRow(() => "Once true it stays true until the lines below pass, or the lines above have failed for the hold time.") { Key = "sticky.hint", VisibleWhen = () => Sticky(id) });
        rows.Add(new ValueNumber("Hold time", () => NamedConditions.HoldOf(S.NamedConditions.List(id)), v => EditNamed(doc =>
        {
            if (!NamedConditions.IsNamed(doc, id)) return false;
            SetField(doc.List(id), "holdS", v <= 0f ? null : JsonValue.Create(v));
            return true;
        }), 0f, NamedConditions.MaxHold, NamedConditions.HoldStep, " s") { Key = "hold", VisibleWhen = () => Sticky(id) });
        if (Sticky(id))
        {
            rows.Add(new HeadingRow("Ends when"));
            RuleTreeRows.Add(rows, NamedTree("namedends", id, () => id + NamedConditions.EndsSuffix, start: false));
        }

        rows.Add(new HeadingRow("Used by"));
        var uses = s_named?.UsesOf(id) ?? Array.Empty<NamedPlace>();
        if (uses.Count == 0) rows.Add(new HintRow(() => "Nothing yet. Add a line \"" + NamedConditions.Name(S.NamedConditions.Doc, id) + " is true\" to a macro.") { Key = "uses.none" });
        for (int i = 0; i < uses.Count; i++)
        {
            var use = uses[i];
            if (use.Open is not { } open)
            {
                rows.Add(new ValueText(use.Place, () => "another form's list") { Key = $"use.{i}" });
                continue;
            }
            rows.Add(new TextButtonRow(use.Place, () => string.Empty, new ActionButton("Open", $"named.use.{i}") { OnClick = () => OpenPlace(open), Tip = "Opens it; Close comes back here" })
            {
                Key = $"use.{i}",
            });
        }
        s_namedRows = rows;
        return rows;
    }

    private static void OpenPlace(string key)
    {
        if (!key.StartsWith(NamedPageKey + "/", StringComparison.Ordinal) && key != EveryKey && !key.StartsWith(SlotsKey + "/", StringComparison.Ordinal)) View.Selected = key;
        EditFromHere(key);
    }

    // "true", "false · held", "holding 1.2 s": the result now, checked as its users see it.
    private static (string, StatusState?) NamedNow(string id)
    {
        if (s_named is not { } named) return ("macros off", StatusState.Off);
        bool? value = named.Result(id, out string? reason);
        if (value is not bool v) return ($"unknown: {reason}", StatusState.Waiting);
        string sticky = named.StickyText(id);
        string text = (v ? "true" : "false") + (sticky is "plain" or "off" ? string.Empty : " · " + sticky);
        return (text, v ? StatusState.Active : StatusState.Paused);
    }

    private static bool Sticky(string id) => S.NamedConditions.List(id).Field("holdS") != null || S.NamedConditions.Doc.Lists.ContainsKey(id + NamedConditions.EndsSuffix);

    // On: a 3 s hold to start from and an empty ends tree; off removes both.
    private static void SetSticky(string id, bool on) => EditNamed(doc =>
    {
        if (!NamedConditions.IsNamed(doc, id)) return false;
        string ends = id + NamedConditions.EndsSuffix;
        if (on)
        {
            SetField(doc.List(id), "holdS", JsonValue.Create(3f));
            if (!doc.Lists.ContainsKey(ends))
            {
                // Kept while empty, so the "Ends when" tree shows.
                var list = new ConditionList();
                doc.Lists[ends] = list;
                doc.Order.Add(ends);
            }
        }
        else
        {
            SetField(doc.List(id), "holdS", null);
            doc.Lists.Remove(ends);
            doc.Order.Remove(ends);
        }
        return true;
    });

    // A named condition's start or ends tree as the rule tree rows read and change it, with its live state from the
    // resolver (checked as its users see it).
    private static TreeAccess NamedTree(string prefix, string id, Func<string> key, bool start) => new()
    {
        Prefix = prefix,
        List = () => S.NamedConditions.List(key()),
        Edit = change =>
        {
            bool done = false;
            EditNamed(doc =>
            {
                if (!NamedConditions.IsNamed(doc, id)) return false;
                var list = doc.Lists.TryGetValue(key(), out var stored) ? stored : new ConditionList();
                if (list.Invalid != null) return false;
                change(list);
                if (!doc.Lists.ContainsKey(key())) doc.Order.Add(key());
                // An ends tree stays while "Keep true until" is on, even when empty.
                doc.Lists[key()] = list;
                done = true;
                return true;
            });
            return done;
        },
        State = () =>
        {
            if (!Player.Exists || s_named is not { } named) return null;
            LiveFrame();
            named.Result(id, out _);
            var state = named.StateOf(id);
            var tree = start ? state?.Tree : state?.EndsTree;
            return tree?.List != null && ReferenceEquals(tree.List, S.NamedConditions.List(key())) ? tree : null;
        },
        Blocked = () => S.NamedConditions.Doc.ReadOnly ? "made by a newer version" : null,
        Named = id,
        Open = s_openLine is { } open && open.StartsWith(prefix + "/") ? open[(prefix.Length + 1)..] : null,
        SetOpen = path => s_openLine = path == null ? null : prefix + "/" + path,
    };

    // ---- Import clashes ----

    // Imported copies whose name the player already had, this session (cleared on a profile change).
    private static readonly List<NamedConditions.Clash> s_clashes = new();
    private static int s_clashesProfile = -1;

    // The choices still open: none from another profile, none whose copy or own one is gone.
    private static List<NamedConditions.Clash> Clashes()
    {
        if (s_clashesProfile != Profiles.Generation)
        {
            s_clashesProfile = Profiles.Generation;
            s_clashes.Clear();
        }
        var doc = S.NamedConditions.Doc;
        s_clashes.RemoveAll(c => !NamedConditions.IsNamed(doc, c.CopyId) || !NamedConditions.IsNamed(doc, c.MineId));
        return s_clashes;
    }

    private static NamedConditions.Clash? ClashOf(string copyId) => Clashes().FirstOrDefault(c => c.CopyId == copyId);

    /// <summary>Points every line at the copy (the imported set's, the other imported copies') at the player's own named
    /// condition and deletes the copy; one Undo takes both back.</summary>
    private static void UseMine(string copyId)
    {
        if (ClashOf(copyId) is not { } clash) return;
        var map = new Dictionary<string, string> { [clash.CopyId] = clash.MineId };
        History.Watch();
        EditLibrary(doc =>
        {
            for (int i = 0; i < doc.Sets.Count; i++)
            {
                if (MacroDoc.NamedIds(doc.Sets[i]).Contains(clash.CopyId)) doc.Sets[i] = MacroDoc.RemapNamed(doc.Sets[i], map);
            }
        });
        EditNamed(doc =>
        {
            var json = doc.ToJson();
            if (json["lists"] is JsonObject lists)
            {
                lists.Remove(clash.CopyId);
                lists.Remove(clash.CopyId + NamedConditions.EndsSuffix);
                NamedConditions.RemapIds(lists, map);
            }
            var remapped = ConditionDoc.Parse(json, out _);
            if (remapped == null) return false;
            doc.Lists.Clear();
            doc.Order.Clear();
            foreach (string key in remapped.Order)
            {
                doc.Order.Add(key);
                doc.Lists[key] = remapped.Lists[key];
            }
            return true;
        }, watch: false);
        s_clashes.Remove(clash);
        Report($"The set now uses your {NamedConditions.Name(S.NamedConditions.Doc, clash.MineId)}; the copy is deleted (Undo brings it back).", ToastKind.Info);
        if (s_editing == $"{NamedPageKey}/{copyId}") CloseEditor();
    }
}
