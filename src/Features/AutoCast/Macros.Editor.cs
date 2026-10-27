using System.Text.Json.Nodes;
using EpochAssist.Core;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Features;

// The macro editor (contracts/menu.md) as menu data: the name; pre-conditions (trigger and its key, movement, wait
// between uses); must be ready (a switch per cast skill); the trigger tree; the sequence of steps, each opening its
// options under it. Rows are rebuilt when the library changes or another line opens; every change goes through the
// library setting (undo snapshots first).
internal static partial class Macros
{
    private static readonly string[] s_triggerLabels = { "automatic", "on key press", "while key held", "toggled by key" };
    private static readonly string[] s_moveLabels = { "don't care", "moving", "standing still" };
    private static readonly string[] s_modeLabels = { "tap", "hold", "channel" };
    private static readonly string[] s_timeoutLabels = { "stop the macro", "go on" };
    private static readonly string[] s_stepKinds = { "cast", "drink", "portal", "wait", "waitUntil", "message" };
    private static readonly string[] s_stepLabels = { "Cast a skill", "Drink a potion", "Open a town portal", "Wait", "Wait until", "Show a message" };

    // The open line ("trigger/2.1", "step/3", "until/3/1"), and the rows built for (editor, generation, open line).
    private static string? s_openLine;
    private static (string Key, int Generation, string? Open)? s_builtFor;
    private static List<MenuRow> s_editorRows = new();

    // The open editor's live tree states (the trigger, and each wait until or channel), checked once a frame.
    private static readonly TreeState s_liveTrigger = new();
    private static readonly Dictionary<int, TreeState> s_liveUntil = new();
    private static int s_liveFrame = -1;

    private static LibraryEditor? MacroEditor(string key)
    {
        var (setId, macroId) = Split(key);
        if (macroId == null || S.LibraryDoc.Doc.Find(setId, macroId).Macro == null) return null;
        var editor = new LibraryEditor(key, () => CurrentMacro(setId, macroId)?.Name ?? "?", () => EditorRows(key, setId, macroId))
        {
            Place = () => S.LibraryDoc.Doc.Find(setId).Set?.Name,
            Note = () => TestResult ?? (CurrentMacro(setId, macroId)?.Invalid is { } bad ? $"Never starts: {bad}." : null),
        };
        editor.Buttons.Add(Button("Undo", "undo", Undo, () => History.CanUndo, "Takes back the last change on this tab"));
        editor.Buttons.Add(Button("Test fire", "testfire", () => TestFire(setId, macroId), () => CurrentMacro(setId, macroId) is { Invalid: null },
            "Closes the menu and runs the sequence 0.3 s later, skipping trigger, pre-conditions and must be ready"));
        editor.Buttons.Add(Button("Close", "close", CloseEditor));
        return editor;
    }

    private static Macro? CurrentMacro(string setId, string macroId) => S.LibraryDoc.Doc.Find(setId, macroId).Macro;

    private static void TestFire(string setId, string macroId)
    {
        if (CurrentMacro(setId, macroId) is not { Invalid: null } macro) return;
        if (!Arm(macro.Id)) return;
        Core.Actions.Run("menu.close", "macro test fire");
    }

    private static bool EditMacro(string setId, string macroId, Action<Macro> change) => EditLibrary(doc =>
    {
        if (doc.Find(setId, macroId).Macro is not { } macro) return;
        change(macro);
        macro.SyncReady();
    });

    private static string? ReadOnlyReason() => S.LibraryDoc.Doc.ReadOnly ? "made by a newer version" : null;

    // The trigger tree's live state (null out of game), checked once a frame while the editor shows it.
    private static TreeState? LiveTrigger(string setId, string macroId)
    {
        if (!Player.Exists || CurrentMacro(setId, macroId) is not { } macro) return null;
        LiveFrame();
        if (!ReferenceEquals(s_liveTrigger.List, macro.All) || s_liveTrigger.Frame != UnityEngine.Time.frameCount)
        {
            Conditions.Check(macro.All, ContextOf(macro), s_liveTrigger);
            s_liveTrigger.Frame = UnityEngine.Time.frameCount;
        }
        return s_liveTrigger;
    }

    private static TreeState? LiveUntil(string setId, string macroId, int step)
    {
        if (!Player.Exists || CurrentMacro(setId, macroId)?.Actions.ElementAtOrDefault(step)?.Until is not { } until) return null;
        LiveFrame();
        if (!s_liveUntil.TryGetValue(step, out var state)) s_liveUntil[step] = state = new TreeState();
        if (state.Frame != UnityEngine.Time.frameCount)
        {
            Conditions.Check(until, CurrentMacro(setId, macroId) is { } m ? ContextOf(m, step) : AutoCast.ContextFor(-1), state);
            state.Frame = UnityEngine.Time.frameCount;
        }
        return state;
    }

    private static void LiveFrame() => s_liveFrame = UnityEngine.Time.frameCount;

    /// <summary>Whether the editor showed in the last second (its enemy lines are counted then).</summary>
    private static bool EditorShowing => s_editing != null && UnityEngine.Time.frameCount - s_liveFrame < 90;

    private static IReadOnlyList<MenuRow> EditorRows(string key, string setId, string macroId)
    {
        var built = (key, S.LibraryDoc.Generation, s_openLine);
        if (s_builtFor == built) return s_editorRows;
        s_builtFor = built;
        var rows = new List<MenuRow>();
        if (CurrentMacro(setId, macroId) is not { } macro)
        {
            s_editorRows = rows;
            return rows;
        }
        rows.Add(new HintRow(() => "No live data: enter the game to see values.") { Key = "nolive", VisibleWhen = () => !Player.Exists });
        rows.Add(new ValueTextField("Name", () => CurrentMacro(setId, macroId)?.Name ?? string.Empty, name =>
        {
            if (name.Trim().Length > 0) EditMacro(setId, macroId, m => m.Name = name);
        }, Macro.MaxName) { Key = "name" });

        rows.Add(new HeadingRow("Pre-conditions"));
        rows.Add(new ValueChoice("Trigger", () => Array.IndexOf(MacroTrigger.Kinds, CurrentMacro(setId, macroId)?.Trigger.Kind ?? "auto"), i =>
        {
            if (i >= 0 && i < MacroTrigger.Kinds.Length) EditMacro(setId, macroId, m => m.Trigger.Kind = MacroTrigger.Kinds[i]);
        }, () => s_triggerLabels) { Key = "trigger", Width = 150f });
        rows.Add(new ValueKey("Key", () => CurrentMacro(setId, macroId)?.Trigger.Key ?? string.Empty, chord => EditMacro(setId, macroId, m => m.Trigger.Key = chord))
        {
            Key = "triggerkey",
            VisibleWhen = () => CurrentMacro(setId, macroId)?.Trigger.NeedsKey == true,
            Warnings = () => CurrentMacro(setId, macroId)?.Trigger.Key is { Length: > 0 } chord ? HotkeyConflicts.ForCondition(chord, false) : Array.Empty<string>(),
        });
        rows.Add(new ValueChoice("Movement", () => Array.IndexOf(Macro.Moves, CurrentMacro(setId, macroId)?.Move ?? "any"), i =>
        {
            if (i >= 0 && i < Macro.Moves.Length) EditMacro(setId, macroId, m => m.Move = Macro.Moves[i]);
        }, () => s_moveLabels) { Key = "move", Width = 150f });
        rows.Add(new ValueNumber("Wait between uses", () => CurrentMacro(setId, macroId)?.WaitMs ?? 1000f, v => EditMacro(setId, macroId, m => m.WaitMs = v),
            0f, Macro.MaxWaitMs, 100f, " ms") { Key = "waitms" });

        rows.Add(new HeadingRow("Must be ready"));
        var skills = macro.CastSkills().ToList();
        if (skills.Count == 0) rows.Add(new HintRow(() => "The skills of cast steps show here.") { Key = "ready.none" });
        foreach (string skill in skills)
        {
            string id = skill;
            rows.Add(new ValueToggle(ConditionKinds.SkillName(id), () => CurrentMacro(setId, macroId)?.ReadyTicked(id) ?? true, on => EditMacro(setId, macroId, m =>
            {
                int index = m.Ready.FindIndex(r => r.Key == id);
                if (index >= 0) m.Ready[index] = new(id, on);
                else m.Ready.Add(new(id, on));
            })) { Key = "ready." + id });
        }

        rows.Add(new HeadingRow("Trigger"));
        RuleTreeRows.Add(rows, TreeFor("trigger", setId, macroId, m => m.All, () => LiveTrigger(setId, macroId)));

        rows.Add(new HeadingRow("Sequence"));
        AddSteps(rows, setId, macroId, macro);
        s_editorRows = rows;
        return rows;
    }

    // A tree of the macro (its trigger, or a step's until) as the rule tree rows read and change it.
    private static TreeAccess TreeFor(string prefix, string setId, string macroId, Func<Macro, ConditionList?> tree, Func<TreeState?> live) => new()
    {
        Prefix = prefix,
        List = () => CurrentMacro(setId, macroId) is { } m ? tree(m) : null,
        Edit = change => EditMacro(setId, macroId, m =>
        {
            if (tree(m) is { } list) change(list);
        }),
        State = live,
        Blocked = ReadOnlyReason,
        Open = s_openLine is { } open && open.StartsWith(prefix + "/") ? open[(prefix.Length + 1)..] : null,
        SetOpen = path => s_openLine = path == null ? null : prefix + "/" + path,
    };

    // ---- Steps ----

    private static void AddSteps(List<MenuRow> rows, string setId, string macroId, Macro macro)
    {
        for (int i = 0; i < macro.Actions.Count; i++)
        {
            int index = i;
            MacroStep? Step() => CurrentMacro(setId, macroId)?.Actions.ElementAtOrDefault(index);
            string path = "step/" + (index + 1);
            rows.Add(new TreeLine
            {
                Key = $"step.{index + 1}",
                Depth = 0,
                Number = $"{index + 1}.",
                Words = () => Step() is { } s ? StepWords(s) : string.Empty,
                On = () => Step()?.On ?? false,
                SetOn = on => EditMacro(setId, macroId, m =>
                {
                    if (m.Actions.ElementAtOrDefault(index) is { } s) s.On = on;
                }),
                Live = () => StepLive(macro.Id, index),
                Opened = s_openLine == path,
                Toggle = () => s_openLine = s_openLine == path ? null : path,
                Remove = () =>
                {
                    EditMacro(setId, macroId, m =>
                    {
                        if (index < m.Actions.Count) m.Actions.RemoveAt(index);
                    });
                    s_openLine = null;
                },
                Blocked = ReadOnlyReason,
                Tip = () => Step()?.Invalid is { } bad ? $"Skipped: {bad}" : null,
            });
            if (s_openLine == path && macro.Actions.ElementAtOrDefault(index) is { } step)
            {
                foreach (var row in StepOptions(setId, macroId, index, step)) rows.Add(new Indented(row, 1));
            }
        }
        rows.Add(new TreeAdd
        {
            Key = "step.add",
            Depth = 0,
            Label = "+ add a step",
            Choices = () => s_stepLabels,
            Picked = i =>
            {
                if (i < 0 || i >= s_stepKinds.Length) return;
                // The portal only from a key trigger, where the game's command exists.
                if (s_stepKinds[i] == "portal" && PortalBlocked(setId, macroId) is { } why)
                {
                    Report($"Can't add a town portal: {why}.", ToastKind.Warning);
                    return;
                }
                int added = -1;
                EditMacro(setId, macroId, m =>
                {
                    if (m.Actions.Count >= Macro.MaxSteps) return;
                    string? skill = s_stepKinds[i] == "cast" ? PlayerModel.Slot(1)?.AbilityId : null;
                    m.Actions.Add(MacroStep.New(s_stepKinds[i], skill));
                    added = m.Actions.Count;
                });
                if (added > 0) s_openLine = "step/" + added;
            },
            Blocked = () => ReadOnlyReason() ?? (CurrentMacro(setId, macroId)?.Actions.Count >= Macro.MaxSteps ? $"{Macro.MaxSteps} steps at most" : null),
        });
    }

    private static string? PortalBlocked(string setId, string macroId)
    {
        if (CurrentMacro(setId, macroId)?.Trigger.NeedsKey != true) return "only key-triggered macros can open one (set the trigger to a key first)";
        return Dispatcher.CanPortal || PlayerModel.Online == null ? null : "the game's portal command isn't there in this mode";
    }

    // The running macro's state on its current step, else nothing.
    private static (string, StatusState)? StepLive(string macroId, int index)
    {
        if (s_engine?.Run is not { } run || run.Macro.Id != macroId) return null;
        if (index < run.StepIndex) return (run.Results.ElementAtOrDefault(index) ?? "done", StatusState.Active);
        if (index == run.StepIndex) return (RunWaiting(run), StatusState.Waiting);
        return null;
    }

    /// <summary>A step as a line: "cast Smoke Bomb (tap, at cursor)", "wait 500 ms", "wait until 1 condition (2 s, then stop)".</summary>
    public static string StepWords(MacroStep step)
    {
        if (step.Raw != null) return step.Raw.ToJsonString();
        string text = step.Step switch
        {
            // The aims from before targets read as they did: "(tap, nearest enemy within 15 m)"; any other target after the
            // mode: "(tap) at the rarest enemy within 20 m, else at cursor".
            "cast" => step.Target is { Legacy: true } old
                ? $"cast {ConditionKinds.SkillName(step.Skill)} ({ModeWords(step)}, {old.Words(TargetOwner.Step)})"
                : $"cast {ConditionKinds.SkillName(step.Skill)} ({ModeWords(step)}) {step.Target.Words(TargetOwner.Step)}",
            "drink" => "drink a potion",
            "portal" => "open a town portal",
            "wait" => $"wait {ArgSpec.Format(step.Ms)} ms",
            "waitUntil" => $"wait until {step.Until?.Entries.Count ?? 0} condition{(step.Until?.Entries.Count == 1 ? string.Empty : "s")} pass "
                + $"({ArgSpec.Format(step.TimeoutMs / 1000f)} s, then {(step.OnTimeout == "go" ? "go on" : "stop")})",
            "message" => $"show \"{step.Message}\"",
            _ => $"{step.Step} (needs a newer version)",
        };
        return step.Invalid is { } bad && step.Known ? $"{text} — {bad}" : text;
    }

    private static string ModeWords(MacroStep step) => step.Mode != "tap" ? $"{step.Mode} {ArgSpec.Format(step.Ms)} ms" : step.Mode;

    private static IEnumerable<MenuRow> StepOptions(string setId, string macroId, int index, MacroStep step)
    {
        MacroStep? Current() => CurrentMacro(setId, macroId)?.Actions.ElementAtOrDefault(index);
        void Edit(Action<MacroStep> change) => EditMacro(setId, macroId, m =>
        {
            if (m.Actions.ElementAtOrDefault(index) is { } s) change(s);
        });
        void Set(string name, JsonNode? value) => Edit(s => s.Fields[name] = value);
        string prefix = $"step.{index + 1}.";
        switch (step.Step)
        {
            case "cast":
                yield return RuleTreeRows.SkillPicker("Skill", prefix + "skill", () => Current()?.Skill ?? string.Empty, id => Set("skill", JsonValue.Create(id)));
                yield return new ValueChoice("Mode", () => Array.IndexOf(MacroStep.Modes, Current()?.Mode ?? "tap"), i =>
                {
                    if (i < 0 || i >= MacroStep.Modes.Length) return;
                    Edit(s =>
                    {
                        s.Fields["mode"] = MacroStep.Modes[i];
                        if (MacroStep.Modes[i] != "tap" && !s.Has("ms")) s.Fields["ms"] = 1000;
                        if (MacroStep.Modes[i] == "channel") s.Until ??= new ConditionList();
                    });
                }, () => s_modeLabels) { Key = prefix + "mode", Width = 110f };
                yield return new ValueNumber("Hold for", () => Current()?.Ms ?? 1000f, v => Set("ms", JsonValue.Create(v)), 100f, 10000f, 50f, " ms")
                {
                    Key = prefix + "ms",
                    VisibleWhen = () => Current()?.Mode is "hold" or "channel",
                };
                // Target in place of the old Aim and Aim range; a change goes through the library edit (undo works).
                foreach (var row in TargetRows.Rows(new TargetRows.Access
                {
                    Prefix = prefix,
                    Owner = TargetOwner.Step,
                    Get = () => Current()?.Target,
                    Set = target => Edit(s => s.SetTarget(target ?? TargetSpec.Cursor)),
                    Blocked = ReadOnlyReason,
                })) yield return row;
                yield return new ValueNumber("Wait for ready", () => Current()?.TimeoutMs ?? 1000f, v => Set("timeoutMs", JsonValue.Create(v)), 0f, 5000f, 100f, " ms")
                {
                    Key = prefix + "timeout",
                };
                if (step.Mode == "channel")
                {
                    yield return new HintRow(() => "Channel while these pass (and at most the time above):") { Key = prefix + "until.hint" };
                    var until = new List<MenuRow>();
                    RuleTreeRows.Add(until, TreeFor($"until{index + 1}", setId, macroId, m => m.Actions.ElementAtOrDefault(index)?.Until, () => LiveUntil(setId, macroId, index)));
                    foreach (var row in until) yield return row;
                }
                break;
            case "drink":
                yield return new ValueNumber("Wait for a drink", () => Current()?.TimeoutMs ?? 1000f, v => Set("timeoutMs", JsonValue.Create(v)), 0f, 5000f, 100f, " ms")
                {
                    Key = prefix + "timeout",
                };
                yield return new HintRow(() => "The game also drinks automatically: its Auto Drink is on.")
                {
                    Key = prefix + "autodrink",
                    VisibleWhen = () => PlayerModel.GameAutoDrink == true,
                };
                break;
            case "wait":
                yield return new ValueNumber("Wait", () => Current()?.Ms ?? 500f, v => Set("ms", JsonValue.Create(v)), 50f, 10000f, 50f, " ms") { Key = prefix + "ms" };
                break;
            case "waitUntil":
                var rows = new List<MenuRow>();
                RuleTreeRows.Add(rows, TreeFor($"until{index + 1}", setId, macroId, m => m.Actions.ElementAtOrDefault(index)?.Until, () => LiveUntil(setId, macroId, index)));
                foreach (var row in rows) yield return row;
                yield return new ValueNumber("Time-out", () => Current()?.TimeoutMs ?? 2000f, v => Set("timeoutMs", JsonValue.Create(v)), 100f, 10000f, 100f, " ms")
                {
                    Key = prefix + "timeout",
                };
                yield return new ValueChoice("At the time-out", () => Array.IndexOf(MacroStep.OnTimeouts, Current()?.OnTimeout ?? "stop"), i =>
                {
                    if (i >= 0 && i < MacroStep.OnTimeouts.Length) Set("onTimeout", JsonValue.Create(MacroStep.OnTimeouts[i]));
                }, () => s_timeoutLabels) { Key = prefix + "ontimeout", Width = 140f };
                break;
            case "message":
                yield return new ValueTextField("Text", () => Current()?.Message ?? string.Empty, text => Set("text", JsonValue.Create(text)), MacroStep.MaxMessage)
                {
                    Key = prefix + "text",
                };
                break;
        }
        yield return new ActionButton("Move up", "macros.step.up") { Key = prefix + "up", OnClick = () => MoveStep(setId, macroId, index, -1), EnabledWhen = () => index > 0 };
        yield return new ActionButton("Move down", "macros.step.down")
        {
            Key = prefix + "down",
            OnClick = () => MoveStep(setId, macroId, index, 1),
            EnabledWhen = () => index + 1 < (CurrentMacro(setId, macroId)?.Actions.Count ?? 0),
        };
    }

    private static void MoveStep(string setId, string macroId, int index, int direction)
    {
        bool moved = false;
        EditMacro(setId, macroId, m =>
        {
            int target = index + direction;
            if (target < 0 || target >= m.Actions.Count) return;
            (m.Actions[index], m.Actions[target]) = (m.Actions[target], m.Actions[index]);
            moved = true;
        });
        if (moved) s_openLine = "step/" + (index + direction + 1);
    }
}
