using System.Text.Json.Nodes;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// The condition list editor as menu data: a list row whose entries are one line of value rows each (the kind, a
/// control per arg, a live readout, the switch and "⋯" for the details line with "True when unresolvable"), an add
/// dropdown of kinds grouped by subject, move and remove. Every change goes through the setting, so it lands in the
/// profile in use. The UI draws it; no feature draws anything itself.
/// </summary>
internal static class ConditionEditor
{
    public const string LimitReason = "30 conditions at most";

    /// <summary>
    /// The editor for one list of a setting. key is read each draw (a form change shows at once); context gives the
    /// readouts their slot and the owner's enemy tally; scanning tells whether the owner counts enemies now (for "not
    /// counted (autocast off)"); keptTip is the remove button's tip on a kept entry.
    /// </summary>
    public static ListRow List(ConditionSetting setting, Func<string> key, Func<ConditionContext> context, string label,
        Func<bool>? scanning = null, string keptTip = "This condition can be switched off, not removed.")
    {
        var editor = new Editor(setting, key, context, scanning);
        var kinds = new List<ConditionKind>();
        var headings = new List<(string Label, int First)>();
        foreach (var (heading, group) in ConditionKinds.BySubject())
        {
            headings.Add((heading, kinds.Count));
            kinds.AddRange(group);
        }
        var kindLabels = kinds.Select(k => k.Label).ToList();
        var groups = headings.ToArray();

        return new ListRow(label, editor.Items, editor.RowsOf, "Add condition")
        {
            Key = "list." + MenuEntry.Slug(label),
            AddChoices = () => kindLabels,
            AddGroups = () => groups,
            AddPicked = i =>
            {
                if (i < 0 || i >= kinds.Count) return;
                var entry = ConditionEntry.New(kinds[i]);
                editor.Edit(list =>
                {
                    if (!list.Full) list.Entries.Add(entry);
                });
            },
            AddBlocked = () =>
            {
                var list = setting.List(key());
                if (setting.Doc.ReadOnly || list.Invalid != null) return "made by a newer version";
                return list.Full ? LimitReason : null;
            },
            Remove = i => editor.Edit(list =>
            {
                if (i < list.Entries.Count && !setting.IsKept(key(), list.Entries[i])) list.Entries.RemoveAt(i);
            }, removed: i),
            RemoveBlocked = i => setting.List(key()).Entries is var entries && i < entries.Count && setting.IsKept(key(), entries[i]) ? keptTip : null,
            Move = (from, to) => editor.Edit(list =>
            {
                if (from >= list.Entries.Count || to >= list.Entries.Count) return;
                var entry = list.Entries[from];
                list.Entries.RemoveAt(from);
                list.Entries.Insert(to, entry);
            }, moved: (from, to)),
            Details = editor.DetailsOf,
        };
    }

    // ---- Enemy ranges the open page shows ----

    private static readonly Dictionary<ConditionSetting, (float At, List<TallyKey> Ranges)> s_shown = new();
    private const float ShownSeconds = 0.5f;

    /// <summary>Adds the ranges of switched-on enemy conditions on a shown editor page of this setting (drawn in the
    /// last half second), so the owner's next count includes them.</summary>
    public static void ShownEnemyRanges(ConditionSetting setting, List<TallyKey> into)
    {
        if (!s_shown.TryGetValue(setting, out var shown) || Time.unscaledTime - shown.At > ShownSeconds) return;
        foreach (var key in shown.Ranges)
        {
            if (!into.Contains(key)) into.Add(key);
        }
    }

    /// <summary>Adds the enemies that switched-on, valid enemy-subject conditions in a tree are about.</summary>
    public static void EnemySubjects(ConditionList list, List<SubjectKey> into)
    {
        if (list.Invalid != null) return;
        var nodes = list.Nodes;
        for (int i = 0; i < nodes.Length; i++)
        {
            var entry = nodes[i];
            if (!entry.On)
            {
                if (entry.IsGroup) i = list.End(i) - 1;
                continue;
            }
            if (entry.IsGroup || entry.Invalid != null || entry.KindDef?.Picks is not { } subject) continue;
            var key = subject(entry.Values);
            if (!into.Contains(key)) into.Add(key);
        }
    }

    /// <summary>Adds the counts that switched-on, valid enemy conditions in a tree need (inside switched-on groups).</summary>
    public static void EnemyRanges(ConditionList list, List<TallyKey> into)
    {
        if (list.Invalid != null) return;
        var nodes = list.Nodes;
        for (int i = 0; i < nodes.Length; i++)
        {
            var entry = nodes[i];
            if (!entry.On)
            {
                // Nothing under a switched-off group counts.
                if (entry.IsGroup) i = list.End(i) - 1;
                continue;
            }
            if (entry.IsGroup || entry.Invalid != null || entry.KindDef?.KeyFor(entry.Values) is not { } key) continue;
            if (!into.Contains(key)) into.Add(key);
        }
    }

    /// <summary>The buff picker's options: ids, labels and group headings.</summary>
    public sealed class BuffOptions
    {
        public float BuiltAt;
        public readonly List<int> Ids = new();
        public readonly List<string> Labels = new();
        public (string Label, int First)[] Groups = Array.Empty<(string, int)>();
    }

    private static BuffOptions? s_buffs;

    /// <summary>The buff picker's options (active now, seen this session, all buffs), built at most once a second.</summary>
    public static BuffOptions SharedBuffs()
    {
        if (s_buffs != null && Time.unscaledTime - s_buffs.BuiltAt < 1f) return s_buffs;
        var options = s_buffs ?? new BuffOptions();
        options.BuiltAt = Time.unscaledTime;
        options.Ids.Clear();
        options.Labels.Clear();
        var groups = new List<(string, int)>();
        void Group(string heading, IEnumerable<int> ids)
        {
            int first = options.Ids.Count;
            foreach (int id in ids)
            {
                if (options.Ids.Contains(id)) continue;
                options.Ids.Add(id);
                options.Labels.Add(ConditionKinds.BuffName(id));
            }
            if (options.Ids.Count > first) groups.Add((heading, first));
        }
        Group("Active now", PlayerModel.Buffs?.Where(b => b.IsBuff != false).Select(b => b.Id) ?? Enumerable.Empty<int>());
        Group("Seen this session", PlayerModel.SeenBuffs.OrderBy(ConditionKinds.BuffName));
        Group("All buffs", GameData.AllBuffs().OrderBy(ConditionKinds.BuffName));
        options.Groups = groups.ToArray();
        s_buffs = options;
        return options;
    }

    // ---- One editor ----

    private sealed class Editor
    {
        private readonly ConditionSetting _setting;
        private readonly Func<string> _key;
        private readonly Func<ConditionContext> _context;
        private readonly Func<bool>? _scanning;
        private readonly HashSet<int> _unfolded = new();
        private string? _unfoldedKey;

        // The readouts are checked at most once per frame.
        private int _checkedFrame = -1;
        private ConditionList? _checkedList;
        private ConditionResult[] _results = new ConditionResult[ConditionList.Limit];

        public Editor(ConditionSetting setting, Func<string> key, Func<ConditionContext> context, Func<bool>? scanning)
        {
            _setting = setting;
            _key = key;
            _context = context;
            _scanning = scanning;
        }

        private ConditionList List => _setting.List(_key());

        public IReadOnlyList<object> Items()
        {
            var list = List;
            NoteShownRanges(list);
            var items = new object[list.Invalid != null ? 1 : list.Entries.Count];
            for (int i = 0; i < items.Length; i++) items[i] = i;
            return items;
        }

        private void NoteShownRanges(ConditionList list)
        {
            if (!s_shown.TryGetValue(_setting, out var shown)) s_shown[_setting] = shown = (0f, new List<TallyKey>());
            shown.Ranges.Clear();
            EnemyRanges(list, shown.Ranges);
            s_shown[_setting] = (Time.unscaledTime, shown.Ranges);
        }

        public void Edit(Action<ConditionList> change, int? removed = null, (int From, int To)? moved = null)
        {
            _setting.Edit(_key(), list =>
            {
                change(list);
                return list;
            }, out string reason);
            if (reason.Length > 0) Log.WarnThrottled("conditions:edit", $"Condition list not changed: {reason}");
            // Unfolded details follow their entry.
            if (removed is int r) _unfolded.RemoveWhere(i => i == r);
            if (moved is { } m && _unfolded.Remove(m.From)) _unfolded.Add(m.To);
        }

        private void EditEntry(int index, Action<ConditionEntry> change) => Edit(list =>
        {
            if (index < list.Entries.Count) change(list.Entries[index]);
        });

        private ConditionResult Result(int index)
        {
            var list = List;
            if (_checkedFrame != Time.frameCount || !ReferenceEquals(_checkedList, list))
            {
                _checkedFrame = Time.frameCount;
                _checkedList = list;
                if (_results.Length < list.Entries.Count) _results = new ConditionResult[list.Entries.Count];
                Array.Clear(_results);
                Conditions.Check(list, _context(), _results);
            }
            return index < _results.Length ? _results[index] : default;
        }

        private bool Unfolded(int index)
        {
            string key = _key();
            if (key != _unfoldedKey)
            {
                _unfolded.Clear();
                _unfoldedKey = key;
            }
            return _unfolded.Contains(index);
        }

        public IReadOnlyList<MenuRow>? DetailsOf(object item)
        {
            int index = (int)item;
            var list = List;
            if (list.Invalid != null || index >= list.Entries.Count || !Unfolded(index)) return null;
            // Inline rows draw no label of their own, so the switch's name comes first.
            return new MenuRow[]
            {
                new ValueText("Unresolvable label", () => "True when unresolvable") { Key = "unknown-label", Width = 150f },
                new ValueToggle("True when unresolvable", () => List.Entries.ElementAtOrDefault(index)?.TrueWhenUnknown ?? false,
                    on => EditEntry(index, e => e.TrueWhenUnknown = on)) { Key = "unknown" },
                new ValueText("Unresolvable hint", () => "On: a value that can't be read counts as passing.") { Key = "unknown-hint" },
            };
        }

        public IReadOnlyList<MenuRow> RowsOf(object item)
        {
            int index = (int)item;
            var list = List;
            if (list.Invalid != null)
            {
                return new MenuRow[] { new ValueText("List", () => $"{List.Invalid}: {List.Raw?.ToJsonString()}") { Key = "invalid" } };
            }
            if (index >= list.Entries.Count) return Array.Empty<MenuRow>();
            var entry = list.Entries[index];
            var rows = new List<MenuRow>();
            // The switch first and the readout last, so the readout takes what width is left (its tip has the words).
            rows.Add(Switch(index));
            if (entry.KindDef is not { } kind)
            {
                // A kind this build doesn't know: its stored text and the reason; only on, move and remove work.
                rows.Add(new ValueText("Stored", () => Clip(List.Entries.ElementAtOrDefault(index)?.Words ?? string.Empty, 60)) { Key = "stored", Width = 180f });
                rows.Add(Readout(index));
                return rows;
            }
            rows.Add(new ValueText("Kind", () => List.Entries.ElementAtOrDefault(index) is { } e ? e.Label ?? e.KindDef?.Label ?? e.Kind : string.Empty) { Key = "kind", Width = 84f });
            // The macro suite's optional args (comparisons, centre, own weights) are set in its rule editor.
            foreach (var arg in kind.Args.Where(a => !a.Optional)) rows.Add(ArgRow(index, arg));
            rows.Add(new ActionButton("⋯", "details")
            {
                Key = "details",
                Tip = "Details",
                OnClick = () =>
                {
                    if (!_unfolded.Remove(index)) _unfolded.Add(index);
                },
            });
            rows.Add(Readout(index));
            return rows;
        }

        private MenuRow Switch(int index) =>
            new ValueToggle("On", () => List.Entries.ElementAtOrDefault(index)?.On ?? false, on => EditEntry(index, e => e.On = on)) { Key = "on" };

        private MenuRow Readout(int index) => new ValueReadout("Now", () => ReadoutValue(index))
        {
            Key = "readout",
            Word = () => List.Entries.ElementAtOrDefault(index) is { On: true } ? Conditions.StateWord(Result(index).State) : "off",
            Tip = () => List.Entries.ElementAtOrDefault(index) is { } e ? e.Words : null,
        };

        private (string, StatusState?) ReadoutValue(int index)
        {
            var entry = List.Entries.ElementAtOrDefault(index);
            if (entry == null) return (string.Empty, StatusState.Off);
            if (!entry.On) return ("off", StatusState.Off);
            var result = Result(index);
            string text = Conditions.ValueText(entry, result);
            if (result.State == ConditionState.Unknown && result.Reason == ConditionKinds.NotCounted && _scanning?.Invoke() == false) text = "not counted (autocast off)";
            return result.State switch
            {
                ConditionState.Pass => (text, StatusState.Active),
                ConditionState.Fail => (text, StatusState.Holding),
                ConditionState.Unknown => (text, StatusState.Waiting),
                _ => (text, null),
            };
        }

        // ---- Arg controls; each reads the stored arg, so an entry with a bad value can still be fixed ----

        private JsonNode? StoredArg(int index, string name) =>
            List.Entries.ElementAtOrDefault(index) is { } e && e.Args.TryGetPropertyValue(name, out var node) ? node : null;

        private float StoredNumber(int index, ArgSpec arg)
        {
            var node = StoredArg(index, arg.Name) ?? arg.Default;
            if (node is JsonValue v && v.TryGetValue(out double d)) return (float)d;
            if (node is JsonValue f && f.TryGetValue(out float x)) return x;
            return arg.Min;
        }

        private string StoredText(int index, ArgSpec arg)
        {
            var node = StoredArg(index, arg.Name) ?? arg.Default;
            return node is JsonValue v && v.TryGetValue(out string? text) ? text : string.Empty;
        }

        private void SetArg(int index, string name, JsonNode? value) => EditEntry(index, e => e.Args[name] = value);

        private MenuRow ArgRow(int index, ArgSpec arg)
        {
            string label = ArgLabel(arg);
            switch (arg.Kind)
            {
                case ArgKind.Number:
                    return new ValueNumber(label, () => StoredNumber(index, arg), v => SetArg(index, arg.Name, JsonValue.Create(v)), arg.Min, arg.Max, arg.Step, arg.Unit)
                    {
                        Key = "arg." + arg.Name,
                    };
                case ArgKind.Choice:
                case ArgKind.Rarity:
                    var options = arg.Options;
                    var shown = arg.Kind == ArgKind.Rarity ? options.Select(RarityLabel).ToList() : options.Select(ArgSpec.OptionWords).ToList();
                    return new ValueChoice(label, () => Array.IndexOf(options, StoredText(index, arg)), i =>
                    {
                        if (i >= 0 && i < options.Length) SetArg(index, arg.Name, JsonValue.Create(options[i]));
                    }, () => shown) { Key = "arg." + arg.Name, Width = arg.Kind == ArgKind.Rarity ? 120f : 84f };
                case ArgKind.Slot:
                    return new ValueChoice(label, () => (int)Math.Round(StoredNumber(index, arg)), i => SetArg(index, arg.Name, JsonValue.Create(i)), () => s_slotOptions)
                    {
                        Key = "arg." + arg.Name,
                        Width = 92f,
                    };
                case ArgKind.Buff:
                    return BuffPicker(index, arg, label);
                case ArgKind.Skill:
                    return new ValueChoice(label, () => ConditionKinds.SkillOptions(StoredText(index, arg)).FindIndex(o => o.Id == StoredText(index, arg)), i =>
                    {
                        var skills = ConditionKinds.SkillOptions(StoredText(index, arg));
                        if (i >= 0 && i < skills.Count) SetArg(index, arg.Name, JsonValue.Create(skills[i].Id));
                    }, () => ConditionKinds.SkillOptions(StoredText(index, arg)).Select(o => o.Name).ToList()) { Key = "arg." + arg.Name, Width = 140f };
                default:
                    return new ValueKey(label, () => StoredText(index, arg), chord => SetArg(index, arg.Name, JsonValue.Create(chord)))
                    {
                        Key = "arg." + arg.Name,
                        Warnings = () => StoredText(index, arg) is { Length: > 0 } chord
                            ? HotkeyConflicts.ForCondition(chord, List.Entries.ElementAtOrDefault(index)?.Kind == "input.toggle")
                            : Array.Empty<string>(),
                    };
            }
        }

        private static readonly List<string> s_slotOptions = new() { "This slot", "Slot 1", "Slot 2", "Slot 3", "Slot 4", "Slot 5" };

        private static string RarityLabel(string name) => name switch { "boss" => "boss", "rare" => "rare or boss", _ => "magic or better" };

        private static string ArgLabel(ArgSpec arg) => arg.Name switch
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
            _ => arg.Name,
        };

        // The buff picker: active buffs first, then the ones seen this session, then every buff the game has; built at
        // most once a second while shown. The stored id shows by name, or "Buff #id" when game data can't name it.

        private BuffOptions Buffs() => SharedBuffs();

        private MenuRow BuffPicker(int index, ArgSpec arg, string label) => new ValueChoice(label, () =>
        {
            var node = StoredArg(index, arg.Name);
            return node is JsonValue v && v.TryGetValue(out double id) ? Buffs().Ids.IndexOf((int)Math.Round(id)) : -1;
        }, i =>
        {
            var buffs = Buffs();
            if (i >= 0 && i < buffs.Ids.Count) SetArg(index, arg.Name, JsonValue.Create(buffs.Ids[i]));
        }, () => Buffs().Labels)
        {
            Key = "arg." + arg.Name,
            Width = 140f,
            Groups = () => Buffs().Groups,
            Shown = () => StoredArg(index, arg.Name) is JsonValue v && v.TryGetValue(out double id) ? ConditionKinds.BuffName((int)Math.Round(id)) : "Select buff",
        };

        private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
    }
}
