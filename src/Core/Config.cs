using MelonLoader;
using UnityEngine;

namespace EpochAssist.Core;

internal enum HudCorner { TopLeft, TopRight, BottomLeft, BottomRight }

// Settings live in UserData/MelonPreferences.cfg; each feature registers its own category.
internal static class Config
{
    public static MelonPreferences_Entry<KeyCode> MenuKey = null!;
    public static MelonPreferences_Entry<float> UiScale = null!;
    public static MelonPreferences_Entry<bool> HudEnabled = null!;
    public static MelonPreferences_Entry<int> HudCornerIndex = null!;
    public static MelonPreferences_Entry<float> MenuOpacity = null!;
    public static MelonPreferences_Entry<float> HudOpacity = null!;

    public static HudCorner HudPosition
    {
        get => (HudCorner)Mathf.Clamp(HudCornerIndex.Value, 0, 3);
        set => HudCornerIndex.Value = (int)value;
    }

    public static void Init()
    {
        var c = Category("EpochAssist", BuildInfo.Name);
        MenuKey = Entry(c, "MenuKey", KeyCode.F2, "Menu key");
        UiScale = Entry(c, "UiScale", 1f, "Menu scale");
        HudEnabled = Entry(c, "HudEnabled", true, "Show the mini HUD");
        // An int, not HudCorner: an entry typed by one of our own enums would not match after a reload.
        HudCornerIndex = Entry(c, "HudCorner", 0, "Mini HUD corner: 0 top left, 1 top right, 2 bottom left, 3 bottom right");
        MenuOpacity = Entry(c, "MenuOpacity", 1f, "Menu background opacity");
        HudOpacity = Entry(c, "HudOpacity", 0.85f, "Mini HUD opacity");
    }

    public static void Save() => MelonPreferences.Save();

    // Get-or-create, so registering again (a restart or a reload) reuses what is already there.
    public static MelonPreferences_Category Category(string id, string displayName) =>
        MelonPreferences.GetCategory(id) ?? MelonPreferences.CreateCategory(id, displayName);

    public static MelonPreferences_Entry<T> Entry<T>(MelonPreferences_Category category, string id, T defaultValue, string? description = null) =>
        category.GetEntry<T>(id) ?? category.CreateEntry(id, defaultValue, description: description);
}
