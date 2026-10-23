using EpochAssist.Core;
using EpochAssist.Features;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using static UnityEngine.GUI;
using BuildInfo = EpochAssist.Core.BuildInfo;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Ui;

internal static class Menu
{
    private const int WindowId = 0x45417373;
    private static bool s_visible;
    private static Rect s_rect = new(40, 40, 420, 520);
    private static Vector2 s_scroll;
    private static bool s_weightsOpen;
    private static bool s_inputBlocked;
    private static bool s_forceDisableInputBefore;

    // The key entry being rebound, or null.
    private static MelonPreferences_Entry<KeyCode>? s_capturing;
    private static KeyCode[]? s_bindableKeys;

    public static void OnUpdate()
    {
        if (UpdateKeyCapture()) return;

        if (Input.GetKeyDown(Config.MenuKey.Value) || (s_visible && Input.GetKeyDown(KeyCode.Escape)))
        {
            s_visible = !s_visible;
            if (!s_visible) Config.Save();
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
        s_scroll = GUILayout.BeginScrollView(s_scroll);
        DrawAutoCast();
        GUILayout.EndScrollView();

        GUILayout.Space(6f);
        KeyRow("Menu key", Config.MenuKey, clearable: false);
        GUI.DragWindow();
    }

    private static void DrawAutoCast()
    {
        Toggle(S.Enabled, "Skill Auto-Cast");
        if (!S.Enabled.Value) return;

        GUILayout.Label($"Hold {AutoCast.ModifierKeyName} and press a skill's key to toggle auto-cast for that slot.");
        GUILayout.Label("Status: " + AutoCast.StatusText);

        GUILayout.BeginHorizontal();
        GUILayout.Label("Modifier:");
        for (int i = 0; i < AutoCast.ModifierKeys.Length; i++)
        {
            bool selected = S.ModifierKey.Value == AutoCast.ModifierKeys[i];
            if (GUILayout.Toggle(selected, AutoCast.ModifierKeyNames[i], GUI.skin.button) && !selected)
                S.ModifierKey.Value = AutoCast.ModifierKeys[i];
        }
        GUILayout.EndHorizontal();

        KeyRow("Hold to pause", S.HoldToPauseKey, clearable: true);

        Toggle(S.PauseOnZoneChange, "Pause On Zone Change (press any skill to resume)");
        Toggle(S.PauseInNonCombatZones, "Pause In Non-Combat Zones");

        Toggle(S.AimAtClosestEnemy, "Aim At Closest Enemy (cursor if none in range)");
        if (S.AimAtClosestEnemy.Value) Slider(S.AimRange, "Aim Range", 2f, 60f, " m");

        Toggle(S.RequireEnemies, "Only Cast When Enemies Are In Range");
        if (!S.RequireEnemies.Value) return;

        Slider(S.EnemyRange, "Enemy Range", 2f, 60f, " m");
        Toggle(S.ShowRangeRing, "Show Range Ring");
        Slider(S.EnemyMinWeight, "Minimum Enemy Weight", 1f, 50f);
        GUILayout.Label($"In range now: {AutoCast.EnemiesInRange} enemies, weight {AutoCast.EnemyWeightInRange:F0}"
            + (AutoCast.EnemyGateOpen ? "  (casting)" : "  (holding)"));

        s_weightsOpen = GUILayout.Toggle(s_weightsOpen, "Enemy Weights", GUI.skin.button);
        if (s_weightsOpen)
        {
            Slider(S.WeightNormal, "Normal", 0f, 50f);
            Slider(S.WeightMagic, "Magic", 0f, 50f);
            Slider(S.WeightRare, "Rare", 0f, 50f);
            Slider(S.WeightBoss, "Boss", 0f, 50f);
        }
    }

    private static void Toggle(MelonPreferences_Entry<bool> entry, string label)
    {
        bool value = GUILayout.Toggle(entry.Value, label);
        if (value != entry.Value) entry.Value = value;
    }

    // Whole-number slider.
    private static void Slider(MelonPreferences_Entry<float> entry, string label, float min, float max, string unit = "")
    {
        GUILayout.Label($"{label}: {entry.Value:F0}{unit}");
        float value = Mathf.Round(GUILayout.HorizontalSlider(entry.Value, min, max));
        if (value != entry.Value) entry.Value = value;
    }

    private static void KeyRow(string label, MelonPreferences_Entry<KeyCode> entry, bool clearable)
    {
        GUILayout.BeginHorizontal();
        if (s_capturing == entry)
        {
            GUILayout.Label($"{label}: press any key... (Esc to cancel)");
            if (GUILayout.Button("Cancel", GUILayout.Width(70f))) s_capturing = null;
        }
        else
        {
            GUILayout.Label($"{label}: {entry.Value}");
            if (GUILayout.Button("Change", GUILayout.Width(70f))) s_capturing = entry;
            if (clearable && entry.Value != KeyCode.None && GUILayout.Button("Clear", GUILayout.Width(70f)))
                entry.Value = KeyCode.None;
            else if (!clearable && entry.Value != (KeyCode)entry.DefaultValue && GUILayout.Button("Reset", GUILayout.Width(70f)))
                entry.Value = (KeyCode)entry.DefaultValue;
        }
        GUILayout.EndHorizontal();
    }

    // True while a rebind is in progress, so the press isn't also handled as a menu toggle.
    private static bool UpdateKeyCapture()
    {
        if (s_capturing == null) return false;
        if (!s_visible || Input.GetKeyDown(KeyCode.Escape))
        {
            s_capturing = null;
            return true;
        }

        s_bindableKeys ??= Enum.GetValues<KeyCode>()
            .Where(k => k != KeyCode.None && k != KeyCode.Escape && (k < KeyCode.Mouse0 || k > KeyCode.Mouse6) && k < KeyCode.JoystickButton0)
            .ToArray();
        foreach (KeyCode key in s_bindableKeys)
        {
            if (!Input.GetKeyDown(key)) continue;
            // Holding the menu key to pause would toggle the menu.
            if (s_capturing != Config.MenuKey && key == Config.MenuKey.Value) continue;

            s_capturing.Value = key;
            s_capturing = null;
            break;
        }
        return true;
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
