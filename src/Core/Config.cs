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
    public static MelonPreferences_Entry<HudCorner> HudPosition = null!;
    public static MelonPreferences_Entry<float> MenuOpacity = null!;
    public static MelonPreferences_Entry<float> HudOpacity = null!;

    public static void Init()
    {
        var c = MelonPreferences.CreateCategory("EpochAssist", BuildInfo.Name);
        MenuKey = c.CreateEntry("MenuKey", KeyCode.F2, "Menu key");
        UiScale = c.CreateEntry("UiScale", 1f, "Menu scale");
        HudEnabled = c.CreateEntry("HudEnabled", true, "Show the mini HUD");
        HudPosition = c.CreateEntry("HudPosition", HudCorner.TopLeft, "Mini HUD corner");
        MenuOpacity = c.CreateEntry("MenuOpacity", 1f, "Menu background opacity");
        HudOpacity = c.CreateEntry("HudOpacity", 0.85f, "Mini HUD opacity");
    }

    public static void Save() => MelonPreferences.Save();
}
