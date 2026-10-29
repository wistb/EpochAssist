using System.Text.Json.Nodes;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// One line of a rule tree or step list (contracts/menu.md): its depth and tree-line flags, its words, an optional
/// number ("1."), its switch, its live value and state shape, a group's type dropdown, and buttons to open its options
/// and remove it. Clicking the words or Enter opens the line's options under it.
/// </summary>
internal sealed class TreeLine : MenuRow
{
    public int Depth { get; init; }
    /// <summary>The line is its parent's last item (its tree line stops here).</summary>
    public bool Last { get; init; }
    /// <summary>Per ancestor level from the root's items down: whether that ancestor was its parent's last.</summary>
    public bool[] Ancestors { get; init; } = Array.Empty<bool>();
    public string? Number { get; init; }
    public Func<string> Words { get; init; } = () => string.Empty;
    public Func<bool>? On { get; init; }
    public Action<bool>? SetOn { get; init; }
    /// <summary>The live value and state; null hides them (steps, out of game).</summary>
    public Func<(string Value, StatusState State)?>? Live { get; init; }
    /// <summary>A group line's type (0 all of, 1 any of, 2 none of) and its setter; null for other lines.</summary>
    public Func<int>? Type { get; init; }
    public Action<int>? SetType { get; init; }
    public bool Opened { get; init; }
    /// <summary>Opens or closes the line's options; null for lines that have none (groups).</summary>
    public Action? Toggle { get; init; }
    public Action? Remove { get; init; }
    /// <summary>Why the line can't be removed or changed (a newer library), or null.</summary>
    public Func<string?>? Blocked { get; init; }
    public Func<string?>? Tip { get; init; }
    protected override string DefaultKey => "line";

    public static readonly string[] TypeLabels = { "all of", "any of", "none of" };
}

/// <summary>"+ add a condition" (a searchable list grouped by subject) and an optional second button ("+ add a group")
/// under a group or list, with the reason when the limit is reached.</summary>
internal sealed class TreeAdd : MenuRow
{
    public int Depth { get; init; }
    public string Label { get; init; } = "+ add a condition";
    public Func<IReadOnlyList<string>> Choices { get; init; } = () => Array.Empty<string>();
    public Func<(string Label, int First)[]?>? Groups { get; init; }
    public Action<int> Picked { get; init; } = _ => { };
    public Func<string?>? Blocked { get; init; }
    public string? SecondLabel { get; init; }
    public Action? Second { get; init; }
    public Func<string?>? SecondBlocked { get; init; }
    protected override string DefaultKey => "add";
}

/// <summary>Another row drawn indented to a tree depth (the options of an open line).</summary>
internal sealed class Indented : MenuRow
{
    public Indented(MenuRow inner, int depth)
    {
        Inner = inner;
        Depth = depth;
    }

    public MenuRow Inner { get; }
    public int Depth { get; }
    protected override string DefaultKey => Inner.Key;
    public override bool Visible => Inner.Visible && base.Visible;
}

/// <summary>A one-line text field bound to a value (a macro's name, a message), committed as it is typed.</summary>
internal sealed class ValueTextField : MenuRow
{
    public ValueTextField(string label, Func<string> get, Action<string> set, int maxLength)
    {
        Label = label;
        Get = get;
        Set = set;
        MaxLength = maxLength;
    }

    public string Label { get; }
    public Func<string> Get { get; }
    public Action<string> Set { get; }
    public int MaxLength { get; }
    /// <summary>The field's width inline in a list entry, where it shows with no label.</summary>
    public float InlineWidth { get; init; } = 220f;
    protected override string DefaultKey => "text." + MenuEntry.Slug(Label);
}

/// <summary>
/// How a rule tree is read and changed: the stored tree (null when there is none), an edit that changes a fresh copy
/// and stores it (false when it can't), the live state for values (null out of game), and the open line's path.
/// </summary>
internal sealed class TreeAccess
{
    public Func<ConditionList?> List { get; init; } = () => null;
    public Func<Action<ConditionList>, bool> Edit { get; init; } = _ => false;
    public Func<TreeState?> State { get; init; } = () => null;
    public Func<string?> Blocked { get; init; } = () => null;
    /// <summary>Key prefix of its rows ("trigger", "until.2").</summary>
    public string Prefix { get; init; } = "tree";
    public string? Open { get; init; }
    public Action<string?> SetOpen { get; init; } = _ => { };
    /// <summary>Called before any change (undo).</summary>
    public Action? BeforeChange { get; init; }
    /// <summary>Why a line can't be removed (a kept entry: the enemy gate), or null.</summary>
    public Func<ConditionEntry, string?>? Kept { get; init; }
    /// <summary>A top-level line left out of the tree (still checked): one with a page of its own, the enemy gate.</summary>
    public Func<ConditionEntry, bool>? Hidden { get; init; }
    /// <summary>The check context for live values ("this slot" args read the slot).</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Builds the rows of a rule tree from its stored shape: a line per group and condition, "+ add" rows under each group,
/// and the open condition's options (each argument, "True when unresolvable", "For at least", "Show chart on overlay",
/// its value). Lines name their nodes by path ("2.1"), so the rows built from one stored text work on its copies.
/// </summary>
internal static class RuleTreeRows
{
    private static readonly List<ConditionKind> s_kinds = new();
    private static readonly List<string> s_kindLabels = new();
    private static (string Label, int First)[] s_kindGroups = Array.Empty<(string, int)>();

    // The "+ add a condition" list: every kind grouped by subject, with mode notes.
    private static void Kinds()
    {
        if (s_kinds.Count > 0) return;
        var groups = new List<(string, int)>();
        foreach (var (heading, kinds) in ConditionKinds.BySubject())
        {
            groups.Add((heading, s_kinds.Count));
            foreach (var kind in kinds)
            {
                s_kinds.Add(kind);
                s_kindLabels.Add(kind.Label);
            }
        }
        s_kindGroups = groups.ToArray();
    }

    /// <summary>A node's path: its place in each group down from the root, from 1 ("2.1").</summary>
    public static string PathOf(ConditionList list, int node)
    {
        var places = new List<int>();
        for (int n = node; n >= 0; n = list.Parent(n)) places.Add(list.PlaceInParent(n));
        places.Reverse();
        return string.Join(".", places);
    }

    /// <summary>The node at a path in a list (its own copy), or null.</summary>
    public static ConditionEntry? NodeAt(ConditionList list, string path, out List<ConditionEntry>? siblings)
    {
        siblings = null;
        var items = list.Entries;
        ConditionEntry? node = null;
        foreach (string part in path.Split('.'))
        {
            if (!int.TryParse(part, out int place) || place < 1 || place > items.Count) return null;
            siblings = items;
            node = items[place - 1];
            if (node.Group != null) items = node.Group.Items;
        }
        return node;
    }

    /// <summary>The items of the group at a path ("" for the root).</summary>
    private static List<ConditionEntry>? ItemsAt(ConditionList list, string path) =>
        path.Length == 0 ? list.Entries : NodeAt(list, path, out _)?.Group?.Items;

    public static void Add(List<MenuRow> rows, TreeAccess access)
    {
        Kinds();
        var list = access.List();
        if (list == null) return;
        if (list.Invalid != null)
        {
            rows.Add(new HintRow(() => $"Can't be read here: {list.Invalid}") { Key = access.Prefix + ".invalid" });
            return;
        }
        var nodes = list.Nodes;
        // The root's "all of" heading line, with the tree's verdict.
        rows.Add(new TreeLine
        {
            Key = access.Prefix + ".root",
            Depth = 0,
            Last = true,
            Words = () => "all of",
            Live = () => Verdict(access),
        });
        AddItems(rows, access, list, -1, 1, Array.Empty<bool>());
    }

    // The items of a group (the root at -1), each line then a group's items, then the group's add row.
    private static void AddItems(List<MenuRow> rows, TreeAccess access, ConditionList list, int group, int depth, bool[] ancestors)
    {
        int end = group < 0 ? list.NodeCount : list.End(group);
        var children = new List<int>();
        for (int i = group + 1; i < end; i = list.End(i))
        {
            if (group >= 0 || access.Hidden?.Invoke(list.Nodes[i]) != true) children.Add(i);
        }
        string groupPath = group < 0 ? string.Empty : PathOf(list, group);
        for (int c = 0; c < children.Count; c++)
        {
            int node = children[c];
            bool last = false;
            var entry = list.Nodes[node];
            string path = PathOf(list, node);
            rows.Add(Line(access, list, node, path, depth, last, ancestors));
            if (entry.Group != null)
            {
                AddItems(rows, access, list, node, depth + 1, ancestors.Append(last).ToArray());
            }
            else if (access.Open == path && entry.KindDef != null)
            {
                foreach (var row in Options(access, path, entry.KindDef)) rows.Add(new Indented(row, depth + 1));
            }
        }
        rows.Add(AddRow(access, list, groupPath, depth));
    }

    private static (string, StatusState)? Verdict(TreeAccess access)
    {
        if (access.State() is not { } state || state.List == null) return null;
        return state.Verdict.Passed ? ("pass", StatusState.Active) : ("fail", StatusState.Paused);
    }

    private static TreeLine Line(TreeAccess access, ConditionList list, int node, string path, int depth, bool last, bool[] ancestors)
    {
        var entry = list.Nodes[node];
        bool group = entry.Group != null;
        // Each reads the stored tree again, so live values and words follow edits made elsewhere.
        ConditionEntry? Current() => access.List() is { } now && now.Invalid == null ? NodeAt(now, path, out _) : null;
        int? Index() => access.List() is { } now ? Array.IndexOf(now.Nodes, Current()) : null;
        return new TreeLine
        {
            Key = $"{access.Prefix}.line.{path}",
            Depth = depth,
            Last = last,
            Ancestors = ancestors,
            Words = () => Current()?.Words ?? string.Empty,
            On = () => Current()?.On ?? false,
            SetOn = on => Change(access, copy =>
            {
                if (NodeAt(copy, path, out _) is { } n) n.On = on;
            }),
            Live = () =>
            {
                if (access.State() is not { } state || state.List == null || Index() is not int i || i < 0 || i >= state.Results.Length) return null;
                var current = state.List.Nodes.ElementAtOrDefault(i);
                if (current == null) return null;
                if (!current.On) return ("off", StatusState.Off);
                var result = state.Results[i];
                string value = Conditions.ValueText(current, result);
                StatusState shape = result.State switch
                {
                    ConditionState.Unknown => StatusState.Waiting,
                    ConditionState.Invalid => StatusState.Waiting,
                    _ => state.Counted[i] ? StatusState.Active : StatusState.Paused,
                };
                return (value.Length > 0 ? value : Conditions.StateWord(result.State), shape);
            },
            Type = group ? () => Array.IndexOf(ConditionGroup.Types, Current()?.Group?.Type ?? "all") : null,
            SetType = group ? i => Change(access, copy =>
            {
                if (NodeAt(copy, path, out _)?.Group is { } g && i >= 0 && i < ConditionGroup.Types.Length) g.Type = ConditionGroup.Types[i];
            }) : null,
            Opened = access.Open == path,
            Toggle = group ? null : () => access.SetOpen(access.Open == path ? null : path),
            Remove = access.Kept != null && access.Kept(entry) != null ? null : () =>
            {
                Change(access, copy =>
                {
                    if (NodeAt(copy, path, out var siblings) is { } n) siblings!.Remove(n);
                });
                access.SetOpen(null);
            },
            Blocked = access.Blocked,
            Tip = () => Current() is { } e ? (e.Invalid is { } bad ? $"{e.Words}: {bad}" : e.Words) : null,
        };
    }

    private static TreeAdd AddRow(TreeAccess access, ConditionList list, string groupPath, int depth)
    {
        int groupDepth = groupPath.Length == 0 ? 1 : groupPath.Split('.').Length + 1;
        return new TreeAdd
        {
            Key = $"{access.Prefix}.add.{(groupPath.Length == 0 ? "root" : groupPath)}",
            Depth = depth,
            Label = "+ add a condition",
            Choices = () => s_kindLabels,
            Groups = () => s_kindGroups,
            Picked = i =>
            {
                if (i < 0 || i >= s_kinds.Count) return;
                var kind = s_kinds[i];
                string? opened = null;
                Change(access, copy =>
                {
                    if (copy.NodeCount >= ConditionList.Limit || ItemsAt(copy, groupPath) is not { } items) return;
                    items.Add(ConditionEntry.New(kind));
                    opened = (groupPath.Length == 0 ? string.Empty : groupPath + ".") + items.Count;
                });
                // A new line opens, so its arguments can be set at once.
                if (opened != null) access.SetOpen(opened);
            },
            Blocked = () => access.Blocked() ?? (access.List() is { } now && now.NodeCount >= ConditionList.Limit ? ConditionEditor.LimitReason : null),
            SecondLabel = "+ add a group",
            Second = () => Change(access, copy =>
            {
                if (copy.NodeCount >= ConditionList.Limit || ItemsAt(copy, groupPath) is not { } items) return;
                items.Add(ConditionEntry.NewGroup("any"));
            }),
            SecondBlocked = () => access.Blocked()
                ?? (groupDepth >= ConditionList.MaxDepth ? $"groups go {ConditionList.MaxDepth} levels deep at most"
                : access.List() is { } now && now.NodeCount >= ConditionList.Limit ? ConditionEditor.LimitReason : null),
        };
    }

    private static void Change(TreeAccess access, Action<ConditionList> change)
    {
        if (access.Blocked() != null) return;
        access.BeforeChange?.Invoke();
        access.Edit(copy =>
        {
            change(copy);
            copy.Reindex();
        });
    }

    // ---- An open condition's options ----

    private static IEnumerable<MenuRow> Options(TreeAccess access, string path, ConditionKind kind)
    {
        ConditionEntry? Current() => access.List() is { } now && now.Invalid == null ? NodeAt(now, path, out _) : null;
        void Edit(Action<ConditionEntry> change) => Change(access, copy =>
        {
            if (NodeAt(copy, path, out _) is { } n) change(n);
        });
        string prefix = $"{access.Prefix}.opt.{path}.";
        foreach (var arg in kind.Args) yield return ArgRow(arg, prefix, Current, Edit, kind);
        if (kind.Args.Any(a => a.Name == "wNormal"))
        {
            yield return new ValueToggle("Own weights", () => Current()?.Args.ContainsKey("wNormal") == true, on => Edit(e =>
            {
                foreach (string name in s_weightArgs)
                {
                    if (on) e.Args[name] = JsonValue.Create(OwnerWeight?.Invoke(name) ?? 1f);
                    else e.Args.Remove(name);
                }
            })) { Key = prefix + "ownweights" };
        }
        yield return new ValueToggle("True when unresolvable", () => Current()?.TrueWhenUnknown ?? false, on => Edit(e => e.TrueWhenUnknown = on))
        {
            Key = prefix + "unknown",
        };
        yield return new ValueNumber("For at least", () => Current()?.For ?? 0f, v => Edit(e => e.For = v), 0f, ConditionEntry.MaxFor, 0.5f, " s")
        {
            Key = prefix + "for",
        };
        yield return new ValueToggle("Show chart on overlay", () => Current()?.Chart ?? false, on => Edit(e => e.Chart = on)) { Key = prefix + "chart" };
        yield return new ValueReadout("Now", () =>
        {
            if (access.State() is not { } state || state.List == null || Current() is not { } entry) return ("no live data", StatusState.Waiting);
            int i = Array.IndexOf(state.List.Nodes, NodeAt(state.List, path, out _));
            if (i < 0 || i >= state.Results.Length) return ("not checked", StatusState.Waiting);
            var result = state.Results[i];
            string value = Conditions.ValueText(entry, result);
            return ($"{(value.Length > 0 ? value + " · " : string.Empty)}{Conditions.StateWord(result.State)}",
                result.State == ConditionState.Pass ? StatusState.Active : result.State == ConditionState.Fail ? StatusState.Paused : (StatusState?)null);
        }) { Key = prefix + "now" };
    }

    private static readonly string[] s_weightArgs = { "wNormal", "wMagic", "wRare", "wBoss" };

    /// <summary>The owner's enemy weight for a weight arg name (the feature sets it), for "Own weights" to start from.</summary>
    public static Func<string, float>? OwnerWeight { get; set; }

    // One argument's control, with the visibility rules of optional args.
    internal static MenuRow ArgRow(ArgSpec arg, string prefix, Func<ConditionEntry?> current, Action<Action<ConditionEntry>> edit, ConditionKind kind)
    {
        string label = ArgLabel(arg.Name);
        JsonNode? Stored() => current()?.Args.TryGetPropertyValue(arg.Name, out var node) == true ? node : null;
        string Text() => Stored() is JsonValue v && v.TryGetValue(out string? t) ? t : arg.Default is JsonValue d && d.TryGetValue(out string? dt) ? dt : string.Empty;
        float Number() => Stored() is JsonValue v && v.TryGetValue(out double d) ? (float)d
            : arg.Default is JsonValue dv && dv.TryGetValue(out double dd) ? (float)dd : arg.Min;
        void Set(JsonNode? value) => edit(e => e.Args[arg.Name] = value);
        Func<bool>? visible = arg.Name switch
        {
            "to" => () => current()?.Args["compare"] is JsonValue c && c.TryGetValue(out string? cmp) && cmp == "between",
            "value" => () => (current()?.Args["as"] ?? current()?.Args["atLeast"]) is JsonValue a && a.TryGetValue(out string? s) && s == "value",
            "percent" when kind.Args.Any(a => a.Name == "as") => () => !(current()?.Args["as"] is JsonValue a && a.TryGetValue(out string? s) && s == "value"),
            "wNormal" or "wMagic" or "wRare" or "wBoss" => () => current()?.Args.ContainsKey("wNormal") == true,
            // A line's enemy: nearest takes a rarity and range, a target its pick and limits too, the cast target none.
            "minRarity" or "range" when kind.Args.Any(a => a.Name == "subject") => () => SubjectIs(current, "nearest") || SubjectIs(current, "target"),
            "pick" or "withoutMod" or "withAilment" => () => SubjectIs(current, "target"),
            "radius" => () => SubjectIs(current, "target") && current()?.Args["pick"] is JsonValue p && p.TryGetValue(out string? pick) && pick.StartsWith("area", StringComparison.Ordinal),
            _ => null,
        };
        string key = prefix + "arg." + arg.Name;
        switch (arg.Kind)
        {
            case ArgKind.Number:
                return new ValueNumber(label, Number, v => Set(JsonValue.Create(v)), arg.Min, arg.Max, arg.Step, arg.Unit) { Key = key, VisibleWhen = visible };
            case ArgKind.Choice:
            case ArgKind.Rarity:
                var options = arg.Options;
                var shown = options.Select(o => arg.Kind == ArgKind.Rarity ? RarityLabel(o) : arg.Name == "pick" ? PickLabel(o) : ArgSpec.OptionWords(o)).ToList();
                return new ValueChoice(label, () => Array.IndexOf(options, Text()), i =>
                {
                    if (i >= 0 && i < options.Length) Set(JsonValue.Create(options[i]));
                }, () => shown) { Key = key, VisibleWhen = visible, Width = 130f };
            case ArgKind.Buff:
                return new ValueChoice(label, () => Stored() is JsonValue v && v.TryGetValue(out double id) ? ConditionEditor.SharedBuffs().Ids.IndexOf((int)Math.Round(id)) : -1, i =>
                {
                    var buffs = ConditionEditor.SharedBuffs();
                    if (i >= 0 && i < buffs.Ids.Count) Set(JsonValue.Create(buffs.Ids[i]));
                }, () => ConditionEditor.SharedBuffs().Labels)
                {
                    Key = key,
                    Width = 170f,
                    Groups = () => ConditionEditor.SharedBuffs().Groups,
                    Shown = () => Stored() is JsonValue v && v.TryGetValue(out double id) ? ConditionKinds.BuffName((int)Math.Round(id)) : "Select buff",
                };
            case ArgKind.Skill:
                return SkillPicker(label, key, Text, id => Set(JsonValue.Create(id)));
            case ArgKind.Bool:
                return new ValueToggle(label, () => Stored() is JsonValue bv && bv.TryGetValue(out bool b) ? b : arg.Default is JsonValue dv && dv.TryGetValue(out bool db) && db,
                    on => Set(JsonValue.Create(on))) { Key = key };
            case ArgKind.Mod:
            case ArgKind.Ailment:
            {
                bool mods = arg.Kind == ArgKind.Mod;
                IReadOnlyList<int> Ids() => mods ? GameData.AllMonsterMods() : GameData.AllAilments();
                string Name(int id) => mods ? GameData.MonsterMod(id).Name ?? $"Mod {id}" : ConditionKinds.BuffName(id);
                // An optional one (a target's limit) can be cleared: "no … limit" first.
                int first = arg.Optional ? 1 : 0;
                string none = mods ? "no mod limit" : "no ailment limit";
                return new ValueChoice(label, () => Stored() is JsonValue v && v.TryGetValue(out double id) ? Ids().ToList().IndexOf((int)Math.Round(id)) + first : first - 1, i =>
                {
                    if (arg.Optional && i == 0)
                    {
                        edit(e => e.Args.Remove(arg.Name));
                        return;
                    }
                    var ids = Ids();
                    if (i - first >= 0 && i - first < ids.Count) Set(JsonValue.Create(ids[i - first]));
                }, () => (arg.Optional ? new[] { none } : Array.Empty<string>()).Concat(Ids().Select(Name)).ToList())
                {
                    Key = key,
                    Width = 190f,
                    VisibleWhen = visible,
                    Shown = () => Stored() is JsonValue v && v.TryGetValue(out double id) ? Name((int)Math.Round(id)) : arg.Optional ? none : mods ? "Select monster mod" : "Select ailment",
                };
            }
            case ArgKind.Slot:
                return new ValueChoice(label, () => (int)Math.Round(Number()), i => Set(JsonValue.Create(i)), () => s_slotOptions) { Key = key, Width = 110f };
            case ArgKind.Key:
                return new ValueKey(label, Text, chord => Set(JsonValue.Create(chord)))
                {
                    Key = key,
                    Warnings = () => Text() is { Length: > 0 } chord ? HotkeyConflicts.ForCondition(chord, kind.Id == "input.toggle") : Array.Empty<string>(),
                };
            default:
                return new ValueNumber(label, Number, v => Set(JsonValue.Create((int)Math.Round(v))), 0, 100000, 1) { Key = key };
        }
    }

    /// <summary>A skill dropdown: bar first, then seen this session, then the library's and the class's skills.</summary>
    public static ValueChoice SkillPicker(string label, string key, Func<string> get, Action<string> set) =>
        new(label, () => ConditionKinds.SkillOptions(get()).FindIndex(o => o.Id == get()), i =>
        {
            var skills = ConditionKinds.SkillOptions(get());
            if (i >= 0 && i < skills.Count) set(skills[i].Id);
        }, () => ConditionKinds.SkillOptions(get()).Select(o => o.Name).ToList())
        {
            Key = key,
            Width = 170f,
            Shown = () => get().Length > 0 ? ConditionKinds.SkillName(get()) : "Select skill",
        };

    private static readonly List<string> s_slotOptions = new() { "This slot", "Slot 1", "Slot 2", "Slot 3", "Slot 4", "Slot 5" };

    private static bool SubjectIs(Func<ConditionEntry?> current, string subject) =>
        current()?.Args["subject"] is JsonValue v && v.TryGetValue(out string? s) ? s == subject : subject == "nearest";

    // A target pick's label as the Target row offers it.
    private static string PickLabel(string name) =>
        Array.IndexOf(TargetSpec.PickNames, name) is int i and >= 0 ? TargetSpec.Label((TargetPick)i) : name;

    private static string RarityLabel(string name) => name switch { "boss" => "boss", "rare" => "rare or boss", _ => "magic or better" };

    private static string ArgLabel(string name) => name switch
    {
        "compare" => "Compare",
        "percent" => "Percent",
        "seconds" => "Seconds",
        "range" => "Range",
        "weight" => "Weight",
        "count" => "Count",
        "rarity" => "Rarity",
        "slot" => "Slot",
        "buff" => "Buff",
        "key" => "Key",
        "skill" => "Skill",
        "to" => "Up to",
        "as" => "As",
        "value" => "Value",
        "centre" => "Around",
        "wNormal" => "Normal weight",
        "wMagic" => "Magic weight",
        "wRare" => "Rare weight",
        "wBoss" => "Boss weight",
        "subject" => "Enemy",
        "pick" => "Target",
        "withoutMod" => "Not with",
        "withAilment" => "Only with",
        "radius" => "Radius",
        "minRarity" => "At least",
        "metres" => "Metres",
        "is" => "Is",
        "not" => "Not",
        "atLeast" => "At least",
        "mod" => "Mod",
        "ailment" => "Ailment",
        _ => char.ToUpperInvariant(name[0]) + name[1..],
    };
}
