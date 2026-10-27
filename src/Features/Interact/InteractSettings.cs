using EpochAssist.Core;

namespace EpochAssist.Features;

internal static class InteractSettings
{
    // Stored ids are a contract. Renaming or retyping one raises the version and adds a migration for it.
    public static readonly SettingsGroup Group = new("EpochAssist_Interact", "Proximity Interact", version: 1) { ActionKey = "interact" };

    public static readonly Section Main = Group.Section("main", "Proximity Interact");
    // Off until the player turns it on. The quick label offers it on the HUD panel.
    public static readonly Toggle Enabled = new(Main, "Enabled", "Proximity interact", false)
    {
        QuickLabel = "Interact", Help = "Uses what you walk up to, from where you stand.",
    };
    public static readonly Hotkey HoldKey = new(Main, "HoldKey", "Hold key", string.Empty, HotkeyKind.Hold, HotkeyScope.Gameplay)
    {
        Clearable = true, Help = "Pause while held, or use only while held.",
    };
    public const string HoldPauses = "pause";
    public const string HoldUses = "use";
    public static readonly Choice<string> HoldMode = new(Main, "HoldMode", "While held", HoldPauses, (HoldPauses, "Pause"), (HoldUses, "Use"))
    {
        WithKeybinds = true, VisibleWhen = () => HoldKey.Value.Length > 0, ShownWhen = "a hold key is set",
        Help = "Pause: nothing while held. Use: only while held.",
    };

    public static readonly Section Use = Group.Section("use", "Use");
    public static readonly Toggle Shrines = new(Use, "Shrines", "Shrines", false)
    {
        Help = "Activates shrines.",
    };
    public static readonly Toggle Chests = new(Use, "Chests", "Chests", false)
    {
        Help = "Opens chests, caches and reward chests.",
    };
    public static readonly Toggle RunePrisons = new(Use, "RunePrisons", "Rune prisons", false)
    {
        Help = "Frees the Exiled Mage to fight.",
    };
    public static readonly Toggle Forges = new(Use, "Forges", "Forges", false)
    {
        Help = "Starts the echo's forge event.",
    };

    public static readonly Section Pickup = Group.Section("pickup", "Pick up");
    public static readonly Toggle Keys = new(Pickup, "Keys", "Keys", false)
    {
        Help = "Dungeon, arena and other keys.",
    };
    public static readonly Toggle Materials = new(Pickup, "Materials", "Materials", false)
    {
        Help = "Affix shards, runes and glyphs.",
    };
    public static readonly Toggle FilterShown = new(Pickup, "FilterShown", "Loot filter items", false)
    {
        Help = "What your filter shows. Materials and keys have their own switches.",
    };

    public static readonly Section Pace = Group.Section("reach", "Reach and pace");
    // Up to the farthest reach the game allows anything (item pick-up, 5.6 m); each kind stays under its own.
    public static readonly Number Reach = new(Pace, "Reach", "Reach", 5.6f, 0.5f, 5.6f)
    {
        Step = 0.1f, Unit = " m", Slider = true, Help = "Never farther than the game allows.",
    };
    public static readonly Number MinWait = new(Pace, "MinWait", "Wait between uses", 0.25f, 0.15f, 2f)
    {
        Step = 0.05f, Unit = " s", Help = "Plus a little at random.",
    };
    public const string Everywhere = "everywhere";
    public const string NotInTown = "not-town";
    public static readonly Choice<string> Where = new(Pace, "Where", "Where", Everywhere, (Everywhere, "Everywhere"), (NotInTown, "Not in town"));

    public static readonly Section Feedback = Group.Section("feedback", "Feedback");
    public static readonly Toggle HudCount = new(Feedback, "HudCount", "Show count on the mini HUD", false);
    public static readonly Toggle NotifyEach = new(Feedback, "NotifyEach", "Notify each use", false);

    public static readonly Section Debug = Group.Section("debug", "Debug");
    public static readonly Toggle ShowReach = new(Debug, "ShowReach", "Show reach", false)
    {
        Help = "Rings around you and a mark on what's next.",
    };
    private const string ReachShown = "Show reach is on";
    private static readonly (string, string)[] s_ringSwatches =
    {
        ("Green", "#59FF73"), ("Orange", "#FFBF4D"), ("Cyan", "#33D6FF"), ("Purple", "#B98CFF"), ("Red", "#FF5C5C"), ("White", "#FFFFFF"),
    };
    public static readonly ColorSetting ItemRingColor = new(Debug, "ItemRingColor", "Item reach colour")
    {
        EmptyLabel = "Default", Swatches = s_ringSwatches, VisibleWhen = () => ShowReach.Value, ShownWhen = ReachShown,
        Help = "Keys, materials and loot.",
    };
    public static readonly ColorSetting ObjectRingColor = new(Debug, "ObjectRingColor", "Object reach colour")
    {
        EmptyLabel = "Default", Swatches = s_ringSwatches, VisibleWhen = () => ShowReach.Value, ShownWhen = ReachShown,
        Help = "Shrines, chests, rune prisons and forges.",
    };

    public static void Register() => Group.Register();
}
