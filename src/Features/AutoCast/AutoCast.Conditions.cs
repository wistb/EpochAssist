using EpochAssist.Core;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Features;

internal static partial class AutoCast
{
    /// <summary>
    /// A run's condition verdicts, made each update: the every-slot list (its verdict drives the every-slot pause rule)
    /// and each toggled slot's own list for the current form (a failing one turns the slot's press into none). The result
    /// arrays are made once; a check allocates nothing.
    /// </summary>
    private sealed class Verdicts
    {
        public ConditionResult[] Every = new ConditionResult[ConditionList.Limit];
        public ListVerdict EveryVerdict = ListVerdict.Pass;
        public readonly ConditionResult[][] Slot = new ConditionResult[SlotCount][];
        public readonly ListVerdict[] SlotVerdict = new ListVerdict[SlotCount];
        public readonly bool[] Waiting = new bool[SlotCount];

        // The every-slot rule's reason, worked out again only when its first failing entry changes.
        public ConditionList? ReasonList;
        public int ReasonIndex = -1;
        public string Reason = string.Empty;

        // The check step's own time over the last second, for why's header.
        public double CheckMs;
        public int CheckFrames;
        public float WindowStart;
        public double LastSecondMs;

        public Verdicts()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                Slot[i] = new ConditionResult[ConditionList.Limit];
                SlotVerdict[i] = ListVerdict.Pass;
            }
        }

        public void Clear()
        {
            EveryVerdict = ListVerdict.Pass;
            for (int i = 0; i < SlotCount; i++)
            {
                SlotVerdict[i] = ListVerdict.Pass;
                Waiting[i] = false;
            }
            ReasonList = null;
            ReasonIndex = -1;
            Reason = string.Empty;
        }
    }

    // "<form>/<slot>" keys, made once per form.
    private static readonly Dictionary<int, string[]> s_slotKeys = new();

    /// <summary>A slot's list key in SlotConditions: the form key (-1 human, else the transform id) and the slot 1–5.</summary>
    public static string SlotKey(int formKey, int slot)
    {
        if (!s_slotKeys.TryGetValue(formKey, out var keys))
        {
            keys = new string[SlotCount];
            for (int i = 0; i < SlotCount; i++) keys[i] = $"{formKey}/{i + 1}";
            s_slotKeys[formKey] = keys;
        }
        return keys[slot];
    }

    /// <summary>The form whose lists apply now: the run's, or human out of game.</summary>
    public static int CurrentFormKey => s_run?.FormKey ?? HumanFormKey;

    /// <summary>The every-slot list passes (true while autocast doesn't run).</summary>
    public static bool EveryPasses => s_run?.Verdicts.EveryVerdict.Passed ?? true;

    /// <summary>The slot is toggled and its own conditions hold it back (chips show Paused, tree items Holding).</summary>
    public static bool SlotWaiting(int slot) => s_run is { } run && run.Verdicts.Waiting[slot];

    /// <summary>A check's context for a slot (0-based, -1 for none), with the run's enemy tally while it counts.</summary>
    internal static ConditionContext ContextFor(int slot) => new(slot, s_run is { Active: true } run ? run.Tally : null, Clock.Now);

    /// <summary>Whether autocast counts enemies now (for the editor's "not counted (autocast off)").</summary>
    internal static bool Scanning => s_run is { Active: true };

    private static ConditionResult[] Fit(ConditionResult[] results, ConditionList list) =>
        results.Length >= list.Entries.Count ? results : new ConditionResult[list.Entries.Count];

    // The every-slot list, each update while running (the pause rule needs it even while that rule holds autocast).
    private static void CheckEvery(Run run)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var verdicts = run.Verdicts;
        var list = S.GateConditions.List(S.EveryList);
        verdicts.Every = Fit(verdicts.Every, list);
        verdicts.EveryVerdict = Conditions.Check(list, ContextFor(-1), verdicts.Every);
        if (!verdicts.EveryVerdict.Passed && (verdicts.EveryVerdict.FirstFailing != verdicts.ReasonIndex || !ReferenceEquals(list, verdicts.ReasonList)))
        {
            verdicts.ReasonList = list;
            verdicts.ReasonIndex = verdicts.EveryVerdict.FirstFailing;
            verdicts.Reason = verdicts.ReasonIndex >= 0 ? list.Entries[verdicts.ReasonIndex].FailText : list.Invalid ?? "every-slot list can't be read";
        }
        AddCheckTime(verdicts, start);
        // The every-slot check runs once per frame, so frames are counted here; slot checks add to the same frame.
        verdicts.CheckFrames++;
    }

    // One toggled slot's own list for the current form; true when it lets the slot cast.
    private static bool CheckSlot(Run run, int slot)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var verdicts = run.Verdicts;
        var list = S.SlotConditions.List(SlotKey(run.FormKey, slot));
        verdicts.Slot[slot] = Fit(verdicts.Slot[slot], list);
        var verdict = Conditions.Check(list, ContextFor(slot), verdicts.Slot[slot]);
        verdicts.SlotVerdict[slot] = verdict;
        verdicts.Waiting[slot] = !verdict.Passed;
        AddCheckTime(verdicts, start);
        return verdict.Passed;
    }

    private static void AddCheckTime(Verdicts verdicts, long start)
    {
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        float now = Clock.Now;
        if (now - verdicts.WindowStart >= 1f)
        {
            verdicts.LastSecondMs = verdicts.CheckFrames > 0 ? verdicts.CheckMs / verdicts.CheckFrames : 0.0;
            verdicts.CheckMs = 0.0;
            verdicts.CheckFrames = 0;
            verdicts.WindowStart = now;
        }
        verdicts.CheckMs += ms;
    }

    // ---- why ----

    /// <summary>
    /// Every slot's verdict (or one slot's), for the dev why verb: per slot whether it is toggled, its skill, readiness,
    /// each condition's words, value, result and reason, and the verdict (would cast, waiting on n, held by every-slot n,
    /// on cooldown, paused: reason, off). Untoggled slots' lists are checked here, on demand.
    /// </summary>
    public static IReadOnlyList<string> Explain(int? only)
    {
        var lines = new List<string>();
        var run = s_run;
        var client = run?.Client;
        string state = client != null ? Dispatcher.StatusOf(client).State.ToString().ToLowerInvariant() : "off";
        int form = CurrentFormKey;
        double ms = run?.Verdicts.LastSecondMs ?? 0.0;
        lines.Add($"ok autocast {(run is { Active: true } ? "running" : "off")}, form {(form == HumanFormKey ? "human" : "transform")} ({form}), status {state}, "
            + $"conditions {ms.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} ms");

        var every = S.GateConditions.List(S.EveryList);
        var everyResults = new ConditionResult[Math.Max(1, every.Entries.Count)];
        var everyVerdict = Conditions.Check(every, ContextFor(-1), everyResults);
        lines.Add($"every: {(everyVerdict.Passed ? "pass" : "fail")}" + EntriesText(every, everyResults, "   "));

        bool[] toggles = CurrentToggles();
        var blocking = client?.Blocking;
        for (int i = 0; i < SlotCount; i++)
        {
            if (only is int o && o != i) continue;
            string skill = SlotAbilityName(i) ?? "-";
            if (!toggles[i])
            {
                var own = S.SlotConditions.List(SlotKey(form, i));
                string listed = own.Entries.Count > 0 ? "  " + EntriesText(own, Check(own, i), "  ").TrimStart() : string.Empty;
                lines.Add($"slot {i + 1} {skill,-16} off{listed}");
                continue;
            }
            var list = S.SlotConditions.List(SlotKey(form, i));
            var results = Check(list, i);
            var verdict = Conditions.Check(list, ContextFor(i), results);
            bool? ready = PlayerModel.Slot(i + 1)?.Ready;
            string readiness = ready switch { true => "ready", false => "not ready", _ => "ready? (unknown = ready)" };
            string conditions = list.Entries.Count == 0 ? "conditions: none" : EntriesText(list, results, "  ").TrimStart();
            string outcome = blocking != null && blocking.Name == "every-slot" ? $"held by every-slot {everyVerdict.FirstFailing + 1}"
                : blocking != null ? $"paused: {blocking.Reason}"
                : !verdict.Passed ? $"waiting on {verdict.FirstFailing + 1}"
                : ready == false ? "on cooldown"
                : "would cast";
            lines.Add($"slot {i + 1} {skill,-16} toggled  {readiness}  {conditions}  → {outcome}");
        }
        string held = client == null ? "none" : string.Join(",", Enumerable.Range(0, SlotCount).Where(client.Holds).Select(x => (x + 1).ToString())) is { Length: > 0 } h ? h : "none";
        lines.Add($"dispatcher: {(blocking == null ? "allowed" : $"held by {blocking.Name} ({blocking.Reason})")}; held: {held}; presses last second {Dispatcher.PressesLastSecond}");
        return lines;
    }

    private static ConditionResult[] Check(ConditionList list, int slot)
    {
        var results = new ConditionResult[Math.Max(1, list.Entries.Count)];
        Conditions.Check(list, ContextFor(slot), results);
        return results;
    }

    // "1 Silver Shroud under 2 s   4.1 s  fail" per entry; unknown values say why and what the switch did.
    private static string EntriesText(ConditionList list, ConditionResult[] results, string gap)
    {
        if (list.Invalid != null) return $"{gap}list invalid: {list.Invalid}";
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < list.Entries.Count; i++)
        {
            var entry = list.Entries[i];
            text.Append(gap).Append(i + 1).Append(' ').Append(entry.Words);
            if (!entry.On)
            {
                text.Append("  off");
                continue;
            }
            var result = results[i];
            string value = Conditions.ValueText(entry, result);
            switch (result.State)
            {
                case ConditionState.Unknown:
                    text.Append($"  unknown ({value}) → {(entry.TrueWhenUnknown ? "passes (true when unresolvable on)" : "fails (true when unresolvable off)")}");
                    break;
                case ConditionState.Invalid:
                    text.Append($"  invalid ({value})");
                    break;
                default:
                    if (value.Length > 0) text.Append("  ").Append(value);
                    text.Append("  ").Append(Conditions.StateWord(result.State));
                    break;
            }
        }
        return text.ToString();
    }

    // Registers the lists for the dev cond and why verbs and the keys they use for the hotkey layer.
    private static void RegisterConditionTargets(Feature feature)
    {
        ConditionTargets.Register(new ConditionTargets.Target(feature.Id, "gate", S.GateConditions, () => new[] { S.EveryList },
            name => name == S.EveryList ? S.EveryList : null, _ => ContextFor(-1)));
        ConditionTargets.Register(new ConditionTargets.Target(feature.Id, "slots", S.SlotConditions, () => new[] { "1", "2", "3", "4", "5" },
            name => int.TryParse(name, out int n) && n is >= 1 and <= SlotCount ? SlotKey(CurrentFormKey, n - 1)
                : name.Contains('/') ? name : null,
            name => ContextFor(int.TryParse(name.Split('/')[^1], out int n) && n is >= 1 and <= SlotCount ? n - 1 : -1)));
        ConditionKeys.Register(feature.Id, S.GateConditions, _ => "every-slot condition");
        ConditionKeys.Register(feature.Id, S.SlotConditions, key => $"slot {key.Split('/')[^1]} condition");
    }
}
