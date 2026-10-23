using MelonLoader;
using UnityEngine;

namespace EpochAssist.Core;

// Settings live in UserData/MelonPreferences.cfg; each feature registers its own category.
internal static class Config
{
    public static MelonPreferences_Entry<KeyCode> MenuKey = null!;

    public static void Init()
    {
        var c = MelonPreferences.CreateCategory("EpochAssist", BuildInfo.Name);
        MenuKey = c.CreateEntry("MenuKey", KeyCode.F2, "Menu key");
    }

    public static void Save() => MelonPreferences.Save();
}
