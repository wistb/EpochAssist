using EpochAssist.Core;

namespace EpochAssist.Ui;

// The menu's keys, the keys section of the General group (Config).
internal static class KeySettings
{
    private static readonly Section Keys = Config.Group.Section("keys", "Keybinds");
    public static readonly Hotkey MenuKey = new(Keys, "MenuKey", "Menu key", "F2", HotkeyKind.Press, HotkeyScope.Global)
    {
        Action = "menu.toggle",
    };
    public static readonly Hotkey ConsoleKey = new(Keys, "ConsoleKey", "Console key", string.Empty, HotkeyKind.Press, HotkeyScope.Global)
    {
        Clearable = true, Action = "console.open", Help = "Opens Settings → Diagnostics → Console, ready to type.",
    };

    // Explicit, so every field is set by the time Declare reads the section, before the group registers.
    static KeySettings() { }

    /// <summary>Declares the section in the General group; Runtime.Init calls it before Config.Init registers it.</summary>
    internal static void Declare()
    {
        _ = Keys;
        // Version 1 stored a KeyCode name, which is already a one-key chord; clamping resets anything else to F2.
        Config.Group.Migration(2, _ =>
        {
            bool valid = Chord.TryParse(MenuKey.Value, HotkeyKind.Press, out var chord) && !chord.IsEmpty;
            Log.Info($"General MenuKey: '{MenuKey.Value}'{(valid ? string.Empty : " is not a key, so it goes back to F2")}");
        });
    }
}
