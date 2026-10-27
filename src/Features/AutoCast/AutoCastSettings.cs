using System.Text.Json.Nodes;
using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Features;

internal static class AutoCastSettings
{
    // Version 2: HoldToPauseKey is a Hotkey (chord text) instead of a KeyCode. Version 3: the enemy gate moved into
    // GateConditions.
    // The feature grew into Macros; ids, the group id and its version stay, so nothing migrates (labels only).
    public static readonly SettingsGroup Group = new("EpochAssist_AutoCast", "Macros", version: 3) { ActionKey = "autocast" };

    private static readonly Section Main = Group.Section("main", "Macros");
    public static readonly Toggle Enabled = new(Main, "Enabled", "Run macros", false) { QuickLabel = "Macros" };

    public static readonly Section Controls = Group.Section("controls", "Keys");
    public static readonly Choice<KeyCode> ModifierKey = new(Controls, "ModifierKey", "Modifier", KeyCode.LeftControl,
        (KeyCode.LeftControl, "Left Ctrl"), (KeyCode.LeftAlt, "Left Alt"), (KeyCode.LeftShift, "Left Shift"),
        (KeyCode.RightControl, "Right Ctrl"), (KeyCode.RightAlt, "Right Alt"), (KeyCode.RightShift, "Right Shift"))
    {
        Help = "Hold it and press a skill's key to toggle that slot.",
    };
    public static readonly Hotkey HoldToPauseKey = new(Controls, "HoldToPauseKey", "Hold to pause", string.Empty, HotkeyKind.Hold, HotkeyScope.Gameplay)
    {
        Clearable = true, Help = "While held, macros stop.",
    };

    public static readonly Section Pausing = Group.Section("pausing", "Pausing");
    public static readonly Toggle PauseOnZoneChange = new(Pausing, "PauseOnZoneChange", "Pause on zone change", true)
    {
        Help = "Press any skill yourself to resume.",
    };
    public static readonly Toggle PauseInNonCombatZones = new(Pausing, "PauseInNonCombatZones", "Pause in non-combat zones", false);

    public static readonly Section Targeting = Group.Section("targeting", "Aiming");
    // The old aiming switch and its range: kept stored (ids are a contract) and read only while no shared slot target is
    // saved; never shown, so the HUD no longer offers the switch.
    public static readonly Toggle AimAtClosestEnemy = new(Targeting, "AimAtClosestEnemy", "Aim at closest enemy", false)
    {
        Help = "Uses the cursor when nothing is in range.", VisibleWhen = () => false,
    };
    public static readonly Number AimRange = new(Targeting, "AimRange", "Aim range", 20f, 2f, 60f)
    {
        Unit = " m", VisibleWhen = () => false,
    };
    // The shared slot target and each slot's own (per form); while no shared one is saved, the old switch above and its
    // range decide it, so saved settings act as before.
    public static readonly TargetSetting SlotTargets = new(Targeting, "SlotTargets", "Slot target", "{\"v\":1}")
    {
        Help = "Where slot macros aim (it replaces Aim at closest enemy and Aim range); set on the Shared Conditions page, and a slot macro can set its own.",
    };

    private static TargetSpec? s_oldShared;
    private static (bool On, float Range) s_oldSharedFrom;

    /// <summary>
    /// The shared slot target: the saved one, else from the old switch (off: at cursor; on: nearest within Aim range,
    /// else at cursor).
    /// </summary>
    public static TargetSpec SharedSlotTarget
    {
        get
        {
            if (SlotTargets.Shared is { } shared) return shared;
            var from = (AimAtClosestEnemy.Value, AimRange.Value);
            if (s_oldShared == null || from != s_oldSharedFrom)
            {
                s_oldSharedFrom = from;
                s_oldShared = from.Item1 ? TargetSpec.OldNearest(from.Item2) : TargetSpec.Cursor;
            }
            return s_oldShared;
        }
    }

    /// <summary>A slot's (0-based) target in a form: its own, else the shared one.</summary>
    public static TargetSpec EffectiveSlotTarget(int formKey, int slot) => SlotTargets.Slot(AutoCast.SlotKey(formKey, slot)) ?? SharedSlotTarget;

    // Version 3: the enemy gate is the first entry of the every-slot condition list (GateConditions); RequireEnemies,
    // EnemyRange and EnemyMinWeight are left in the files and read only by the step.
    public static readonly Section Gate = Group.Section("gate", "Shared Conditions");
    public const string EveryList = "every";
    public const string GateEntryId = "gate";
    private const string GateDefault =
        "{\"v\":1,\"lists\":{\"every\":{\"all\":[{\"id\":\"gate\",\"label\":\"Enemy gate\",\"kind\":\"enemies.weight\",\"args\":{\"range\":15,\"weight\":1},\"on\":false,\"trueWhenUnknown\":true}]}}}";
    public static readonly ConditionSetting GateConditions = new(Gate, "GateConditions", "Shared Conditions", GateDefault)
    {
        Help = "Checked for every slot macro before its own conditions.",
        // The old gate switch's quick toggle and action, now the gate entry's switch.
        Quick = { (EveryList, GateEntryId, "Enemy gate", SettingsRegistry.ToggleAction(Group, "RequireEnemies")) },
        KeptEntries = { (EveryList, GateEntryId) },
    };
    private const string GateOn = "the enemy gate is on";
    public static readonly Toggle ShowRangeRing = new(Gate, "ShowRangeRing", "Show range ring", false)
    {
        QuickLabel = "Show range ring", VisibleWhen = () => GateEntry() is { On: true }, ShownWhen = GateOn,
    };
    public static readonly Number WeightNormal = Weight("WeightNormal", "Normal", 1f);
    public static readonly Number WeightMagic = Weight("WeightMagic", "Magic", 2f);
    public static readonly Number WeightRare = Weight("WeightRare", "Rare", 5f);
    public static readonly Number WeightBoss = Weight("WeightBoss", "Boss", 20f);

    // Keyed "<form>/<slot>": form -1 is human, else the transform id; slot 1–5.
    public static readonly Section Slots = Group.Section("slots", "Slot conditions");
    public static readonly ConditionSetting SlotConditions = new(Slots, "SlotConditions", "Slot conditions", "{\"v\":1,\"lists\":{}}");

    // Named sets of macros per class (contracts/macro-format.md); the Slots set isn't stored here.
    public static readonly Section Library = Group.Section("library", "Macro library");
    public static readonly MacroLibrarySetting LibraryDoc = new(Library, "Library", "Macro library", MacroDoc.DefaultText)
    {
        Help = "Named sets of macros for each class.",
    };

    public static readonly Section Debug = Group.Section("debug", "Debug");
    public static readonly Toggle DebugConditions = new(Debug, "DebugConditions", "Debug conditions", false)
    {
        Help = "Shows each enabled macro's state and first failing line on screen.",
    };
    public static readonly Toggle DebugRanges = new(Debug, "DebugRanges", "Debug ranges", false)
    {
        Help = "Draws the ranges enemy lines count, green while they pass.",
    };
    public static readonly Toggle DebugNotifications = new(Debug, "DebugNotifications", "Debug notifications", false)
    {
        Help = "Each macro that starts shows a notification: the line that set it off, with its value, and its first step.",
    };

    /// <summary>The gate entry of the every-slot list, or null when the list can't be read here.</summary>
    public static ConditionEntry? GateEntry()
    {
        var list = GateConditions.List(EveryList);
        int index = list.IndexOf(GateEntryId);
        return index >= 0 ? list.Entries[index] : null;
    }

    /// <summary>
    /// The gate entry made from the version 2 values (RequireEnemies, EnemyRange, EnemyMinWeight): missing or unreadable
    /// → false, 15, 1; out of range → clamped and snapped as the old settings did.
    /// </summary>
    public static JsonObject GateEntryFrom(Func<string, JsonNode?> read)
    {
        bool on = read("RequireEnemies") is JsonValue r && r.TryGetValue(out bool b) && b;
        float range = OldNumber(read("EnemyRange"), 15f, 2f, 60f);
        float weight = OldNumber(read("EnemyMinWeight"), 1f, 1f, 50f);
        return new JsonObject
        {
            ["id"] = GateEntryId, ["label"] = "Enemy gate", ["kind"] = "enemies.weight",
            ["args"] = new JsonObject { ["range"] = range, ["weight"] = weight },
            ["on"] = on, ["trueWhenUnknown"] = true,
        };
    }

    private static float OldNumber(JsonNode? node, float fallback, float min, float max)
    {
        double value = node is JsonValue v && (v.TryGetValue(out double d) || (v.TryGetValue(out float f) && (d = f) == f)) ? d : double.NaN;
        if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
        return Mathf.Clamp(Mathf.Round((float)value), min, max);
    }

    private static JsonObject GateDocument(JsonObject entry) =>
        new() { ["v"] = 1, ["lists"] = new JsonObject { [EveryList] = new JsonObject { ["all"] = new JsonArray(entry) } } };

    public static void Register()
    {
        // Version 1 stored KeyCode names, with "None" for unset; any other name is already a one-key chord.
        Group.Migration(2, _ =>
        {
            if (HoldToPauseKey.Value.Equals("None", StringComparison.OrdinalIgnoreCase)) HoldToPauseKey.Value = string.Empty;
            Log.Info($"Autocast HoldToPauseKey: '{HoldToPauseKey.Value}'");
        });
        // The old values come from raw entries with their old defaults (creating one loads its stored value); a fresh
        // install gets the default document.
        Group.Migration(3, category =>
        {
            var require = Config.Entry(category, "RequireEnemies", false);
            var range = Config.Entry(category, "EnemyRange", 15f);
            var weight = Config.Entry(category, "EnemyMinWeight", 1f);
            var entry = GateEntryFrom(id => id switch
            {
                "RequireEnemies" => JsonValue.Create(require.Value),
                "EnemyRange" => JsonValue.Create(range.Value),
                "EnemyMinWeight" => JsonValue.Create(weight.Value),
                _ => null,
            });
            GateConditions.GlobalValue = GateDocument(entry).ToJsonString();
            Log.Info($"Autocast gate: on {entry["on"]}, range {entry["args"]!["range"]}, weight {entry["args"]!["weight"]}");
        });
        // Profiles and exports: the same entry from the group's stored values.
        Group.StoredMigration(3, values =>
        {
            var entry = GateEntryFrom(id => values.TryGetPropertyValue(id, out var node) ? node : null);
            values["GateConditions"] = GateDocument(entry);
        });
        Group.Register();
    }

    private static Number Weight(string id, string label, float defaultValue) => new(Gate, id, label, defaultValue, 0f, 100f)
    {
        Fold = "Enemy weights", Help = "How much each enemy counts toward an enemy weight condition.",
    };
}
