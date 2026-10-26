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
    public static readonly Number HudOpacity = new(Hud, "HudOpacity", "Opacity", 0.85f, 0.2f, 1f)
    {
        Step = 0.01f, Percent = true, Help = "Full opacity while the cursor is over it.",
    };
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
