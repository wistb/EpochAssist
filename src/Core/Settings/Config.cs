using MelonLoader;

namespace EpochAssist.Core;

// The mod's own settings, the General group. Values live in UserData/MelonPreferences.cfg; each feature declares its own
// group too.
internal static class Config
{
    // Version 2: MenuKey is a Hotkey (chord text) instead of a KeyCode (KeySettings migrates it).
    // Each section is a tab on the Settings page, in this order, wherever it is declared: the UI layer declares hud
    // (HudSettings), appearance (LookSettings) and keys (KeySettings); the core declares sessions (SessionSettings).
    // Setting ids, not sections, are what is stored.
    public static readonly SettingsGroup Group = new("EpochAssist", "General", version: 2)
    {
        ActionKey = "general",
        SectionOrder = new[] { "hud", "appearance", "keys", "sessions", "diagnostics" },
    };

    // Also drawn as a card on the Diagnostics page.
    public static readonly Section Diagnostics = Group.Section("diagnostics", "Diagnostics");
    public static readonly Choice<int> LogLevel = new(Diagnostics, "LogLevel", "Log level", Log.InfoLevel,
        (Log.ErrorLevel, "Error"), (Log.WarningLevel, "Warning"), (Log.InfoLevel, "Info"), (Log.DebugLevel, "Debug"))
    {
        Help = "How much the mod writes to the log. Debug is for bug reports.",
    };

    public static void Init()
    {
        Group.Register();
        Log.Level = LogLevel.Value;
    }

    private const float SaveDelaySeconds = 1f;
    private static Job? s_save;

    public static void Save()
    {
        s_save?.Cancel();
        s_save = null;
        MelonPreferences.Save();
    }

    /// <summary>A setting changed; it is written shortly after (a host job, so changes made with the menu closed, or
    /// while features are stopped, persist too).</summary>
    public static void MarkDirty()
    {
        if (s_save is not { Pending: true }) s_save = Scheduler.HostAfter("config save", SaveDelaySeconds, Save);
    }

    // Get-or-create, so registering again (a restart or a reload) reuses what is already there.
    public static MelonPreferences_Category Category(string id, string displayName) =>
        MelonPreferences.GetCategory(id) ?? MelonPreferences.CreateCategory(id, displayName);

    public static MelonPreferences_Entry<T> Entry<T>(MelonPreferences_Category category, string id, T defaultValue, string? description = null) =>
        category.GetEntry<T>(id) ?? category.CreateEntry(id, defaultValue, description: description);
}
