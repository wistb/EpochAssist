using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Features;

internal static class MacroSettings
{
    public static readonly SettingsGroup Group = new("EpochAssist_Macros", "Macros", version: 1) { ActionKey = "macros", Module = "macros" };

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

    // The shared slot target and each slot's own (per form); with none saved, slot macros aim at the cursor.
    public static readonly Section Targeting = Group.Section("targeting", "Slot targets");
    public static readonly TargetSetting SlotTargets = new(Targeting, "SlotTargets", "Slot target", "{\"v\":1}")
    {
        Help = "Where slot macros aim; set on the Shared Conditions page, and a slot macro can set its own.",
    };

    /// <summary>The range a slot target starts with when it first gets an enemy pick.</summary>
    public const float StartRange = 20f;

    /// <summary>The shared slot target: the saved one, else at cursor.</summary>
    public static TargetSpec SharedSlotTarget => SlotTargets.Shared ?? TargetSpec.Cursor;

    /// <summary>A slot's (0-based) target in a form: its own, else the shared one.</summary>
    public static TargetSpec EffectiveSlotTarget(int formKey, int slot) => SlotTargets.Slot(SlotMacros.SlotKey(formKey, slot)) ?? SharedSlotTarget;

    // The enemy gate is the first entry of the every-slot condition list.
    public static readonly Section Gate = Group.Section("gate", "Shared Conditions");
    public const string EveryList = "every";
    public const string GateEntryId = "gate";
    private const string GateDefault =
        "{\"v\":1,\"lists\":{\"every\":{\"all\":[{\"id\":\"gate\",\"label\":\"Enemy gate\",\"kind\":\"enemies.weight\",\"args\":{\"range\":15,\"weight\":1},\"on\":false,\"trueWhenUnknown\":true}]}}}";
    public static readonly ConditionSetting GateConditions = new(Gate, "GateConditions", "Shared Conditions", GateDefault)
    {
        Help = "Checked for every slot macro before its own conditions.",
        // The gate entry's switch is a HUD quick toggle and an action.
        Quick = { (EveryList, GateEntryId, "Enemy gate", SettingsRegistry.ToggleAction(Group, "EnemyGate")) },
        KeptEntries = { (EveryList, GateEntryId) },
    };
    private const string GateOn = "the enemy gate is on";
    public static readonly Toggle ShowRangeRing = new(Gate, "ShowRangeRing", "Show range ring", false)
    {
        // On the Macros tab's footer with the debug switches, not a quick toggle.
        VisibleWhen = () => GateEntry() is { On: true }, ShownWhen = GateOn, WithDebug = true,
    };
    public static readonly Number WeightNormal = Weight("WeightNormal", "Normal", 1f);
    public static readonly Number WeightMagic = Weight("WeightMagic", "Magic", 2f);
    public static readonly Number WeightRare = Weight("WeightRare", "Rare", 5f);
    public static readonly Number WeightBoss = Weight("WeightBoss", "Boss", 20f);

    // Keyed "<form>/<slot>": form -1 is human, else the transform id; slot 1–5.
    public static readonly Section Slots = Group.Section("slots", "Slot conditions");
    public static readonly ConditionSetting SlotConditions = new(Slots, "SlotConditions", "Slot conditions", "{\"v\":1,\"lists\":{}}");

    // Named conditions, keyed by id; "<id>/ends" is one's ends tree (contracts/named-format.md). Six starters, used by
    // no macro, built from kinds every class has and the default enemy weights; once anything is saved, a deleted one
    // stays gone. Thresholds from game-facts.md.
    public static readonly Section Named = Group.Section("named", "Named conditions");
    private const string NamedDefault = """
        {"v":1,"lists":{
          "worth":{"name":"Worth fighting","all":[{"any":[
            {"kind":"enemies.weight","args":{"range":12,"weight":6,"compare":"atLeast"},"on":true,"trueWhenUnknown":false},
            {"kind":"enemies.rarity","args":{"range":15,"rarity":"rare"},"on":true,"trueWhenUnknown":false}]}]},
          "travel":{"name":"Travelling","all":[
            {"kind":"player.moving","args":{"is":"moving"},"on":true,"trueWhenUnknown":false},
            {"kind":"cursor.distance","args":{"compare":"above","metres":8},"on":true,"trueWhenUnknown":false},
            {"kind":"named.is","args":{"named":"worth","is":"false"},"on":true,"trueWhenUnknown":false}]},
          "pack":{"name":"Pack ahead","all":[
            {"kind":"enemies.weight","args":{"range":10,"weight":6,"compare":"atLeast","centre":"cursor"},"on":true,"trueWhenUnknown":false},
            {"kind":"cursor.distance","args":{"compare":"above","metres":8},"on":true,"trueWhenUnknown":false},
            {"kind":"enemies.weight","args":{"range":8,"weight":1,"compare":"below"},"on":true,"trueWhenUnknown":false}]},
          "engaged":{"name":"Engaged","holdS":3,"all":[{"any":[
            {"kind":"named.is","args":{"named":"worth","is":"true"},"on":true,"trueWhenUnknown":false},
            {"kind":"named.is","args":{"named":"boss","is":"true"},"on":true,"trueWhenUnknown":false}]}]},
          "engaged/ends":{"all":[
            {"kind":"enemies.weight","args":{"range":12,"weight":2,"compare":"below"},"on":true,"trueWhenUnknown":false}]},
          "danger":{"name":"In danger","all":[{"any":[
            {"all":[
              {"kind":"player.health","args":{"compare":"below","percent":35},"on":true,"trueWhenUnknown":false},
              {"kind":"enemies.count","args":{"range":5,"count":3,"compare":"atLeast"},"on":true,"trueWhenUnknown":false}]},
            {"kind":"player.health","args":{"compare":"below","percent":20},"on":true,"trueWhenUnknown":false}]}]},
          "boss":{"name":"Boss nearby","all":[
            {"kind":"enemies.rarity","args":{"range":40,"rarity":"boss"},"on":true,"trueWhenUnknown":false}]}
        }}
        """;
    public static readonly ConditionSetting NamedConditions = new(Named, "NamedConditions", "Named conditions", NamedDefault)
    {
        Help = "Condition trees with a name, used by macro lines such as \"Worth fighting is true\".",
    };

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

    public static void Register() => Group.Register();

    private static Number Weight(string id, string label, float defaultValue) => new(Gate, id, label, defaultValue, 0f, 100f)
    {
        Fold = "Enemy weights", Slider = true, Help = "How much each enemy counts toward an enemy weight condition.",
    };
}
