using MelonLoader;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>A place on the screen: Horizontal -1 left, 0 centre, 1 right; Vertical -1 top, 0 middle, 1 bottom. Runtime only.</summary>
internal readonly record struct ScreenSpot(int Horizontal, int Vertical)
{
    /// <summary>A rect of the given size at this spot, kept the margin away from the screen edges it sits against.</summary>
    public Rect Place(Vector2 size, float screenWidth, float screenHeight, float margin) => new(
        Horizontal < 0 ? margin : Horizontal > 0 ? screenWidth - margin - size.x : (screenWidth - size.x) / 2f,
        Vertical < 0 ? margin : Vertical > 0 ? screenHeight - margin - size.y : (screenHeight - size.y) / 2f,
        size.x, size.y);
}

// The mod's own settings. Values live in UserData/MelonPreferences.cfg; each feature declares its own group too.
internal static class Config
{
    // Version 2: MenuKey is a Hotkey (chord text) instead of a KeyCode; PanicKey is new.
    public static readonly SettingsGroup Group = new("EpochAssist", "General", version: 2) { ActionKey = "general" };

    // Each section is a tab on the Settings page. Setting ids, not sections, are what is stored.
    private static readonly Section Keys = Group.Section("keys", "Keys");
    public static readonly Hotkey MenuKey = new(Keys, "MenuKey", "Menu key", "F2", HotkeyKind.Press, HotkeyScope.Global)
    {
        Action = "menu.toggle",
    };
    public static readonly Hotkey PanicKey = new(Keys, "PanicKey", "Panic key", string.Empty, HotkeyKind.Press, HotkeyScope.Gameplay)
    {
        Clearable = true, Action = "panic", Help = "Lets go of everything at once and switches all automation off.",
    };

    private static readonly Section Look = Group.Section("appearance", "Appearance");
    public static readonly Pick Theme = new(Look, "Theme", "Theme", "Slate")
    {
        Help = "Built-in looks, plus your own from UserData/EpochAssist/themes.",
    };
    public static readonly Choice<int> Font = new(Look, "GameFont", "Font", 1, (0, "Unity"), (1, "Pretendard"), (2, "Noto Sans"))
    {
        Help = "Fonts the game ships with.",
    };
    public static readonly Choice<int> FontWeight = new(Look, "FontWeight", "Text", 1, (0, "Regular"), (1, "Bold"))
    {
        Help = "Weight of labels, buttons and values. Hints stay regular.",
    };
    public static readonly Number Corners = new(Look, "Corners", "Corners", 1f, 0f, 2f)
    {
        Step = 0.05f, Percent = true, ApplyOnRelease = true,
        Help = "How round corners are: 100% is the theme's own, 0% square.",
    };
    public static readonly Number UiScale = new(Look, "UiScale", "UI scale", 1f, 0.75f, 2f)
    {
        Step = 0.05f, Unit = "x", ApplyOnRelease = true, Help = "Size of the menu and the mini HUD.",
    };
    public static readonly Number MenuOpacity = new(Look, "MenuOpacity", "Background", 1f, 0.2f, 1f)
    {
        Step = 0.01f, Percent = true, Help = "Menu background opacity; text and switches stay solid.",
    };

    public static readonly Section Hud = Group.Section("hud", "Mini HUD");
    public static readonly Toggle HudEnabled = new(Hud, "HudEnabled", "Show the mini HUD", true)
    {
        Help = "A small status pill while the menu is closed. Click it for quick toggles.",
    };
    // An int, not an enum of ours: an entry typed by one of our own enums would not match after a reload. The stored
    // numbers are the four corners (0-3, as before), then the edge centres (4-7); the options are in compass order.
    public static readonly Choice<int> HudPlacement = new(Hud, "HudCorner", "Position", 0,
        (0, "Top left"), (4, "Top"), (1, "Top right"), (6, "Left"), (7, "Right"), (2, "Bottom left"), (5, "Bottom"), (3, "Bottom right"))
    {
        Cells = new[] { 0, 1, 2, 3, -1, 4, 5, 6, 7 },
    };
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
    public static readonly Choice<int> ToastPlacement = new(Hud, "ToastPosition", "Notifications", 8,
        (0, "Top left"), (4, "Top"), (1, "Top right"), (6, "Left"), (8, "Next to HUD"), (7, "Right"), (2, "Bottom left"), (5, "Bottom"), (3, "Bottom right"))
    {
        Cells = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 },
        Help = "Where notifications show. They follow the mini HUD's switch and opacity.",
    };

    private static readonly ScreenSpot[] s_spots =
    {
        new(-1, -1), new(1, -1), new(-1, 1), new(1, 1), new(0, -1), new(0, 1), new(-1, 0), new(1, 0),
    };

    public static ScreenSpot HudSpot => s_spots[Math.Clamp(HudPlacement.Value, 0, s_spots.Length - 1)];

    /// <summary>Where notifications go, or null to keep them next to the mini HUD.</summary>
    public static ScreenSpot? ToastSpot => ToastPlacement.Value >= 0 && ToastPlacement.Value < s_spots.Length ? s_spots[ToastPlacement.Value] : null;

    public static void Init()
    {
        // Version 1 stored a KeyCode name, which is already a one-key chord; clamping resets anything else to F2.
        Group.Migration(2, _ =>
        {
            bool valid = Chord.TryParse(MenuKey.Value, HotkeyKind.Press, out var chord) && !chord.IsEmpty;
            Log.Info($"General MenuKey: '{MenuKey.Value}'{(valid ? string.Empty : " is not a key, so it goes back to F2")}");
        });
        Group.Register();
    }

    private const float SaveDelaySeconds = 1f;
    private static float s_dirtySince = -1f;

    public static void Save()
    {
        s_dirtySince = -1f;
        MelonPreferences.Save();
    }

    /// <summary>A setting changed; it is written shortly after, so changes made with the menu closed persist too.</summary>
    public static void MarkDirty()
    {
        if (s_dirtySince < 0f) s_dirtySince = UnityEngine.Time.unscaledTime;
    }

    public static void SaveIfDirty()
    {
        if (s_dirtySince >= 0f && UnityEngine.Time.unscaledTime - s_dirtySince >= SaveDelaySeconds) Save();
    }

    // Get-or-create, so registering again (a restart or a reload) reuses what is already there.
    public static MelonPreferences_Category Category(string id, string displayName) =>
        MelonPreferences.GetCategory(id) ?? MelonPreferences.CreateCategory(id, displayName);

    public static MelonPreferences_Entry<T> Entry<T>(MelonPreferences_Category category, string id, T defaultValue, string? description = null) =>
        category.GetEntry<T>(id) ?? category.CreateEntry(id, defaultValue, description: description);
}
