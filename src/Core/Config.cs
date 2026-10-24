using MelonLoader;
using UnityEngine;

namespace EpochAssist.Core;

internal enum HudCorner { TopLeft, TopRight, BottomLeft, BottomRight }

// The mod's own settings. Values live in UserData/MelonPreferences.cfg; each feature declares its own group too.
internal static class Config
{
    // Version 2: MenuKey is a Hotkey (chord text) instead of a KeyCode; PanicKey is new.
    public static readonly SettingsGroup Group = new("EpochAssist", "General", version: 2) { ActionKey = "general" };

    private static readonly Section Menu = Group.Section("menu", "Menu");
    public static readonly Hotkey MenuKey = new(Menu, "MenuKey", "Menu key", "F2", HotkeyKind.Press, HotkeyScope.Global)
    {
        Action = "menu.toggle",
    };
    public static readonly Hotkey PanicKey = new(Menu, "PanicKey", "Panic key", string.Empty, HotkeyKind.Press, HotkeyScope.Gameplay)
    {
        Clearable = true, Action = "panic", Help = "Lets go of everything at once and switches all automation off.",
    };
    public static readonly Number UiScale = new(Menu, "UiScale", "UI scale", 1f, 0.75f, 2f)
    {
        Step = 0.05f, Unit = "x", ApplyOnRelease = true, Help = "Size of the menu and the mini HUD.",
    };
    public static readonly Choice<int> Font = new(Menu, "GameFont", "Font", 1, (0, "Unity"), (1, "Pretendard"), (2, "Noto Sans"))
    {
        Help = "Fonts the game ships with.",
    };
    public static readonly Choice<int> FontWeight = new(Menu, "FontWeight", "Text", 1, (0, "Regular"), (1, "Bold"))
    {
        Help = "Weight of labels, buttons and values. Hints stay regular.",
    };
    public static readonly Number MenuOpacity = new(Menu, "MenuOpacity", "Background", 1f, 0.2f, 1f)
    {
        Step = 0.01f, Percent = true, Help = "Menu background opacity; text and switches stay solid.",
    };

    private static readonly Section Hud = Group.Section("hud", "Mini HUD");
    public static readonly Toggle HudEnabled = new(Hud, "HudEnabled", "Show the mini HUD", true)
    {
        Help = "A small status pill while the menu is closed. Click it for quick toggles.",
    };
    // An int, not HudCorner: an entry typed by one of our own enums would not match after a reload.
    public static readonly Choice<int> HudPlacement = new(Hud, "HudCorner", "Corner", 0,
        (0, "Top left"), (1, "Top right"), (2, "Bottom left"), (3, "Bottom right"));
    public static readonly Number HudOpacity = new(Hud, "HudOpacity", "Opacity", 0.85f, 0.2f, 1f)
    {
        Step = 0.01f, Percent = true, Help = "Full opacity while the cursor is over it.",
    };

    public static HudCorner HudPosition => (HudCorner)Math.Clamp(HudPlacement.Value, 0, 3);

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
