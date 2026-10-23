using Il2Cpp;

namespace EpochAssist.Core;

// Keeps game input off while the menu is open or the cursor is over the HUD.
internal static class InputGate
{
    public static bool Menu;
    public static bool Hud;

    private static bool s_blocked;
    private static bool s_before;

    /// <summary>Gives input back to the game, whatever was blocking it.</summary>
    public static void Release()
    {
        Menu = false;
        Hud = false;
        Apply();
    }

    public static void Apply()
    {
        bool block = Menu || Hud;
        if (block == s_blocked) return;

        var input = EpochInputManager.instance;
        if (input == null) return;
        // forceDisableInput is the switch the game honours.
        if (block)
        {
            s_before = input.forceDisableInput;
            input.forceDisableInput = true;
        }
        else
        {
            input.forceDisableInput = s_before;
        }
        s_blocked = block;
    }
}
