using MelonLoader;
using UnityEngine;

namespace EpochAssist.Core;

// Settings live in UserData/MelonPreferences.cfg under [EpochAssist].
internal static class Config
{
    private static MelonPreferences_Category? s_category;

    public static MelonPreferences_Entry<KeyCode> MenuKey = null!;

    public static void Init()
    {
        s_category = MelonPreferences.CreateCategory("EpochAssist", BuildInfo.Name);
        MenuKey = s_category.CreateEntry("MenuKey", KeyCode.F9, "Menu key");
    }

    public static void Save() => s_category?.SaveToFile(false);
}
