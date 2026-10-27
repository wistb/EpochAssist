using EpochAssist.Core;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.MacroSettings;

namespace EpochAssist.Features;

/// <summary>
/// Library macros (contracts/engine.md): each frame the start check over the character's class's enabled macros in
/// library order, at most one start per frame, one run at a time, a higher macro taking over between steps; the run's
/// steps sent through the library and drinks clients, sending steps at least 0.1 s apart, cancelled on every cause in
/// the contract. Part of the Macros feature (SlotMacros); everything it holds is in its engine object, made in Start and
/// dropped in Shutdown (the dispatcher lets go of what the clients hold).
/// </summary>
internal static partial class Macros
{
    private const float SendGapSeconds = 0.1f, RunCapSeconds = 20f, ConfirmSeconds = 0.5f, MessageGapSeconds = 2f, TestFireDelaySeconds = 0.3f;
    private const int MaxPressesPerStep = 3;

    // Why a macro didn't start at the last check (the first gate that failed), for the state text and `why`.
    internal enum Gate { None, Off, Invalid, Class, Trigger, Movement, Wait, Landing, NotReady, Tree, Blocked, Passed }

    // One macro's last start check: the gate it stopped at, its tree state, and the skill that wasn't ready.
    internal sealed class MacroCheck
    {
        public readonly TreeState Tree = new();
        public Gate Gate;
        public float WaitLeft;
        public string? Skill;
        public int Frame = -1;
    }

    private sealed class Engine
    {
        public Engine(DispatchClient library, DispatchClient drinks)
        {
            Library = library;
            Drinks = drinks;
        }

        public readonly DispatchClient Library, Drinks;
        public MacroRun? Run { get; set; }
        // Last fired time per macro id (unscaled); absent = never. Toggle trigger states per macro id. Not saved.
        public readonly Dictionary<string, float> Fired = new();
        public readonly Dictionary<string, bool> Toggled = new();
        public readonly Dictionary<Macro, MacroCheck> Checks = new();
        public readonly Dictionary<string, float> LastMessage = new();
        // Why each macro's last run ended ("done", "taken over by …"), by macro id.
        public readonly Dictionary<string, string> LastCause = new();
        public int LibraryGeneration = -1, ProfileGeneration = -1;
        public string? Class;
        // Test fire: the armed macro, and when the menu-open rule last cleared (NaN while it holds).
        public string? Armed;
        public float ArmedClearAt = float.NaN;
        public string? TestResult;
        public bool ZoneChanged;
        public float ChecksShownAt = float.NegativeInfinity;
        // The sets the engine walks: the library's in order, then the dev bench set; rebuilt when either changes.
        public readonly List<MacroSet> Sets = new();
        public int SetsGeneration = -1, SetsBench = -1;
        public MacroSet? Bench { get; set; }
        public int BenchVersion { get; set; }
    }

    // The engine's sets for this frame (no allocation unless the library or the bench changed).
    private static List<MacroSet> SetsOf(Engine engine)
    {
        int generation = S.LibraryDoc.Generation;
        if (engine.SetsGeneration == generation && engine.SetsBench == engine.BenchVersion) return engine.Sets;
        engine.SetsGeneration = generation;
        engine.SetsBench = engine.BenchVersion;
        engine.Sets.Clear();
        engine.Sets.AddRange(S.LibraryDoc.Doc.Sets);
        if (engine.Bench != null) engine.Sets.Add(engine.Bench);
        return engine.Sets;
    }

    private const float ShownCheckSeconds = 0.25f;

    // Every macro of the class checked without starting one, for the menu's state text while the library is held.
    private static void CheckForDisplay(Engine engine, MacroDoc doc, float now)
    {
        engine.ChecksShownAt = now;
        foreach (var set in SetsOf(engine))
        {
            if (!set.AppliesTo(engine.Class)) continue;
            foreach (var macro in set.Macros) Check(engine, set, macro, now);
        }
    }

    private static Engine? s_engine;

    /// <summary>Starts the engine with the feature's library and drinks clients; fired times start empty.</summary>
    public static void Start(DispatchClient library, DispatchClient drinks)
    {
        s_engine = new Engine(library, drinks);
        ConditionKinds.MacroFiredAt = FiredAt;
        ConditionKinds.CursorPoint = FrameCursor;
        ConditionKinds.MoreSkills = LibrarySkills;
        HotkeyConflicts.MoreKeys = TriggerKeys;
    }

    // Trigger keys of the library's macros, for the mod's bindings' conflict warnings.
    private static IEnumerable<ConditionKeys.KeyUse> TriggerKeys() =>
        S.LibraryDoc.Doc.Sets.SelectMany(s => s.Macros).Where(m => m.Trigger.NeedsKey && m.Trigger.Key.Length > 0)
            .Select(m => new ConditionKeys.KeyUse($"trigger of macro {m.Name}", m.Trigger.Key, m.Trigger.Kind != "hold"));

    /// <summary>Drops the engine; the dispatcher lets go of what the library and drinks clients hold.</summary>
    public static void Shutdown()
    {
        s_engine = null;
        ConditionKinds.MacroFiredAt = null;
        ConditionKinds.CursorPoint = null;
        ConditionKinds.MoreSkills = null;
        HotkeyConflicts.MoreKeys = null;
    }

    private static int s_cursorFrame = -1;
    private static Vector3? s_cursor;

    // The cursor point once a frame, for cursor lines.
    private static Vector3? FrameCursor()
    {
        if (Time.frameCount != s_cursorFrame)
        {
            s_cursorFrame = Time.frameCount;
            s_cursor = SlotMacros.CursorPoint();
        }
        return s_cursor;
    }

    /// <summary>The context a library macro's trees are checked in: its id, the feature's tally, now.</summary>
    internal static ConditionContext ContextOf(Macro macro) => SlotMacros.ContextFor(-1) with { Macro = macro.Id, CastTarget = CastTargetOf(macro, 0) };

    /// <summary>The context of a step's tree: a wait until's cast target is the next cast step's, a channel's its own.</summary>
    internal static ConditionContext ContextOf(Macro macro, int step) => SlotMacros.ContextFor(-1) with { Macro = macro.Id, CastTarget = StepCastTarget(macro, step) };

    // "The cast target" for a tree: the first switched-on cast step's from a step on, or null with none.
    private static TargetSpec? CastTargetOf(Macro macro, int from)
    {
        var steps = macro.Actions;
        for (int i = from; i < steps.Count; i++)
        {
            if (steps[i].On && steps[i].Step == "cast" && steps[i].Raw == null) return steps[i].Target;
        }
        return null;
    }

    private static TargetSpec? StepCastTarget(Macro macro, int step) =>
        macro.Actions.ElementAtOrDefault(step) is { Step: "cast", Raw: null } own ? own.Target : CastTargetOf(macro, step + 1);

    /// <summary>The running macro's name, or null.</summary>
    public static string? RunningName => s_engine?.Run?.Macro.Name;

    /// <summary>The run going now, for dev output; null when none.</summary>
    internal static MacroRun? CurrentRun => s_engine?.Run;

    /// <summary>Whether the library client holds a channelled skill on a slot (so letting go also stops the channel).</summary>
    public static bool LibraryChannelled(int slot) => s_engine?.Run is { } run && run.ChannelSlot == slot;

    /// <summary>
    /// Whether a run has a pending cast on a bar slot (0-based), holds it or channels it: the slots client then leaves
    /// that slot alone ("in use by a macro").
    /// </summary>
    public static bool SlotInUse(int slot) => s_engine?.Run is { } run && run.CastSlot == slot + 1;

    /// <summary>Whether the character's class has an enabled macro in an enabled set (with Run macros on).</summary>
    public static bool HasEnabled
    {
        get
        {
            if (s_engine is not { } engine || !S.Enabled.Value || engine.Class == null) return false;
            foreach (var set in SetsOf(engine))
            {
                if (!set.On || !set.AppliesTo(engine.Class)) continue;
                foreach (var macro in set.Macros)
                {
                    if (macro.On) return true;
                }
            }
            return false;
        }
    }

    /// <summary>When a macro (by id) last fired, or negative infinity for never (macro.sinceFired).</summary>
    public static float FiredAt(string macroId) =>
        s_engine != null && s_engine.Fired.TryGetValue(macroId, out float at) ? at : float.NegativeInfinity;

    // The character's class's skills from game data (the picker's last group).
    private static IEnumerable<string> ClassSkills() => GameData.SkillsFor(s_engine?.Class ?? PickedClass);

    private static IEnumerable<string> LibrarySkills() =>
        S.LibraryDoc.Doc.Sets.SelectMany(s => s.Macros).SelectMany(m => m.CastSkills()).Concat(ClassSkills());

    /// <summary>Adds the enemies the character's class's enabled macros (and the running and open ones) are about.</summary>
    public static void EnemySubjects(List<SubjectKey> into)
    {
        if (s_engine is not { } engine || engine.Class == null) return;
        foreach (var macro in ScannedMacros(engine))
        {
            ConditionEditor.EnemySubjects(macro.All, into);
            foreach (var step in macro.Actions)
            {
                if (step.Until != null) ConditionEditor.EnemySubjects(step.Until, into);
            }
        }
    }

    /// <summary>The slot (0-based) whose page is open, -1 for the Shared Conditions page, or null when neither shows:
    /// their targets are picked then, so the page's live pick has a value.</summary>
    internal static int? ShownSlotPage => !EditorShowing || s_editing is not { } key ? null
        : key == EveryKey ? -1
        : key.StartsWith(SlotsKey + "/") && int.TryParse(key[(SlotsKey.Length + 1)..], out int slot) && slot is >= 1 and <= SlotMacros.Slots ? slot - 1 : null;

    /// <summary>The sticky targets of the class's enabled macros (and the running and open ones): each switched-on cast
    /// step's.</summary>
    public static void CollectTargets()
    {
        if (s_engine is not { } engine || engine.Class == null) return;
        bool named = Targets.WantOwners;
        foreach (var macro in ScannedMacros(engine))
        {
            ConditionEditor.EnemyTargets(macro.All, CastTargetOf(macro, 0), named ? macro.Name : null);
            var steps = macro.Actions;
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step.Until != null) ConditionEditor.EnemyTargets(step.Until, StepCastTarget(macro, i), named ? $"{macro.Name} step {i + 1}" : null);
                if (!step.On || step.Step != "cast" || step.Raw != null) continue;
                var target = step.Target;
                if (!target.Sticky || target.Invalid(TargetOwner.Step) != null) continue;
                Targets.Collect(target.Key, named ? $"{macro.Name} step {i + 1}" : null);
            }
        }
    }

    // The macros whose lines the scan counts for: the class's enabled ones, the running one, the one open in the editor.
    private static IEnumerable<Macro> ScannedMacros(Engine engine)
    {
        foreach (var set in SetsOf(engine))
        {
            if (!set.On || !set.AppliesTo(engine.Class)) continue;
            foreach (var macro in set.Macros)
            {
                if (macro.On) yield return macro;
            }
        }
        if (engine.Run is { } run) yield return run.Macro;
        if (EditorShowing && s_editing is { } key && Split(key) is var (setId, macroId) && macroId != null
            && S.LibraryDoc.Doc.Find(setId, macroId).Macro is { } open) yield return open;
    }

    /// <summary>Adds the counts the character's class's enabled macros (and the running one's steps) need.</summary>
    public static void EnemyKeys(List<TallyKey> into)
    {
        if (s_engine is not { } engine || engine.Class == null) return;
        foreach (var set in SetsOf(engine))
        {
            if (!set.On || !set.AppliesTo(engine.Class)) continue;
            foreach (var macro in set.Macros)
            {
                if (macro.On) ConditionEditor.EnemyRanges(macro.All, into);
            }
        }
        if (engine.Run?.Step is { Until: { } until }) ConditionEditor.EnemyRanges(until, into);
        // The macro open in the editor, so its live values count.
        if (EditorShowing && s_editing is { } key && Split(key) is var (setId, macroId) && macroId != null
            && S.LibraryDoc.Doc.Find(setId, macroId).Macro is { } open)
        {
            ConditionEditor.EnemyRanges(open.All, into);
            foreach (var step in open.Actions)
            {
                if (step.Until != null) ConditionEditor.EnemyRanges(step.Until, into);
            }
        }
    }

    // ---- Each frame ----

    /// <summary>
    /// The engine's frame, from the feature's update after the slot refresh and before the slot macros' requests: reads
    /// last frame's outcomes, cancels on any cause, takes over or starts a run, and lets the run make its one request.
    /// </summary>
    public static void Update()
    {
        if (s_engine is not { } engine) return;
        float now = Clock.Now;
        PlayerModel.SampleMovement();
        // The cast signal is read every frame so no cast is missed between reads.
        PlayerModel.LastCastAt(1);

        var doc = S.LibraryDoc.Doc;
        // A library from before a shipped set gets it once (each profile's, as it comes into effect).
        if (doc.NeedsShipped && S.LibraryDoc.Edit(d => d.AddShipped(), out _)) doc = S.LibraryDoc.Doc;
        int generation = S.LibraryDoc.Generation;
        if (generation != engine.LibraryGeneration)
        {
            engine.LibraryGeneration = generation;
            engine.Checks.Clear();
            if (engine.Run != null) Cancel(engine, "edited");
            Disarm(engine);
        }
        if (Profiles.Generation != engine.ProfileGeneration)
        {
            if (engine.ProfileGeneration >= 0)
            {
                Cancel(engine, "character or profile change");
                Disarm(engine);
                engine.Toggled.Clear();
            }
            engine.ProfileGeneration = Profiles.Generation;
        }
        string? cls = PlayerModel.ClassName;
        // An unread class keeps the last one (the read can miss a frame); a new one cancels.
        if (cls != null && cls != engine.Class)
        {
            if (engine.Class != null)
            {
                Cancel(engine, "class change");
                Disarm(engine);
            }
            engine.Class = cls;
        }

        if (engine.ZoneChanged)
        {
            engine.ZoneChanged = false;
            Cancel(engine, "zone change");
            Disarm(engine);
        }
        if (!GameState.PlayerAlive)
        {
            Cancel(engine, "death");
            Disarm(engine);
            return;
        }
        if (GameState.Loading)
        {
            Cancel(engine, "loading");
            return;
        }
        // While a rule holds the library, drink-only macros (drinks, waits, messages) go on when the drinks client may act:
        // the menu being open doesn't stop a potion.
        bool drinksOnly = engine.Library.Blocking != null && engine.Drinks.Blocking == null;
        if (engine.Library.Blocking is { } rule)
        {
            if (!(drinksOnly && engine.Run is { } held && DrinkOnly(held.Macro))) Cancel(engine, "paused: " + rule.Reason);
            // Test fire waits for the menu (and any other rule) to clear.
            engine.ArmedClearAt = float.NaN;
        }

        PollToggles(engine, doc);
        if (engine.Run is { } run)
        {
            if (now - run.StartedAt > RunCapSeconds) Cancel(engine, "took too long");
            else if (run.CanBeTakenOver && TakeOver(engine, doc, run) is { } higher) Cancel(engine, "taken over by " + higher.Name);
        }
        if (engine.Run == null && (engine.Library.Blocking == null || drinksOnly)) StartOne(engine, doc, now, drinksOnly);
        // Held by a rule (the menu open): no other start, but the states the Macros tab shows stay current.
        if (engine.Library.Blocking != null && now - engine.ChecksShownAt >= ShownCheckSeconds) CheckForDisplay(engine, doc, now);
        if (engine.Run is { } current) Advance(engine, current, now);
        SampleCharts(engine, now);
    }

    /// <summary>Cancels the run (the feature switching off, a zone change); its keys are let go this frame.</summary>
    public static void Cancel(string cause)
    {
        if (s_engine is not { } engine) return;
        Cancel(engine, cause);
        Disarm(engine);
    }

    /// <summary>A zone change: the run is cancelled at the next frame (or now, if the feature is off).</summary>
    public static void OnZoneChanged()
    {
        if (s_engine != null) s_engine.ZoneChanged = true;
    }

    private static void Cancel(Engine engine, string cause)
    {
        if (engine.Run is not { } run) return;
        Dispatcher.Release(engine.Library);
        Dispatcher.Release(engine.Drinks);
        run.Cause = cause;
        engine.LastCause[run.Macro.Id] = cause;
        engine.Run = null;
        Log.Debug($"Macros: {run.Macro.Name} cancelled ({cause}) at step {run.StepIndex + 1}");
        if (run.TestFire) FinishTest(engine, run);
    }

    private static void Finish(Engine engine, MacroRun run, string cause)
    {
        Dispatcher.Release(engine.Library);
        Dispatcher.Release(engine.Drinks);
        run.Cause = cause;
        engine.LastCause[run.Macro.Id] = cause;
        engine.Run = null;
        if (run.TestFire) FinishTest(engine, run);
    }

    private static void Disarm(Engine engine)
    {
        if (engine.Armed == null) return;
        engine.Armed = null;
        engine.TestResult = "test fire: not started (cancelled)";
    }

    // ---- Triggers and the start check ----

    // A toggle trigger flips on each press of its key, whatever else the macro waits on; it starts off.
    private static void PollToggles(Engine engine, MacroDoc doc)
    {
        foreach (var set in SetsOf(engine))
        {
            if (!set.AppliesTo(engine.Class)) continue;
            foreach (var macro in set.Macros)
            {
                if (macro.Trigger.Kind != "toggle" || macro.Trigger.Key.Length == 0) continue;
                if (!Hotkeys.PressedNow(ConditionKinds.ChordOf(macro.Trigger.Key), HotkeyScope.Gameplay)) continue;
                engine.Toggled[macro.Id] = !(engine.Toggled.TryGetValue(macro.Id, out bool on) && on);
            }
        }
    }

    /// <summary>Whether a toggle-triggered macro is toggled on now.</summary>
    public static bool ToggledOn(string macroId) => s_engine != null && s_engine.Toggled.TryGetValue(macroId, out bool on) && on;

    // The first macro in library order that passes its start check starts (or the armed test fire, after its delay).
    private static void StartOne(Engine engine, MacroDoc doc, float now, bool drinksOnly = false)
    {
        if (engine.Armed is { } armed && !drinksOnly)
        {
            if (float.IsNaN(engine.ArmedClearAt)) engine.ArmedClearAt = now;
            if (now - engine.ArmedClearAt < TestFireDelaySeconds) return;
            engine.Armed = null;
            foreach (var set in SetsOf(engine))
            {
                var macro = set.Macros.FirstOrDefault(m => m.Id == armed);
                if (macro == null) continue;
                if (macro.Invalid != null)
                {
                    engine.TestResult = $"test fire: {macro.Name} can't run ({macro.Invalid})";
                    Toasts.Show("macros", engine.TestResult, ToastKind.Warning);
                    return;
                }
                Begin(engine, set, macro, now, testFire: true);
                return;
            }
            return;
        }
        if (engine.Class == null || !S.Enabled.Value) return;
        foreach (var set in SetsOf(engine))
        {
            if (!set.AppliesTo(engine.Class)) continue;
            foreach (var macro in set.Macros)
            {
                if (drinksOnly && !DrinkOnly(macro)) continue;
                if (Check(engine, set, macro, now) != Gate.Passed) continue;
                Begin(engine, set, macro, now, testFire: false);
                return;
            }
        }
    }

    // A higher macro (earlier in library order) whose start check passes, or null.
    private static Macro? TakeOver(Engine engine, MacroDoc doc, MacroRun run)
    {
        float now = Clock.Now;
        foreach (var set in SetsOf(engine))
        {
            if (!set.AppliesTo(engine.Class)) continue;
            foreach (var macro in set.Macros)
            {
                if (macro.Id == run.Macro.Id) return null;
                if (Check(engine, set, macro, now) == Gate.Passed) return macro;
            }
        }
        return null;
    }

    private static MacroCheck CheckOf(Engine engine, Macro macro)
    {
        if (!engine.Checks.TryGetValue(macro, out var check)) engine.Checks[macro] = check = new MacroCheck();
        return check;
    }

    // The start check (contracts/engine.md), cheap gates first; the tree only when they pass.
    private static Gate Check(Engine engine, MacroSet set, Macro macro, float now)
    {
        var check = CheckOf(engine, macro);
        check.Frame = Time.frameCount;
        check.Skill = null;
        return check.Gate = CheckGates(engine, set, macro, now, check);
    }

    private static Gate CheckGates(Engine engine, MacroSet set, Macro macro, float now, MacroCheck check)
    {
        if (!set.On || !macro.On) return Gate.Off;
        if (set.Invalid != null || macro.Invalid != null) return Gate.Invalid;
        if (!set.KnownClass || !set.AppliesTo(engine.Class)) return Gate.Class;
        if (!TriggerOn(engine, macro)) return Gate.Trigger;
        if (macro.Move != "any")
        {
            if (PlayerModel.Moving is not bool moving || moving != (macro.Move == "moving")) return Gate.Movement;
        }
        if (engine.Fired.TryGetValue(macro.Id, out float fired))
        {
            check.WaitLeft = fired + macro.WaitMs / 1000f - now;
            if (check.WaitLeft > 0f) return Gate.Wait;
        }
        // A drink still landing holds back every macro that drinks: its rule may still see the health from before it.
        if (Dispatcher.DrinkLanding && Drinks(macro)) return Gate.Landing;
        foreach (var (skill, ticked) in macro.Ready)
        {
            if (!ticked || !CastsSkill(macro, skill)) continue;
            int number = PlayerModel.SlotOf(skill);
            // Unknown readiness counts as ready, as slot macros take it.
            if (number <= 0 || PlayerModel.Slot(number)?.Ready == false)
            {
                check.Skill = skill;
                return Gate.NotReady;
            }
        }
        var verdict = Conditions.Check(macro.All, ContextOf(macro), check.Tree);
        return verdict.Passed ? Gate.Passed : Gate.Tree;
    }

    // Whether a switched-on cast step casts the skill (no allocation, for the start check).
    private static bool CastsSkill(Macro macro, string skill)
    {
        var steps = macro.Actions;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].On && steps[i].Step == "cast" && steps[i].Skill == skill) return true;
        }
        return false;
    }

    // A macro whose switched-on steps only drink, wait or show a message: it presses no skill and opens no portal.
    private static bool DrinkOnly(Macro macro)
    {
        bool drinks = false;
        var steps = macro.Actions;
        for (int i = 0; i < steps.Count; i++)
        {
            if (!steps[i].On) continue;
            if (steps[i].Step == "drink") drinks = true;
            else if (steps[i].Step is not ("wait" or "waitUntil" or "message")) return false;
        }
        return drinks;
    }

    private static bool Drinks(Macro macro)
    {
        var steps = macro.Actions;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].On && steps[i].Step == "drink") return true;
        }
        return false;
    }

    private static bool TriggerOn(Engine engine, Macro macro) => macro.Trigger.Kind switch
    {
        "auto" => true,
        "press" => Hotkeys.PressedNow(ConditionKinds.ChordOf(macro.Trigger.Key), HotkeyScope.Gameplay),
        "hold" => Hotkeys.Held(ConditionKinds.ChordOf(macro.Trigger.Key), HotkeyScope.Gameplay),
        "toggle" => engine.Toggled.TryGetValue(macro.Id, out bool on) && on,
        _ => false,
    };

    private static void Begin(Engine engine, MacroSet set, Macro macro, float now, bool testFire)
    {
        var run = new MacroRun(set, macro, now, testFire);
        engine.Run = run;
        engine.LastCause.Remove(macro.Id);
        Log.Debug($"Macros: {macro.Name} started{(testFire ? " (test fire)" : string.Empty)}");
        if (testFire) engine.TestResult = $"test fire: {macro.Name} running";
        else if (S.DebugNotifications.Value && engine.Checks.TryGetValue(macro, out var check)) Toasts.Show("macros", StartText(macro, check), ToastKind.Info, repeats: true);
    }

    // "Auto potion: Health below 35% (33%) → drink a potion": what set the macro off and what it does first.
    private static string StartText(Macro macro, MacroCheck check)
    {
        string? key = macro.Trigger.NeedsKey ? $"{macro.Trigger.Kind} {ConditionKinds.KeyName(macro.Trigger.Key)}" : null;
        string? line = TriggerLine(check.Tree);
        string cause = key != null && line != null ? $"{key}, {line}" : key ?? line ?? "no lines";
        var first = macro.Actions.FirstOrDefault(s => s.On);
        return $"{macro.Name}: {cause}{(first != null ? " → " + FirstWords(first) : string.Empty)}";
    }

    // A first cast step with what it aims at now: "cast Shadow Rend at Draal Lava Keeper (rare, 12 m)", "cast Meteor at
    // the pack centre (7 enemies)", "cast Shadow Rend at cursor: no rare or better within 20 m", "Shadow Rend skipped: no
    // target"; other steps as their line.
    private static string FirstWords(MacroStep step)
    {
        if (step.Step != "cast" || step.Raw != null || step.Invalid != null) return StepWords(step);
        string skill = ConditionKinds.SkillName(step.Skill);
        var aimed = AimAt(step.Target);
        if (aimed.NoTarget) return step.Target.Else == "wait" ? $"{skill} waits for a target ({aimed.Words})" : $"{skill} skipped: no target";
        return $"cast {skill} at {aimed.Words}";
    }

    // The condition that most recently began to count: the one whose change let the tree pass.
    private static string? TriggerLine(TreeState tree)
    {
        if (tree.List is not { } list) return null;
        int best = -1;
        float since = float.NegativeInfinity;
        for (int i = 0; i < list.NodeCount && i < tree.Counted.Length; i++)
        {
            var node = list.Nodes[i];
            if (node.Group != null || !node.On || !tree.Counted[i] || float.IsNaN(tree.PassingSince[i])) continue;
            if (tree.PassingSince[i] >= since)
            {
                since = tree.PassingSince[i];
                best = i;
            }
        }
        if (best < 0) return null;
        return $"{list.Nodes[best].Words} ({Conditions.ValueText(list.Nodes[best], tree.Results[best])})";
    }

    /// <summary>
    /// Arms a test fire of a macro (by id): it starts 0.3 s after nothing holds the library client any more (the menu
    /// closing), skipping trigger, pre-conditions and must be ready; any cancel cause before that disarms it.
    /// </summary>
    public static bool Arm(string macroId)
    {
        if (s_engine is not { } engine) return false;
        if (engine.Run != null) Cancel(engine, "test fire of another macro");
        engine.Armed = macroId;
        engine.ArmedClearAt = float.NaN;
        engine.TestResult = "test fire: armed";
        return true;
    }

    /// <summary>The last test fire's result line, or null.</summary>
    public static string? TestResult => s_engine?.TestResult;

    private static void FinishTest(Engine engine, MacroRun run)
    {
        int skipped = run.Results.Count(r => r.StartsWith("skipped"));
        string skips = string.Join(", ", run.Results.Where(r => r.StartsWith("skipped")).Select(r => r[8..]).Distinct());
        engine.TestResult = $"test fire: {run.Macro.Name}, {run.Results.Count} steps, {skipped} skipped{(skipped > 0 ? $" ({skips})" : string.Empty)}"
            + (run.Cause is "done" ? string.Empty : $", ended: {run.Cause}");
        Toasts.Show("macros", engine.TestResult, skipped > 0 || run.Cause != "done" ? ToastKind.Warning : ToastKind.Info);
    }

    // ---- State text for the menu, the overlay and `why` ----

    /// <summary>A state shown outside the game (character select), where last checks aren't current.</summary>
    internal const string NoLiveData = "no live data";

    // A valid, enabled macro's state: running step n, waiting on what stopped its last check, ready.
    private static (string Text, StatusState Shape) EngineState(MacroSet set, Macro macro)
    {
        if (s_engine is not { } engine) return ("off", StatusState.Off);
        if (engine.Run is { } run && run.Macro.Id == macro.Id) return ($"running step {run.StepIndex + 1}", StatusState.Active);
        if (engine.Armed == macro.Id) return ("test fire armed", StatusState.Waiting);
        if (!GameState.InGame) return (NoLiveData, StatusState.Waiting);
        if (engine.Class == null) return ("class not read", StatusState.Waiting);
        if (!set.AppliesTo(engine.Class)) return ("other class", StatusState.Off);
        if (!engine.Checks.TryGetValue(macro, out var check) || check.Frame < 0) return ("not checked", StatusState.Waiting);
        return GateText(macro, check);
    }

    internal static (string Text, StatusState Shape) GateText(Macro macro, MacroCheck check) => check.Gate switch
    {
        Gate.Passed => ("ready", StatusState.Active),
        Gate.Trigger => (macro.Trigger.Kind switch
        {
            "press" => $"waiting for {ConditionKinds.KeyName(macro.Trigger.Key)}",
            "hold" => $"waiting for {ConditionKinds.KeyName(macro.Trigger.Key)} held",
            "toggle" => $"toggled off ({ConditionKinds.KeyName(macro.Trigger.Key)})",
            _ => "waiting",
        }, StatusState.Waiting),
        Gate.Movement => (macro.Move == "moving" ? "waiting: not moving" : "waiting: moving", StatusState.Holding),
        Gate.Wait => ($"wait {ArgSpec.Format(Math.Max(0f, check.WaitLeft))} s", StatusState.Holding),
        Gate.Landing => ("waiting: last drink landing", StatusState.Holding),
        Gate.NotReady => ($"waiting: {ConditionKinds.SkillName(check.Skill ?? string.Empty)} not ready", StatusState.Holding),
        Gate.Tree => ("waiting on " + FailingWords(macro.All, check.Tree), StatusState.Holding),
        Gate.Off => ("off", StatusState.Off),
        Gate.Invalid => ("invalid", StatusState.Waiting),
        _ => ("waiting", StatusState.Waiting),
    };

    /// <summary>The first failing line of a tree, as words with its value: "health below 50% (78%)".</summary>
    internal static string FailingWords(ConditionList list, TreeState state)
    {
        int node = state.Verdict.FirstFailing;
        if (node < 0 || node >= list.NodeCount) return list.Invalid ?? "conditions";
        var entry = list.Nodes[node];
        string value = node < state.Results.Length ? Conditions.ValueText(entry, state.Results[node]) : string.Empty;
        return value.Length > 0 ? $"{entry.Words} ({value})" : entry.Words;
    }

    /// <summary>A macro's last start check, for dev output; null when it wasn't checked.</summary>
    internal static MacroCheck? LastCheck(Macro macro) => s_engine != null && s_engine.Checks.TryGetValue(macro, out var check) ? check : null;

    /// <summary>Why a macro's last run ended, or null.</summary>
    internal static string? LastCauseOf(string macroId) => s_engine != null && s_engine.LastCause.TryGetValue(macroId, out string? cause) ? cause : null;

    /// <summary>The class the engine runs (the character's, as last read), or null.</summary>
    internal static string? EngineClass => s_engine?.Class;

#if DEV
    // ---- Dev explanations (why macros, why macro) ----

    /// <summary>
    /// Every macro of the character's class (and the five slot macros): on, trigger, movement, wait left, must be ready,
    /// verdict, first failing path, run state. With a name, that macro alone in full: every node's value, result and
    /// reason, and its run (step, elapsed, what it waits on, presses, fired, last cause).
    /// </summary>
    public static List<string> Explain(string? name)
    {
        var lines = new List<string>();
        var engine = s_engine;
        var doc = S.LibraryDoc.Doc;
        float now = Clock.Now;
        var run = engine?.Run;
        lines.Add($"ok why macros: run macros {(S.Enabled.Value ? "on" : "off")}, class {engine?.Class ?? "not read"}, "
            + $"library {(engine?.Library.Blocking is { } rule ? $"held by {rule.Name} ({rule.Reason})" : "allowed")}, "
            + $"running {(run != null ? $"{run.Macro.Name} step {run.StepIndex + 1}" : "none")}, t={now.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
        if (name == null)
        {
            for (int i = 0; i < SlotMacros.Slots; i++)
            {
                var status = MacroSettingsPage.SlotStatus(i);
                lines.Add($"slot macro {i + 1} {SlotMacros.SlotAbilityName(i) ?? "-"}: {status.Text}{(SlotInUse(i) ? " (in use by a macro)" : string.Empty)}");
            }
        }
        foreach (var set in doc.Sets)
        {
            if (name == null && !set.AppliesTo(engine?.Class ?? PickedClass)) continue;
            foreach (var macro in set.Macros)
            {
                if (name != null && !macro.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                lines.Add(Summary(engine, set, macro, now));
                if (name != null) Detail(lines, engine, set, macro, now);
            }
        }
        if (name != null && lines.Count == 1) lines[0] = $"error why macro: no macro named \"{name}\"";
        return lines;
    }

    private static string Summary(Engine? engine, MacroSet set, Macro macro, float now)
    {
        string on = !S.Enabled.Value ? "master off" : !set.On ? "set off" : !macro.On ? "macro off" : !set.AppliesTo(engine?.Class) ? "other class" : "on";
        var check = engine != null && engine.Checks.TryGetValue(macro, out var c) ? c : null;
        string wait = engine != null && engine.Fired.TryGetValue(macro.Id, out float fired) ? $"fired {ArgSpec.Format(now - fired)} s ago" : "never fired";
        string ready = string.Join(",", macro.Ready.Where(r => r.Value).Select(r => $"{ConditionKinds.SkillName(r.Key)}:{ReadyWord(r.Key)}"));
        string verdict = check == null ? "not checked" : GateText(macro, check).Text;
        string path = check is { Gate: Gate.Tree } && check.Tree.List != null && check.Tree.Verdict.FirstFailing is int f and >= 0
            ? $" path {check.Tree.List.Path(f)}{FailingWords(check.Tree.List, check.Tree)} {Conditions.StateWord(check.Tree.Results[f].State)}" : string.Empty;
        string state = engine?.Run is { } run && run.Macro.Id == macro.Id ? $" run: step {run.StepIndex + 1}/{macro.Actions.Count} {run.Step?.Step} {RunWaiting(run)}"
            : LastCauseOf(macro.Id) is { } cause ? $" last run: {cause}" : string.Empty;
        return $"macro {set.Name}/{macro.Name} ({set.Id}/{macro.Id}): on {on}, trigger {macro.Trigger.Kind}{(macro.Trigger.Key.Length > 0 ? " " + macro.Trigger.Key : string.Empty)}, "
            + $"move {macro.Move}, wait {ArgSpec.Format(macro.WaitMs)} ms ({wait}), ready [{ready}], verdict {verdict}{path}{state}";
    }

    private static string ReadyWord(string skill)
    {
        int slot = PlayerModel.SlotOf(skill);
        if (slot == PlayerModel.NotOnBar) return "not on bar";
        if (slot == PlayerModel.BarUnknown) return "bar unknown";
        return PlayerModel.Slot(slot)?.Ready switch { true => "ready", false => "not ready", _ => "unknown" };
    }

    private static void Detail(List<string> lines, Engine? engine, MacroSet set, Macro macro, float now)
    {
        var state = new TreeState();
        var verdict = Conditions.Check(macro.All, ContextOf(macro), state);
        lines.Add($"tree: {(verdict.Passed ? "pass" : "fail")}{(macro.All.Invalid != null ? $" (invalid: {macro.All.Invalid})" : string.Empty)}");
        for (int i = 0; i < macro.All.NodeCount; i++)
        {
            var node = macro.All.Nodes[i];
            string indent = new(' ', 2 * macro.All.Depth(i));
            var result = state.Results[i];
            string value = Conditions.ValueText(node, result);
            string counted = !node.On ? "off" : state.Counted[i] ? "counts" : "doesn't count";
            string extra = node.For > 0f ? $" for {ArgSpec.Format(node.For)} s" : string.Empty;
            lines.Add($"{indent}{RuleTreeRows.PathOf(macro.All, i)} {node.Words}{extra}  {value}  {Conditions.StateWord(result.State)}  {counted}"
                + (result.Reason != null ? $" ({result.Reason})" : string.Empty));
        }
        for (int i = 0; i < macro.Actions.Count; i++)
        {
            var step = macro.Actions[i];
            lines.Add($"step {i + 1}: {StepWords(step)}{(step.On ? string.Empty : " (off)")}");
            if (step.Step == "cast" && step.Raw == null && step.Target.Pick != TargetPick.Cursor) lines.Add($"  target: {TargetLine(step.Target)}");
        }
        if (engine?.Run is { } run && run.Macro.Id == macro.Id)
        {
            lines.Add($"run: step {run.StepIndex + 1}/{macro.Actions.Count} {run.Step?.Step}, elapsed {ArgSpec.Format(now - run.StartedAt)} s, waiting on {RunWaiting(run)}, "
                + $"presses {run.Presses}, fired {(run.FiredAt is float f ? ArgSpec.Format(now - f) + " s ago" : "not yet")}, results [{string.Join(", ", run.Results)}]");
        }
        else if (LastCauseOf(macro.Id) is { } cause) lines.Add($"last run: {cause}");
        if (TestResult is { } test) lines.Add(test);
    }

    // "<words> → <pick> (kept 1.2 s, last change: higher rarity; left out: 2 mod)" for a kept target; nearest and hovered
    // as read now.
    private static string TargetLine(TargetSpec target)
    {
        string words = target.Words(TargetOwner.Condition);
        if (Targets.PickOf(target) is { } pick) return $"{words} → {Targets.Describe(pick)}";
        var aimed = AimAt(target);
        return $"{words} → {(aimed.NoTarget ? "nothing qualifies: " : string.Empty)}{aimed.Words} (read at the send)";
    }

    /// <summary>Test fire from the dev bridge: armed as the editor's button does, without closing a menu.</summary>
    public static string DevFire(string name)
    {
        var macro = S.LibraryDoc.Doc.Sets.SelectMany(s => s.Macros).FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (macro == null) return $"error macro fire: no macro named \"{name}\"";
        return Arm(macro.Id) ? $"ok macro fire: {macro.Name} armed (starts 0.3 s after nothing holds the library)" : "error macro fire: macros aren't running";
    }

    // ---- Dev bench (macro bench) ----

    /// <summary>
    /// Adds n generated macros for the character's class, in memory only (never saved): each with 12 lines (vitals, a
    /// buff, a skill, three enemy lines around hero and cursor, an any group and a none group) ending in a line that
    /// fails, so all are checked every frame and none starts. 0 removes them. With targets, each also has a cast step
    /// and two lines (Has a target, and a target's health) using one of 20 targets (10 area, radius 2–6 m; 10 single),
    /// so the scan picks them.
    /// </summary>
    public static string Bench(int count, bool targets = false)
    {
        if (s_engine is not { } engine) return "error macro bench: macros aren't running";
        engine.BenchVersion++;
        if (count <= 0)
        {
            engine.Bench = null;
            return "ok macro bench off";
        }
        string cls = engine.Class ?? PickedClass;
        string skill = PlayerModel.Slot(1)?.AbilityId ?? "none";
        var set = new MacroSet { Id = "bench", Name = "Bench", Class = cls, On = true };
        for (int i = 0; i < count; i++)
        {
            float range = 8 + i % 5 * 4;
            string text = $"{{\"id\":\"bench{i}\",\"name\":\"Bench {i + 1}\",\"on\":true,\"trigger\":{{\"kind\":\"auto\"}},\"waitMs\":0,\"all\":["
                + "{\"kind\":\"player.health\",\"args\":{\"compare\":\"below\",\"percent\":99},\"on\":true,\"trueWhenUnknown\":false},"
                + "{\"kind\":\"player.mana\",\"args\":{\"compare\":\"above\",\"percent\":1},\"on\":true,\"trueWhenUnknown\":false},"
                + "{\"kind\":\"buff.missing\",\"args\":{\"buff\":81},\"on\":true,\"trueWhenUnknown\":false},"
                + $"{{\"kind\":\"skill.ready\",\"args\":{{\"skill\":\"{skill}\"}},\"on\":true,\"trueWhenUnknown\":false}},"
                + $"{{\"kind\":\"enemies.weight\",\"args\":{{\"range\":{range},\"weight\":3}},\"on\":true,\"trueWhenUnknown\":false}},"
                + $"{{\"kind\":\"enemies.count\",\"args\":{{\"range\":{range + 2},\"count\":2,\"centre\":\"cursor\"}},\"on\":true,\"trueWhenUnknown\":false}},"
                + "{\"any\":["
                + $"{{\"kind\":\"enemies.rarity\",\"args\":{{\"range\":{range + 4},\"rarity\":\"rare\"}},\"on\":true,\"trueWhenUnknown\":false}},"
                + "{\"kind\":\"potions.available\",\"args\":{\"count\":1},\"on\":true,\"trueWhenUnknown\":false}]},"
                + "{\"none\":[{\"kind\":\"zone.kind\",\"args\":{\"is\":\"town\"},\"on\":true,\"trueWhenUnknown\":false}]},"
                + "{\"kind\":\"player.health\",\"args\":{\"compare\":\"below\",\"percent\":1},\"on\":true,\"trueWhenUnknown\":false}],"
                + $"\"actions\":[{{\"step\":\"wait\",\"ms\":50}}{(targets ? BenchCast(i, skill) : string.Empty)}]}}";
            var holder = MacroDoc.ParseSet(System.Text.Json.Nodes.JsonNode.Parse($"{{\"macros\":[{text}]}}"));
            set.Macros.Add(holder.Macros[0]);
        }
        if (targets)
        {
            // The target lines go before the last (failing) line, so they are checked too.
            for (int i = 0; i < set.Macros.Count; i++)
            {
                var lines = ConditionDoc.ItemsJson(set.Macros[i].All);
                var target = BenchTarget(i);
                target.Remove("else");
                var exists = new System.Text.Json.Nodes.JsonObject { ["subject"] = "target" };
                var health = new System.Text.Json.Nodes.JsonObject { ["subject"] = "target", ["compare"] = "below", ["percent"] = 50 };
                foreach (var (key, value) in target) exists[key] = ConditionDoc.Clone(value);
                foreach (var (key, value) in BenchTarget(i + 10)) if (key != "else" && key != "radius") health[key] = ConditionDoc.Clone(value);
                lines.Insert(lines.Count - 1, new System.Text.Json.Nodes.JsonObject { ["kind"] = "target.exists", ["args"] = exists, ["on"] = true, ["trueWhenUnknown"] = false });
                lines.Insert(lines.Count - 1, new System.Text.Json.Nodes.JsonObject { ["kind"] = "enemy.health", ["args"] = health, ["on"] = true, ["trueWhenUnknown"] = false });
                set.Macros[i].All = ConditionDoc.ParseItems(lines);
            }
        }
        engine.Bench = set;
        string with = targets ? $", {set.Macros.SelectMany(m => m.Actions).Select(s => s.Target.Key).Distinct().Count()} distinct cast targets" : string.Empty;
        return $"ok macro bench {count}{(targets ? " targets" : string.Empty)}: {count} macros of {set.Macros[0].All.NodeCount} lines for {cls}{with}, in memory only";
    }

    // One of 20 targets: 0–9 area (health, weight, count in turn; radius 2–6 m), 10–19 single-enemy picks.
    private static System.Text.Json.Nodes.JsonObject BenchTarget(int i)
    {
        int n = i % 20;
        string[] areas = { "areaHealth", "areaWeight", "areaCount" }, single = { "rarest", "mostHealth", "maxHealth", "lowHealth" };
        var target = n < 10
            ? new System.Text.Json.Nodes.JsonObject { ["pick"] = areas[n % 3], ["range"] = 20 + n, ["radius"] = 2 + n % 5, ["else"] = "skip" }
            : new System.Text.Json.Nodes.JsonObject { ["pick"] = single[n % 4], ["range"] = 10 + n, ["else"] = "skip" };
        return target;
    }

    private static string BenchCast(int i, string skill) =>
        $",{{\"step\":\"cast\",\"skill\":\"{skill}\",\"mode\":\"tap\",\"aim\":\"target\",\"target\":{BenchTarget(i).ToJsonString()},\"timeoutMs\":1000}}";
#endif
}
