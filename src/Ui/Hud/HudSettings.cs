using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>A place on the screen: Horizontal -1 left, 0 centre, 1 right; Vertical -1 top, 0 middle, 1 bottom. Runtime only.</summary>
internal readonly record struct ScreenSpot(int Horizontal, int Vertical)
{
    /// <summary>A rect of the given size at this spot, kept the margin away from the screen edges it sits against.</summary>
    public Rect Place(Vector2 size, float screenWidth, float screenHeight, float margin) => new(
        Horizontal < 0 ? margin : Horizontal > 0 ? screenWidth - margin - size.x : (screenWidth - size.x) / 2f,
        Vertical < 0 ? margin : Vertical > 0 ? screenHeight - margin - size.y : (screenHeight - size.y) / 2f,
        size.x, size.y);
}

// The mini HUD's settings, the hud section of the General group (Config).
internal static class HudSettings
{
    private const string Notifications = "Notifications";
    private const string Sections = "Visible sections";
    private const string QuickToggles = "Quick toggles";

    public static readonly Section Hud = Config.Group.Section("hud", "Mini HUD");
    public static readonly Toggle HudEnabled = new(Hud, "HudEnabled", "Show the mini HUD", true)
    {
        Help = "A small status pill while the menu is closed. Click it for quick toggles.",
    };
    // An int, not an enum of ours: an entry typed by one of our own enums would not match after a reload. The stored
    // numbers are the four corners (0-3, as before), then the edge centres (4-7); the options are in compass order.
    public static readonly Choice<int> HudPlacement = new(Hud, "HudCorner", "Position", 0,
        (0, "Top left"), (4, "Top"), (1, "Top right"), (6, "Left"), (7, "Right"), (2, "Bottom left"), (5, "Bottom"), (3, "Bottom right"));
    public static readonly Number HudOffsetX = new(Hud, "HudOffsetX", "Horizontal offset", 0f, -100f, 100f)
    {
        Unit = " px", Help = "Moves the mini HUD right from its position; below zero, left.",
    };
    public static readonly Number HudOffsetY = new(Hud, "HudOffsetY", "Vertical offset", 0f, -100f, 100f)
    {
        Unit = " px", Help = "Moves the mini HUD down from its position; below zero, up.",
    };
    public static readonly Number HudOpacity = new(Hud, "HudOpacity", "Opacity", 1f, 0.2f, 1f)
    {
        Step = 0.01f, Percent = true, Help = "Full opacity while the cursor is over it.",
    };
    // One switch per pill section, keyed by feature id; a feature that isn't loaded hides its row.
    private static Toggle Section(string featureId, string label, bool shown = true) => new(Hud, "HudShow" + featureId, label, shown)
    {
        Fold = Sections, VisibleWhen = () => FeatureHost.All.Any(f => f.Id == featureId),
    };
    private static readonly Dictionary<string, Toggle> s_sections = new()
    {
        ["macros"] = Section("macros", "Macros"),
        ["interact"] = Section("interact", "Interact", shown: false),
        ["mapreveal"] = Section("mapreveal", "Map", shown: false),
    };

    /// <summary>Whether the feature's section is on the pill; features without a switch always are.</summary>
    public static bool ShowsSection(string featureId) => !s_sections.TryGetValue(featureId, out var toggle) || toggle.Value;

    /// <summary>A section's show switch, or null when it always shows.</summary>
    public static Toggle? SectionToggle(string featureId) => s_sections.TryGetValue(featureId, out var toggle) ? toggle : null;

    // The pill's sections left to right (and row by row): set by dragging on the Mini HUD page.
    public static readonly OrderSetting SectionOrder = new(Hud, "HudOrder", "Section order") { Fold = Sections };

    /// <summary>The session figures' place in the order, when they go in the pill.</summary>
    public const string SessionSection = "session";

    // One switch per quick toggle on the HUD panel, keyed by its action; a quick toggle that isn't loaded hides its row.
    private static Toggle Quick(string id, string actionId, string label) => new(Hud, "HudQuick" + id, label, true)
    {
        Fold = QuickToggles, VisibleWhen = () => SettingsRegistry.QuickToggles.Any(q => q.ActionId == actionId),
    };
    private static readonly Dictionary<string, Toggle> s_quick = new[]
    {
        ("Macros", "setting.toggle.macros.enabled", "Macros"),
        ("EnemyGate", "setting.toggle.macros.enemygate", "Enemy gate"),
        ("Interact", "setting.toggle.interact.enabled", "Interact"),
        ("Map", "setting.toggle.mapreveal.enabled", "Map"),
        ("Overlay", "setting.toggle.planner.enabled", "Planner overlay"),
    }.ToDictionary(q => q.Item2, q => Quick(q.Item1, q.Item2, q.Item3));

    /// <summary>The quick toggles on the HUD panel, in declaration order; one without a switch here always shows.</summary>
    public static IEnumerable<IQuickSwitch> PanelToggles =>
        SettingsRegistry.QuickToggles.Where(q => !s_quick.TryGetValue(q.ActionId, out var toggle) || toggle.Value);

    // 8 keeps notifications next to the mini HUD; 0-7 are the same places as the HUD's.
    public static readonly Choice<int> ToastPlacement = new(Hud, "ToastPosition", "Position", 8,
        (0, "Top left"), (4, "Top"), (1, "Top right"), (6, "Left"), (8, "Next to HUD"), (7, "Right"), (2, "Bottom left"), (5, "Bottom"), (3, "Bottom right"))
    {
        Fold = Notifications, Help = "Where notifications show. They follow the mini HUD's switch and opacity.",
    };
    public static readonly Number ToastDuration = new(Hud, "ToastDuration", "Duration", Toasts.DefaultShowSeconds, 1f, 30f)
    {
        Fold = Notifications, Step = 0.5f, Unit = " s", Help = "How long each notification stays before it fades.",
    };
    public static readonly Number ToastStack = new(Hud, "ToastStack", "At once", Toasts.DefaultMaxShown, 1f, 8f)
    {
        Fold = Notifications, Help = "How many show together; a new one replaces the oldest.",
    };
    public static readonly Choice<int> ToastKinds = new(Hud, "ToastKinds", "Show", 0, (0, "All"), (1, "Warnings only"))
    {
        Fold = Notifications, Help = "Warnings say something stopped or needs a look. Every notification is still written to the log.",
    };
    public static readonly Choice<int> ToastOrder = new(Hud, "ToastOrder", "Order", 0, (0, "Oldest first"), (1, "Newest first"))
    {
        Fold = Notifications, Help = "Newest first puts the latest notification at the top, or nearest the mini HUD.",
    };

    private static readonly ScreenSpot[] s_spots =
    {
        new(-1, -1), new(1, -1), new(-1, 1), new(1, 1), new(0, -1), new(0, 1), new(-1, 0), new(1, 0),
    };

    public static ScreenSpot HudSpot => s_spots[Math.Clamp(HudPlacement.Value, 0, s_spots.Length - 1)];

    /// <summary>Where notifications go, or null to keep them next to the mini HUD.</summary>
    public static ScreenSpot? ToastSpot => ToastPlacement.Value >= 0 && ToastPlacement.Value < s_spots.Length ? s_spots[ToastPlacement.Value] : null;

    // Explicit, so every field is set by the time Declare reads the section, before the group registers.
    static HudSettings() { }

    /// <summary>Declares the section in the General group; Runtime.Init calls it before Config.Init registers it.</summary>
    internal static void Declare()
    {
        _ = Hud;
    }
}
