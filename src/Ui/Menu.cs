using EpochAssist.Core;
using Il2Cpp;
using UnityEngine;
using static UnityEngine.GUI;

namespace EpochAssist.Ui;

internal static class Menu
{
    private const int WindowId = 0x45417373;
    private static bool s_visible;
    private static Rect s_rect = new(40, 40, 360, 420);
    private static bool s_inputBlocked;
    private static bool s_forceDisableInputBefore;

    public static void OnUpdate()
    {
        if (Input.GetKeyDown(Config.MenuKey.Value))
        {
            s_visible = !s_visible;
            if (!s_visible) Config.Save();
        }
        else if (s_visible && Input.GetKeyDown(KeyCode.Escape))
        {
            s_visible = false;
            Config.Save();
        }
        BlockGameInput(s_visible);
    }

    public static void OnGUI()
    {
        if (!s_visible) return;
        s_rect = GUI.Window(WindowId, s_rect, (WindowFunction)DrawWindow, $"{BuildInfo.Name} {BuildInfo.Version}");
    }

    private static void DrawWindow(int id)
    {
        GUILayout.Label($"Menu key: {Config.MenuKey.Value}");
        GUILayout.Label("Features coming soon.");
        GUI.DragWindow();
    }

    // forceDisableInput is the switch the game honours while the menu is open.
    private static void BlockGameInput(bool block)
    {
        if (block == s_inputBlocked) return;
        var input = EpochInputManager.instance;
        if (input == null) return;
        if (block)
        {
            s_forceDisableInputBefore = input.forceDisableInput;
            input.forceDisableInput = true;
        }
        else
        {
            input.forceDisableInput = s_forceDisableInputBefore;
        }
        s_inputBlocked = block;
    }
}
