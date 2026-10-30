using EpochAssist.Core;
using S = EpochAssist.Features.MacroSettings;

namespace EpochAssist.Features;

// The Macros tab as data (contracts/menu.md): the library layout with the class dropdown, the Slots set first (Every
// slot and the five slot macros, whose tick boxes are the slot toggles), then the all-classes sets, then the class's
// named sets and their macros;
// the selection's actions; the footer switches; and the editors that replace the library while open. The menu draws it.
internal static partial class Macros
{
    public static readonly LibraryView View = new()
    {
        Classes = () => MacroSet.Classes,
        Class = () => Array.IndexOf(MacroSet.Classes, PickedClass),
        SetClass = i =>
        {
            if (i >= 0 && i < MacroSet.Classes.Length) s_pickedClass = MacroSet.Classes[i];
        },
        Lines = Lines,
        Tick = Tick,
        Open = line => Edit(line.Key),
        Actions = ActionsFor,
        Editor = () =>
        {
            History.Watch();
            return s_editing != null ? EditorFor(s_editing) : null;
        },
        Back = () =>
        {
            if (s_editing == null) return false;
            CloseEditor();
            return true;
        },
        Rename = Rename,
        Banner = () => s_banner != null && Clock.Now - s_bannerAt < BannerSeconds ? s_banner : null,
        Toolbar = { Button("Import set", "paste", Paste, () => !S.LibraryDoc.Doc.ReadOnly, "Adds the set whose share code is on the clipboard, switched off"),
            Button("New set", "newset", NewSet, () => !S.LibraryDoc.Doc.ReadOnly && S.LibraryDoc.Doc.SetsOf(PickedClass).Count() < MacroSet.MaxPerClass,
            $"A new set for the class shown ({MacroSet.MaxPerClass} sets per class at most)") },
        ActionHint = Hint,
        Footer = { S.Enabled, S.DebugConditions, S.DebugRanges, S.DebugNotifications, S.ShowRangeRing },
        // To the macros' settings in the Config tree, not a page in this tab.
        FooterButtons = { new ActionButton("Settings", "menu.config.macros") { Tip = "Opens Config › Macros" } },
        Undo = Undo,
    };

    // The library page; its Settings button opens Config › Macros.
    public static readonly MenuEntry Entry = new(MenuCategory.Macros, order: 10)
    {
        Title = "Library",
        Switch = S.Enabled,
        Layout = MenuLayout.Library,
        Library = View,
        Pages = { new MenuPage("Library") { Resettable = false } },
    };

    private const string SlotsKey = "slots", EveryKey = "slots/every";

    // The class whose sets show: the character's when it can be read, else the last one shown (Rogue at first).
    private static string? s_pickedClass;
    private static string? s_lastCharacterClass;
    private static string? s_editing;

    /// <summary>The class the tab shows: picked in the dropdown, else the character's, else the last known.</summary>
    public static string PickedClass
    {
        get
        {
            string? character = PlayerModel.ClassName;
            // A character's class takes over when it changes (entering the game, another character).
            if (character != null && character != s_lastCharacterClass)
            {
                s_lastCharacterClass = character;
                s_pickedClass = character;
            }
            return s_pickedClass ?? character ?? s_lastCharacterClass ?? "Rogue";
        }
    }

    // ---- Lines ----

    private static IReadOnlyList<LibraryLine> Lines()
    {
        History.Watch();
        var lines = new List<LibraryLine>
        {
            new(SlotsKey, 0, "Skill Slots", SlotsState(), StatusState.Active, null, true),
            new(EveryKey, 1, "Shared Conditions", MacroSettingsPage.EveryStatus().Text, MacroSettingsPage.EveryStatus().State, null, false),
        };
        for (int i = 0; i < SlotMacros.Slots; i++)
        {
            var status = MacroSettingsPage.SlotStatus(i);
            lines.Add(new($"{SlotsKey}/{i + 1}", 1, $"Slot {i + 1}: {SlotMacros.SlotAbilityName(i) ?? "-"}", status.Text, status.State,
                MacroSettingsPage.Status.Slots?[i] ?? false, false));
        }
        AddNamedLines(lines);
        var doc = S.LibraryDoc.Doc;
        foreach (var set in doc.SetsOf(MacroSet.AllClasses).Concat(doc.SetsOf(PickedClass)))
        {
            var (setState, setShape) = SetState(set);
            if (set.Class == MacroSet.AllClasses) setState = "all classes, " + setState;
            lines.Add(new(set.Id, 0, set.Name.Length > 0 ? set.Name : "(no name)", setState, setShape, set.On, true));
            foreach (var macro in set.Macros)
            {
                var (state, shape) = MacroState(set, macro);
                lines.Add(new($"{set.Id}/{macro.Id}", 1, macro.Name.Length > 0 ? macro.Name : "(no name)", state, shape, macro.On, false));
            }
        }
        return lines;
    }

    private static string SlotsState()
    {
        int on = MacroSettingsPage.Status.Slots?.Count(t => t) ?? 0;
        return on == 0 ? "no slot on" : $"{on} on";
    }

    private static (string, StatusState) SetState(MacroSet set)
    {
        if (set.Invalid is { } invalid) return ("invalid: " + invalid, StatusState.Waiting);
        if (!set.KnownClass) return ("unknown class", StatusState.Waiting);
        return set.On ? ($"{set.Macros.Count(m => m.On)} on", StatusState.Active) : ("off", StatusState.Off);
    }

    /// <summary>A macro's state text: off, invalid, unknown class, running step n, waiting on its first failing line, ready.</summary>
    public static (string Text, StatusState Shape) MacroState(MacroSet set, Macro macro)
    {
        if (!macro.On || !set.On) return ("off", StatusState.Off);
        if (macro.Invalid is { } invalid) return ("invalid: " + invalid, StatusState.Waiting);
        if (set.Invalid != null) return ("invalid set", StatusState.Waiting);
        if (!set.KnownClass) return ("unknown class", StatusState.Waiting);
        return EngineState(set, macro);
    }

    private static void Tick(LibraryLine line)
    {
        if (line.Key.StartsWith(SlotsKey + "/") && int.TryParse(line.Key[(SlotsKey.Length + 1)..], out int slot))
        {
            // The same toggle the chord flips.
            Core.Actions.Run($"macros.slot.{slot}", "menu library");
            return;
        }
        if (IsNamedPage(line.Key)) return;
        var (setId, macroId) = Split(line.Key);
        EditLibrary(doc =>
        {
            var (set, macro) = doc.Find(setId, macroId);
            if (macro != null) macro.On = !macro.On;
            else if (set != null) set.On = !set.On;
        });
    }

    private static (string Set, string? Macro) Split(string key)
    {
        int slash = key.IndexOf('/');
        return slash < 0 ? (key, null) : (key[..slash], key[(slash + 1)..]);
    }

    /// <summary>Changes the library through the setting (the profile in use); a toast when it can't.</summary>
    internal static bool EditLibrary(Action<MacroDoc> edit)
    {
        if (S.LibraryDoc.Edit(edit, out string reason)) return true;
        Toasts.Show("macros", $"The library can't be changed: {reason}.", ToastKind.Warning);
        return false;
    }

    // ---- Actions ----

    private static IReadOnlyList<ActionButton> ActionsFor(LibraryLine? line)
    {
        if (line == null) return Array.Empty<ActionButton>();
        if (line.Key == SlotsKey) return Array.Empty<ActionButton>();
        if (line.Key == EveryKey) return new[] { Button("Edit", "edit", () => Edit(line.Key)) };
        if (IsNamedPage(line.Key)) return NamedActions(line.Key);
        if (line.Key.StartsWith(SlotsKey + "/") && int.TryParse(line.Key[(SlotsKey.Length + 1)..], out int slotNumber))
        {
            return new[]
            {
                Button("Edit", "edit", () => Edit(line.Key)),
                Button("Duplicate as a macro", "dupe", () => DuplicateAsMacro(slotNumber - 1), () => DuplicateTarget() != null,
                    "Copies this slot macro into a named set of the class shown as a macro of its own"),
            };
        }
        var (setId, macroId) = Split(line.Key);
        return macroId == null ? SetActions(setId) : MacroActions(setId, macroId);
    }

    internal static ActionButton Button(string label, string key, Action click, Func<bool>? enabled = null, string? tip = null) =>
        new(label, "macros." + key) { OnClick = click, EnabledWhen = enabled, Tip = tip };

    private static void Edit(string key)
    {
        if (key == SlotsKey) return;
        s_editing = key;
        s_cameFrom = null;
    }

    // The page a link opened this one from (a slot macro's shared conditions or shared target), so Close goes back to it.
    private static string? s_cameFrom;

    // Opens a page from the one open now; closing it returns there.
    private static void EditFromHere(string key)
    {
        string? from = s_editing;
        Edit(key);
        s_cameFrom = from != key ? from : null;
    }

    /// <summary>Dev: opens an editor by its key ("s1/m1", "slots/3", "slots/every") without keys.</summary>
    internal static void DevEdit(string key)
    {
        View.Selected = key;
        Edit(key);
    }

    /// <summary>Closes the open editor (Close, Esc): back to the page a link opened it from, else to the library.</summary>
    internal static void CloseEditor()
    {
        s_editing = s_cameFrom;
        s_cameFrom = null;
    }

    // ---- Editors ----

    private static readonly Dictionary<string, LibraryEditor> s_editors = new();

    private static LibraryEditor? EditorFor(string key)
    {
        if (NamedIdOfPage(key) is { } named && !NamedConditions.IsNamed(S.NamedConditions.Doc, named))
        {
            // Deleted (or undone away): back to where it was opened from.
            s_editors.Remove(key);
            CloseEditor();
            return s_editing != null ? EditorFor(s_editing) : null;
        }
        if (s_editors.TryGetValue(key, out var editor)) return editor;
        if (key == EveryKey) editor = EveryEditor();
        else if (IsNamedPage(key)) editor = NamedEditor(key);
        else if (key.StartsWith(SlotsKey + "/") && int.TryParse(key[(SlotsKey.Length + 1)..], out int slot) && slot is >= 1 and <= SlotMacros.Slots) editor = SlotEditor(slot - 1);
        else editor = MacroEditor(key);
        if (editor == null)
        {
            // Gone (deleted, another class): back to the library.
            s_editing = null;
            s_cameFrom = null;
            return null;
        }
        s_editors[key] = editor;
        return editor;
    }

    private static readonly string[] s_slotMoveLabels = { "don't care", "moving", "standing still" };

    // A condition setting's list as the rule tree rows read and change it (the slot lists, the shared conditions).
    private static TreeAccess ListTree(string prefix, ConditionSetting setting, Func<string> key, Func<int> slot, bool readOnly = false) => new()
    {
        Prefix = prefix,
        List = () => setting.List(key()),
        Edit = change => setting.Edit(key(), list =>
        {
            change(list);
            return list;
        }, out _),
        State = () => LiveList(prefix, setting.List(key()), slot()),
        Blocked = readOnly ? () => "edit these under Shared Conditions" : () => setting.Doc.ReadOnly ? "made by a newer version" : null,
        Kept = entry => setting == S.GateConditions && entry.Id == S.GateEntryId ? "The enemy gate can be switched off, not removed." : null,
        // The enemy gate is set on Config › Macros › Enemies.
        Hidden = entry => setting == S.GateConditions && entry.Id == S.GateEntryId,
        Open = s_openLine is { } open && open.StartsWith(prefix + "/") ? open[(prefix.Length + 1)..] : null,
        SetOpen = path => s_openLine = path == null ? null : prefix + "/" + path,
    };

    private static readonly Dictionary<string, TreeState> s_liveLists = new();

    // A slot list's (or the shared conditions') live state, checked once a frame while shown.
    private static TreeState? LiveList(string prefix, ConditionList list, int slot)
    {
        if (!Player.Exists) return null;
        if (!s_liveLists.TryGetValue(prefix, out var state)) s_liveLists[prefix] = state = new TreeState();
        if (state.Frame != UnityEngine.Time.frameCount)
        {
            Conditions.Check(list, SlotMacros.ContextFor(slot), state);
            state.Frame = UnityEngine.Time.frameCount;
        }
        LiveFrame();
        return state;
    }

    private static (string Key, int Gate, int Slots, int Targets, string? Open)? s_listBuiltFor;
    private static List<MenuRow> s_listRows = new();

    // Rows rebuilt when a list's text, the slot targets or the open line changes.
    private static IReadOnlyList<MenuRow> ListRows(string key, Action<List<MenuRow>> build)
    {
        var built = (key, S.GateConditions.Value.GetHashCode(), S.SlotConditions.Value.GetHashCode(), S.SlotTargets.Value.GetHashCode(), s_openLine);
        if (s_listBuiltFor == built) return s_listRows;
        s_listBuiltFor = built;
        var rows = new List<MenuRow>();
        build(rows);
        s_listRows = rows;
        return rows;
    }

    private static LibraryEditor EveryEditor()
    {
        var editor = new LibraryEditor(EveryKey, () => "Shared Conditions", () => ListRows(EveryKey, rows =>
        {
            rows.Add(new HintRow(() => "No live data: enter the game to see values.") { Key = "nolive", VisibleWhen = () => !Player.Exists });
            rows.Add(new HintRow(() => "Checked for every slot macro before its own conditions, after the enemy gate (Config › Macros › Enemies).") { Key = "hint" });
            RuleTreeRows.Add(rows, ListTree("shared", S.GateConditions, () => S.EveryList, () => -1));
            rows.Add(new HeadingRow("Shared target"));
            rows.Add(new HintRow(() => "Where slot macros aim, unless a slot macro sets its own target.") { Key = "target.hint" });
            rows.AddRange(MacroSettingsPage.SharedTargetRows());
        }))
        { Place = () => "Skill Slots" };
        editor.Buttons.Add(Button("Undo", "undo", Undo, () => History.CanUndo, "Takes back the last change on this tab"));
        editor.Buttons.Add(Button("Close", "close", CloseEditor));
        return editor;
    }

    // A slot macro: trigger and sequence fixed, the shared conditions shown read-only with a link, then its own tree,
    // movement and wait between uses (stored beside the list's "all").
    private static LibraryEditor SlotEditor(int slot)
    {
        string key = $"{SlotsKey}/{slot + 1}";
        Func<string> listKey = () => SlotMacros.SlotKey(SlotMacros.CurrentFormKey, slot);
        var editor = new LibraryEditor(key, () => SlotMacros.SlotAbilityName(slot) ?? "No skill in this slot", () => SlotRows(key, slot, listKey))
        {
            Badge = () => (slot + 1).ToString(),
            Place = () => "Skill Slots",
        };
        editor.Buttons.Add(Button("Undo", "undo", Undo, () => History.CanUndo, "Takes back the last change on this tab"));
        editor.Buttons.Add(Button("Duplicate as a macro", "dupe", () => DuplicateAsMacro(slot), () => DuplicateTarget() != null,
            "Copies this slot macro into a named set as a macro of its own"));
        editor.Buttons.Add(Button("Close", "close", CloseEditor));
        return editor;
    }

    // "Slot macro · human form": what the slot editor edits, with the form whose list it is.
    private static string SlotContext(int slot)
    {
        if (!Player.Exists) return "Slot macro · not in game";
        int form = SlotMacros.CurrentFormKey;
        return $"Slot macro · {(form == -1 ? "human form" : $"form {form}")}";
    }

    private static IReadOnlyList<MenuRow> SlotRows(string key, int slot, Func<string> listKey) => ListRows(key, rows =>
        {
            rows.Add(new HintRow(() => SlotContext(slot)) { Key = "slot" });
            rows.Add(new HintRow(() => "No live data: enter the game to see values.") { Key = "nolive", VisibleWhen = () => !Player.Exists });
            rows.Add(new HeadingRow("Pre-conditions"));
            rows.Add(new HintRow(() => "Trigger: automatic while the slot is toggled.") { Key = "trigger" });
            rows.Add(new ValueChoice("Movement", () => Array.IndexOf(Macro.Moves, ListField(listKey(), "move") ?? "any"), i =>
            {
                if (i >= 0 && i < Macro.Moves.Length) SetListField(listKey(), "move", i == 0 ? null : System.Text.Json.Nodes.JsonValue.Create(Macro.Moves[i]));
            }, () => s_slotMoveLabels) { Key = "move", Width = 150f });
            rows.Add(new ValueNumber("Wait between uses", () => float.TryParse(ListField(listKey(), "waitMs"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ms) ? ms : 0f,
                v => SetListField(listKey(), "waitMs", v <= 0f ? null : System.Text.Json.Nodes.JsonValue.Create(v)), 0f, Macro.MaxWaitMs, 100f, " ms") { Key = "waitms" });
            rows.Add(new HeadingRow("Shared Conditions"));
            RuleTreeRows.Add(rows, ListTree("sharedview", S.GateConditions, () => S.EveryList, () => slot, readOnly: true));
            rows.Add(Button("Edit Shared Conditions", "editshared", () => EditFromHere(EveryKey)));
            rows.Add(new HeadingRow("Trigger"));
            RuleTreeRows.Add(rows, ListTree($"slot{slot + 1}", S.SlotConditions, listKey, () => slot));
            rows.Add(new HeadingRow("Sequence"));
            rows.Add(new HintRow(() => $"1. Cast slot {slot + 1} ({SlotMacros.SlotAbilityName(slot) ?? "whatever is there"}), tapped, or held while it channels.") { Key = "sequence" });
            // Its own target for this form, or the shared one (set on the Shared Conditions page).
            rows.Add(new HeadingRow("Target"));
            rows.Add(new HintRow(() => SlotContext(slot)) { Key = "target.form" });
            var targetRows = TargetRows.Rows(new TargetRows.Access
            {
                Prefix = $"slot{slot + 1}.",
                Owner = TargetOwner.Slot,
                Get = () => S.SlotTargets.Slot(listKey()),
                Set = target =>
                {
                    History.Watch();
                    if (!S.SlotTargets.SetSlot(listKey(), target, out string reason)) Report($"Target not changed: {reason}.", ToastKind.Warning);
                },
                SameAs = () => S.SharedSlotTarget,
                StartRange = () => S.SharedSlotTarget.Pick == TargetPick.Cursor ? S.StartRange : S.SharedSlotTarget.Range,
                Blocked = () => S.SlotTargets.Doc.ReadOnly ? "made by a newer version" : null,
            }).ToList();
            rows.Add(targetRows[0]);
            // While the slot follows the shared target: its words, and Edit to open it.
            rows.Add(new TextButtonRow("Shared target", () => S.SharedSlotTarget.Words(TargetOwner.Slot), new ActionButton("Edit", "editsharedtarget")
            {
                OnClick = () => EditFromHere(EveryKey),
                Tip = "Opens the shared target on the Shared Conditions page",
            })
            {
                Key = "target.shared",
                VisibleWhen = () => S.SlotTargets.Slot(listKey()) == null,
            });
            rows.AddRange(targetRows.Skip(1));
        });

    // Where "Duplicate as a macro" puts the copy: the shown class's first set with room, or a new set; null when the
    // library can't take one.
    private static string? DuplicateTarget()
    {
        var doc = S.LibraryDoc.Doc;
        if (doc.ReadOnly) return null;
        if (doc.SetsOf(PickedClass).FirstOrDefault(s => !s.Full && s.Invalid == null) is { } set) return set.Id;
        return doc.SetsOf(PickedClass).Count() < MacroSet.MaxPerClass ? "new" : null;
    }

    // A slot macro as a library macro (R12): automatic, wait between uses and movement from the slot's list, its skill
    // ticked in must be ready, its tree the shared conditions (each with its switch) then the slot's own lines, and one
    // cast step. "This slot" in a copied line becomes the slot's number. The slot itself isn't touched.
    private static void DuplicateAsMacro(int slot)
    {
        string? skill = PlayerModel.Slot(slot + 1)?.AbilityId;
        if (skill == null)
        {
            Report($"Slot {slot + 1} has no skill to copy.", ToastKind.Warning);
            return;
        }
        string listKey = SlotMacros.SlotKey(SlotMacros.CurrentFormKey, slot);
        var own = S.SlotConditions.List(listKey);
        var shared = S.GateConditions.List(S.EveryList);
        if (own.Invalid != null || shared.Invalid != null)
        {
            Report("Can't copy: a list can't be read here.", ToastKind.Warning);
            return;
        }
        var items = ConditionDoc.ItemsJson(shared);
        foreach (var item in ConditionDoc.ItemsJson(own)) items.Add(ConditionDoc.Clone(item));
        var tree = ConditionDoc.ParseItems(items);
        foreach (var node in tree.Nodes)
        {
            if (node.Args["slot"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out double n) && n == 0) node.Args["slot"] = slot + 1;
        }
        if (tree.NodeCount > ConditionList.Limit)
        {
            Report($"Can't copy: together the lists have more than {ConditionList.Limit} lines.", ToastKind.Warning);
            return;
        }
        string cls = PickedClass;
        string? made = null;
        EditLibrary(doc =>
        {
            var set = doc.SetsOf(cls).FirstOrDefault(s => !s.Full && s.Invalid == null);
            if (set == null)
            {
                if (doc.SetsOf(cls).Count() >= MacroSet.MaxPerClass) return;
                set = new MacroSet { Id = doc.NewId("s"), Name = doc.FreeSetName("From slots", cls), Class = cls, On = true };
                doc.Sets.Add(set);
            }
            string name = $"Slot {slot + 1}: {ConditionKinds.SkillName(skill)}";
            var macro = new Macro
            {
                Id = doc.NewId("m"),
                Name = name.Length > Macro.MaxName ? name[..Macro.MaxName] : name,
                On = true,
                Move = ListField(listKey, "move") is { } move && Macro.Moves.Contains(move) ? move : "any",
                WaitMs = float.TryParse(ListField(listKey, "waitMs"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ms) ? ms : 0f,
                All = ConditionDoc.ParseItems(ConditionDoc.ItemsJson(tree)),
            };
            // The cast step aims where the slot did; "don't cast" becomes "skip the step".
            var step = MacroStep.New("cast", skill);
            var target = S.EffectiveSlotTarget(SlotMacros.CurrentFormKey, slot);
            step.SetTarget(target.Else == "hold" ? target.WithElse("skip") : target);
            macro.Actions.Add(step);
            macro.SyncReady();
            set.Macros.Add(macro);
            made = $"{set.Id}/{macro.Id}";
        });
        if (made == null) return;
        Report($"Copied slot {slot + 1} as a macro; the slot is unchanged.", ToastKind.Info);
        View.Selected = made;
        Edit(made);
    }

    // A slot list's field beside "all" ("move", "waitMs") as text, or null.
    private static string? ListField(string key, string name) => S.SlotConditions.List(key).Field(name) switch
    {
        System.Text.Json.Nodes.JsonValue v when v.TryGetValue(out string? text) => text,
        System.Text.Json.Nodes.JsonValue v when v.TryGetValue(out double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    private static void SetListField(string key, string name, System.Text.Json.Nodes.JsonNode? value)
    {
        S.SlotConditions.Edit(key, list =>
        {
            list.Extra.RemoveAll(e => e.Key == name);
            if (value != null) list.Extra.Add(new(name, value));
            return list;
        }, out _);
    }

    // ---- Sets and macros ----

    private static string? Hint(LibraryLine? line)
    {
        if (S.LibraryDoc.Doc.ReadOnly) return "This library was made by a newer version of Epoch Assist; it is kept as it is and can't be changed here.";
        if (line == null) return null;
        if (line.Key == SlotsKey) return "Slot macros cast whatever is in their slot while toggled. Toggle a slot with its tick box, or hold the modifier and press the slot's key.";
        if (line.Key == EveryKey) return "Checked before each slot macro's own conditions, after the enemy gate.";
        if (line.Key.StartsWith(SlotsKey + "/")) return "Ticked: the slot casts its skill when ready and its conditions pass.";
        if (IsNamedPage(line.Key)) return NamedHint(line.Key);
        var (setId, macroId) = Split(line.Key);
        var (set, macro) = S.LibraryDoc.Doc.Find(setId, macroId);
        if (macro != null) return macro.Invalid is { } invalid ? $"Never starts: {invalid}." : null;
        if (set != null && set.Full) return $"{MacroSet.MaxMacros} macros per set at most.";
        return null;
    }

    private static void NewSet()
    {
        string cls = PickedClass;
        string? made = null;
        if (!EditLibrary(doc =>
        {
            if (doc.SetsOf(cls).Count() >= MacroSet.MaxPerClass) return;
            var set = new MacroSet { Id = doc.NewId("s"), Name = doc.FreeSetName("New set", cls), Class = cls, On = true };
            doc.Sets.Add(set);
            made = set.Id;
        }) || made == null) return;
        if (View.Line(made) is { } line) View.BeginRename(line);
    }

    private static IReadOnlyList<ActionButton> SetActions(string setId)
    {
        bool editable = !S.LibraryDoc.Doc.ReadOnly;
        return new[]
        {
            Button("New macro", "newmacro", () => NewMacro(setId), () => editable && S.LibraryDoc.Doc.Find(setId).Set is { Full: false }),
            Button("Rename", "rename", () => BeginRename(setId), () => editable),
            Button("Duplicate", "duplicate", () => DuplicateSet(setId),
                () => editable && S.LibraryDoc.Doc.Find(setId).Set is { } set && S.LibraryDoc.Doc.SetsOf(set.Class).Count() < MacroSet.MaxPerClass),
            Button("Copy share code", "copy", () => CopyCode(setId), () => S.LibraryDoc.Doc.Find(setId).Set is { Invalid: null }),
            S.LibraryDoc.Doc.Find(setId).Set is { Class: MacroSet.AllClasses }
                ? Button($"Only {PickedClass}", "class", () => SetClassOf(setId, PickedClass), () => editable && CanTakeClass(PickedClass),
                    $"The set shows and runs only for {PickedClass}")
                : Button("For all classes", "class", () => SetClassOf(setId, MacroSet.AllClasses), () => editable && CanTakeClass(MacroSet.AllClasses),
                    "The set shows and runs for every class"),
            Button("Move up", "up", () => MoveSet(setId, -1), () => editable && NeighbourSet(setId, -1) != null),
            Button("Move down", "down", () => MoveSet(setId, 1), () => editable && NeighbourSet(setId, 1) != null),
            Button("Delete", "delete", () => DeleteSet(setId), () => editable, "Undo brings it back"),
        };
    }

    private static IReadOnlyList<ActionButton> MacroActions(string setId, string macroId)
    {
        bool editable = !S.LibraryDoc.Doc.ReadOnly;
        string key = $"{setId}/{macroId}";
        return new[]
        {
            Button("Edit", "edit", () => Edit(key)),
            Button("Rename", "rename", () => BeginRename(key), () => editable),
            Button("Duplicate", "duplicate", () => DuplicateMacro(setId, macroId), () => editable && S.LibraryDoc.Doc.Find(setId).Set is { Full: false }),
            Button("Move up", "up", () => MoveMacro(setId, macroId, -1), () => editable && CanMoveMacro(setId, macroId, -1)),
            Button("Move down", "down", () => MoveMacro(setId, macroId, 1), () => editable && CanMoveMacro(setId, macroId, 1)),
            Button("Delete", "delete", () => DeleteMacro(setId, macroId), () => editable, "Undo brings it back"),
        };
    }

    private static void BeginRename(string key)
    {
        if (View.Line(key) is { } line) View.BeginRename(line);
    }

    private static void Rename(LibraryLine line, string name)
    {
        if (NamedIdOfPage(line.Key) is { } named)
        {
            Rename(named, name);
            if (s_nameProblem != null) Report($"Not renamed: {s_nameProblem}.", ToastKind.Warning);
            return;
        }
        if (IsNamedPage(line.Key)) return;
        if (name.Length > Macro.MaxName) name = name[..Macro.MaxName];
        var (setId, macroId) = Split(line.Key);
        EditLibrary(doc =>
        {
            var (set, macro) = doc.Find(setId, macroId);
            if (macro != null) macro.Name = name;
            else if (set != null) set.Name = name;
        });
    }

    private static void NewMacro(string setId)
    {
        string? made = null;
        if (!EditLibrary(doc =>
        {
            if (doc.Find(setId).Set is not { Full: false } set) return;
            var macro = new Macro { Id = doc.NewId("m"), Name = "New macro", On = true };
            set.Macros.Add(macro);
            made = macro.Id;
        }) || made == null) return;
        View.Selected = $"{setId}/{made}";
        Edit(View.Selected);
    }

    private static bool CanTakeClass(string cls) => S.LibraryDoc.Doc.SetsOf(cls).Count() < MacroSet.MaxPerClass;

    // Moves a set between its class and all classes; its place in library order stays.
    private static void SetClassOf(string setId, string cls)
    {
        EditLibrary(doc =>
        {
            if (doc.Find(setId).Set is not { } set || set.Class == cls || doc.SetsOf(cls).Count() >= MacroSet.MaxPerClass) return;
            set.Name = doc.FreeSetName(set.Name, cls);
            set.Class = cls;
        });
    }

    private static void DuplicateSet(string setId)
    {
        EditLibrary(doc =>
        {
            if (doc.Find(setId).Set is not { } set || doc.SetsOf(set.Class).Count() >= MacroSet.MaxPerClass) return;
            var copy = MacroDoc.ParseSet(MacroDoc.SetJson(set));
            doc.Renumber(copy);
            copy.Name = doc.FreeSetName(set.Name, set.Class);
            doc.Sets.Insert(doc.Sets.IndexOf(set) + 1, copy);
        });
    }

    private static void DuplicateMacro(string setId, string macroId)
    {
        EditLibrary(doc =>
        {
            var (set, macro) = doc.Find(setId, macroId);
            if (set == null || macro == null || set.Full) return;
            var holder = MacroDoc.ParseSet(new System.Text.Json.Nodes.JsonObject { ["macros"] = new System.Text.Json.Nodes.JsonArray(MacroJsonOf(set, macro)) });
            var copy = holder.Macros[0];
            copy.Id = doc.NewId("m");
            copy.Name = macro.Name.Length + 4 <= Macro.MaxName ? macro.Name + " (2)" : macro.Name;
            set.Macros.Insert(set.Macros.IndexOf(macro) + 1, copy);
        });
    }

    // One macro's stored form (through its set's).
    private static System.Text.Json.Nodes.JsonNode? MacroJsonOf(MacroSet set, Macro macro)
    {
        var json = MacroDoc.SetJson(set) as System.Text.Json.Nodes.JsonObject;
        var macros = json?["macros"] as System.Text.Json.Nodes.JsonArray;
        int index = set.Macros.IndexOf(macro);
        var node = macros != null && index >= 0 ? macros[index] : null;
        return node != null ? ConditionDoc.Clone(node) : null;
    }

    // The next set of the same class up or down in library order, or null.
    private static MacroSet? NeighbourSet(string setId, int direction)
    {
        var doc = S.LibraryDoc.Doc;
        if (doc.Find(setId).Set is not { } set) return null;
        var same = doc.SetsOf(set.Class).ToList();
        int index = same.IndexOf(set) + direction;
        return index >= 0 && index < same.Count ? same[index] : null;
    }

    private static void MoveSet(string setId, int direction)
    {
        EditLibrary(doc =>
        {
            if (doc.Find(setId).Set is not { } set) return;
            var same = doc.SetsOf(set.Class).ToList();
            int index = same.IndexOf(set) + direction;
            if (index < 0 || index >= same.Count) return;
            int a = doc.Sets.IndexOf(set), b = doc.Sets.IndexOf(same[index]);
            (doc.Sets[a], doc.Sets[b]) = (doc.Sets[b], doc.Sets[a]);
        });
    }

    // Within its set, or past the end into the neighbouring set of the class.
    private static bool CanMoveMacro(string setId, string macroId, int direction)
    {
        var (set, macro) = S.LibraryDoc.Doc.Find(setId, macroId);
        if (set == null || macro == null) return false;
        int index = set.Macros.IndexOf(macro) + direction;
        if (index >= 0 && index < set.Macros.Count) return true;
        return NeighbourSet(setId, direction) is { Full: false };
    }

    private static void MoveMacro(string setId, string macroId, int direction)
    {
        string? movedTo = null;
        EditLibrary(doc =>
        {
            var (set, macro) = doc.Find(setId, macroId);
            if (set == null || macro == null) return;
            int index = set.Macros.IndexOf(macro);
            int target = index + direction;
            if (target >= 0 && target < set.Macros.Count)
            {
                (set.Macros[index], set.Macros[target]) = (set.Macros[target], set.Macros[index]);
                return;
            }
            var same = doc.SetsOf(set.Class).ToList();
            int other = same.IndexOf(set) + direction;
            if (other < 0 || other >= same.Count || same[other].Full) return;
            set.Macros.Remove(macro);
            if (direction < 0) same[other].Macros.Add(macro);
            else same[other].Macros.Insert(0, macro);
            movedTo = same[other].Id;
        });
        if (movedTo != null) View.Selected = $"{movedTo}/{macroId}";
    }

    // No confirmation: undo brings a deleted set or macro back (FR-005).
    private static void DeleteSet(string setId) => EditLibrary(doc => doc.Sets.RemoveAll(s => s.Id == setId));

    private static void DeleteMacro(string setId, string macroId) => EditLibrary(doc =>
    {
        if (doc.Find(setId).Set is { } set) set.Macros.RemoveAll(m => m.Id == macroId);
    });

    // ---- Undo ----

    /// <summary>Takes back the last change on the Macros tab (library, shared conditions or slot lists).</summary>
    internal static void Undo()
    {
        if (History.Undo(out string what)) Toasts.Show("macros", $"Undone: {what}.", ToastKind.Info);
    }

    /// <summary>
    /// Up to 50 snapshots this session of the library, the shared conditions, the slot lists, the slot targets and the named
    /// conditions (R14): a change seen on
    /// the Macros tab pushes the texts from before it, and Undo puts the last back through the settings. Cleared when
    /// the profile or character changes; never saved.
    /// </summary>
    internal static class History
    {
        private const int Max = 50;
        private static readonly List<(string Library, string Gate, string Slots, string Targets, string Named)> s_stack = new();
        private static (string Library, string Gate, string Slots, string Targets, string Named)? s_last;
        private static int s_profileGeneration = -1;

        public static bool CanUndo => s_stack.Count > 0;
        public static int Count => s_stack.Count;

        private static (string, string, string, string, string) Now() =>
            (S.LibraryDoc.Value, S.GateConditions.Value, S.SlotConditions.Value, S.SlotTargets.Value, S.NamedConditions.Value);

        /// <summary>Notes the texts now; a change since the last look pushes the texts from before it.</summary>
        public static void Watch()
        {
            if (Profiles.Generation != s_profileGeneration)
            {
                s_profileGeneration = Profiles.Generation;
                s_stack.Clear();
                s_last = null;
            }
            var now = Now();
            if (s_last is { } last && last != now)
            {
                s_stack.Add(last);
                if (s_stack.Count > Max) s_stack.RemoveAt(0);
            }
            s_last = now;
        }

        public static bool Undo(out string what)
        {
            what = string.Empty;
            Watch();
            if (s_stack.Count == 0) return false;
            var (library, gate, slots, targets, named) = s_stack[^1];
            s_stack.RemoveAt(s_stack.Count - 1);
            var (nowLibrary, nowGate, nowSlots, nowTargets, nowNamed) = Now();
            var parts = new List<string>();
            if (library != nowLibrary && S.LibraryDoc.SetText(library, out _)) parts.Add("library");
            if (gate != nowGate && S.GateConditions.SetText(gate, out _)) parts.Add("shared conditions");
            if (slots != nowSlots && S.SlotConditions.SetText(slots, out _)) parts.Add("slot conditions");
            if (targets != nowTargets && S.SlotTargets.SetText(targets, out _)) parts.Add("slot targets");
            if (named != nowNamed && S.NamedConditions.SetText(named, out _)) parts.Add("named conditions");
            s_last = Now();
            what = parts.Count > 0 ? string.Join(", ", parts) : "nothing changed";
            return true;
        }
    }

    // ---- Share codes ----

    private const float BannerSeconds = 12f;
    private static string? s_banner;
    private static float s_bannerAt;

    private static void Report(string text, ToastKind kind)
    {
        s_banner = text;
        s_bannerAt = Clock.Now;
        Toasts.Show("macros", text, kind);
    }

    private static void CopyCode(string setId)
    {
        if (S.LibraryDoc.Doc.Find(setId).Set is not { } set) return;
        var (code, error) = ShareCode.Encode(set, S.NamedConditions.Doc);
        if (code == null)
        {
            Report($"Can't copy: {error}.", ToastKind.Warning);
            return;
        }
        Clipboard.Text = code;
        Report("Share code copied.", ToastKind.Info);
    }

    private static void Paste() => Report(Import(Clipboard.Text), ToastKind.Info);

    /// <summary>
    /// Adds the set in a share code: new ids, switched off, " (2)" on a name its class already has, with the named
    /// conditions it carries (reused when the player has the same one, else added; a name the player has with other
    /// lines is added as "&lt;name&gt; (2)" and offers Use mine). The result names what was added and the parts this build
    /// keeps without reading. Nothing is added for a bad code.
    /// </summary>
    internal static string Import(string text)
    {
        var decoded = ShareCode.Decode(text);
        if (decoded.Set is not { } set) return $"Nothing added: {decoded.Error}.";
        ConditionDoc? merged = null;
        var namedReport = new List<string>();
        var clashes = new List<NamedConditions.Clash>();
        if (decoded.Named is { } named && NamedConditions.Count(named) > 0)
        {
            if (S.NamedConditions.Doc.ReadOnly) return "Nothing added: the named conditions were made by a newer version and can't be changed here.";
            merged = NamedConditions.Merge(S.NamedConditions.Doc, named, set.Name, out var map, namedReport, clashes, out string reason);
            if (merged == null) return $"Nothing added: {reason}.";
            set = MacroDoc.RemapNamed(set, map);
        }
        string? added = null, refused = null;
        bool stored = EditLibrary(doc =>
        {
            if (doc.SetsOf(set.Class).Count() >= MacroSet.MaxPerClass)
            {
                refused = $"{MacroSet.ClassLabel(set.Class)} already has {MacroSet.MaxPerClass} sets";
                return;
            }
            doc.Renumber(set);
            set.On = false;
            set.Name = doc.FreeSetName(set.Name.Length > 0 ? set.Name : "Shared set", set.Class);
            doc.Sets.Add(set);
            added = set.Name;
        });
        if (!stored) return "Nothing added: the library can't be changed.";
        if (refused != null) return $"Nothing added: {refused}.";
        string namedNote = string.Empty;
        if (merged != null)
        {
            if (!S.NamedConditions.SetText(merged.ToText(), out string reason)) namedNote = $" Its named conditions weren't added: {reason}.";
            else
            {
                namedNote = $" Named conditions: {string.Join("; ", namedReport)}.";
                foreach (var clash in clashes) Clashes().Add(clash with { SetName = added ?? set.Name });
            }
        }
        string unknown = decoded.Unknown.Count > 0 ? $" Kept without reading: {string.Join(", ", decoded.Unknown)}." : string.Empty;
        string classNote = set.KnownClass ? string.Empty : " Its class isn't known here, so it never runs.";
        return $"Added \"{added}\" ({MacroSet.ClassLabel(set.Class)}), switched off.{classNote}{namedNote}{unknown}";
    }
}
